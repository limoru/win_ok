using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Automation.Core.Frames;
using Automation.Core.Vision;

namespace Automation.Core.DeveloperTools;

public sealed record FrameSampleRequest(
    string SessionId,
    string RunId,
    string ConsumerId,
    long FrameSequence,
    DateTimeOffset CapturedAt,
    BgraFrame Frame,
    OcrFrameResult Ocr,
    string TextSignature);

public enum FrameSampleArchiveOutcome
{
    Saved,
    Duplicate,
    LimitReached
}

public sealed record FrameSampleArchiveResult(
    FrameSampleArchiveOutcome Outcome,
    string ConsumerId,
    string? ImagePath,
    string? JsonPath,
    string? ImageSha256,
    int SavedCount,
    int Limit);

public sealed class FrameSampleArchivedEventArgs(FrameSampleArchiveResult result) : EventArgs
{
    public FrameSampleArchiveResult Result { get; } = result;
}

public sealed class FrameSampleArchiveFailedEventArgs(
    FrameSampleRequest request,
    string message,
    Exception? exception = null) : EventArgs
{
    public FrameSampleRequest Request { get; } = request;
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}

public interface IFrameSampleArchiveSink
{
    bool IsEnabled { get; }

    ValueTask<FrameSampleArchiveResult?> CaptureAsync(
        FrameSampleRequest request,
        CancellationToken cancellationToken = default);
}

public interface IBackgroundFrameSampleArchiveSink : IFrameSampleArchiveSink
{
    bool TryEnqueue(FrameSampleRequest request);
}

/// <summary>
/// Saves the exact frame selected for a frame-bus consumer. Samples are grouped by
/// consumer id, deduplicated by the raw image hash, and stop at the configured limit.
/// </summary>
public sealed class FrameSampleArchiveService : IBackgroundFrameSampleArchiveSink, IDisposable
{
    public const int DefaultLimitPerConsumer = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();
    private readonly Channel<FrameSampleRequest> _archiveQueue;
    private readonly CancellationTokenSource _archiveShutdown = new();
    private readonly Task _archiveWorker;
    private int _isEnabled;
    private int _disposed;

    public FrameSampleArchiveService(
        string outputDirectory,
        int limitPerConsumer = DefaultLimitPerConsumer)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("样本目录不能为空。", nameof(outputDirectory));
        }

        if (limitPerConsumer <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limitPerConsumer));
        }

        OutputDirectory = Path.GetFullPath(outputDirectory);
        LimitPerConsumer = limitPerConsumer;
        _archiveQueue = Channel.CreateBounded<FrameSampleRequest>(
            new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        _archiveWorker = Task.Run(ProcessArchiveQueueAsync);
    }

    public event EventHandler<FrameSampleArchivedEventArgs>? SampleArchived;
    public event EventHandler<FrameSampleArchiveFailedEventArgs>? SampleArchiveFailed;

    public bool IsEnabled
    {
        get => Volatile.Read(ref _isEnabled) != 0;
        set => Volatile.Write(ref _isEnabled, value ? 1 : 0);
    }

    public string OutputDirectory { get; }
    public int LimitPerConsumer { get; }

    public bool TryEnqueue(FrameSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsEnabled)
        {
            return false;
        }

        if (_archiveQueue.Writer.TryWrite(request))
        {
            return true;
        }

        PublishFailed(request, "派发样本队列已满，本帧未归档。");
        return false;
    }

    public ValueTask<FrameSampleArchiveResult?> CaptureAsync(
        FrameSampleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsEnabled)
        {
            return ValueTask.FromResult<FrameSampleArchiveResult?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = CaptureCore(request, cancellationToken);
        PublishArchived(result);
        return ValueTask.FromResult<FrameSampleArchiveResult?>(result);
    }

    private async Task ProcessArchiveQueueAsync()
    {
        try
        {
            await foreach (var request in _archiveQueue.Reader
                               .ReadAllAsync(_archiveShutdown.Token)
                               .ConfigureAwait(false))
            {
                if (!IsEnabled)
                {
                    continue;
                }

                try
                {
                    var result = CaptureCore(request, _archiveShutdown.Token);
                    PublishArchived(result);
                }
                catch (OperationCanceledException) when (_archiveShutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    PublishFailed(request, $"派发样本保存失败：{exception.Message}", exception);
                }
            }
        }
        catch (OperationCanceledException) when (_archiveShutdown.IsCancellationRequested)
        {
        }
    }

    private void PublishArchived(FrameSampleArchiveResult? result)
    {
        if (result is not null && result.Outcome == FrameSampleArchiveOutcome.Saved)
        {
            SampleArchived?.Invoke(this, new FrameSampleArchivedEventArgs(result));
        }
    }

    private void PublishFailed(
        FrameSampleRequest request,
        string message,
        Exception? exception = null)
        => SampleArchiveFailed?.Invoke(
            this,
            new FrameSampleArchiveFailedEventArgs(request, message, exception));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _archiveQueue.Writer.TryComplete();
        try
        {
            _archiveWorker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _archiveShutdown.Cancel();
        _archiveShutdown.Dispose();
    }

    private FrameSampleArchiveResult CaptureCore(
        FrameSampleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConsumerId);

        var consumerDirectoryName = SanitizePathPart(request.ConsumerId);
        var directory = Path.Combine(OutputDirectory, consumerDirectoryName);
        var imageHash = Convert.ToHexString(SHA256.HashData(request.Frame.Pixels));

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            var savedCount = CountSamples(directory);
            if (Directory.EnumerateFiles(directory, $"{imageHash}-*.png")
                .Any(path => File.Exists(Path.ChangeExtension(path, ".json"))))
            {
                return new FrameSampleArchiveResult(
                    FrameSampleArchiveOutcome.Duplicate,
                    request.ConsumerId,
                    null,
                    null,
                    imageHash,
                    savedCount,
                    LimitPerConsumer);
            }

            if (CountSamples(directory) >= LimitPerConsumer)
            {
                return new FrameSampleArchiveResult(
                    FrameSampleArchiveOutcome.LimitReached,
                    request.ConsumerId,
                    null,
                    null,
                    imageHash,
                    savedCount,
                    LimitPerConsumer);
            }

            var timestamp = request.CapturedAt == default
                ? DateTimeOffset.Now
                : request.CapturedAt;
            var session = SanitizePathPart(request.SessionId);
            var stem = $"{imageHash}-{timestamp:yyyyMMdd-HHmmssfff}-{session}-{request.FrameSequence}";
            var imagePath = Path.Combine(directory, stem + ".png");
            var jsonPath = Path.Combine(directory, stem + ".json");
            var temporaryId = Guid.NewGuid().ToString("N");
            var temporaryImagePath = imagePath + $".tmp-{temporaryId}";
            var temporaryJsonPath = jsonPath + $".tmp-{temporaryId}";
            var imageMoved = false;

            var payload = new FrameSampleJson(
                1,
                request.SessionId,
                request.RunId,
                request.ConsumerId,
                request.FrameSequence,
                timestamp,
                DateTimeOffset.Now,
                request.Frame.Width,
                request.Frame.Height,
                Path.GetFileName(imagePath),
                imageHash,
                request.TextSignature,
                new FrameSampleOcrJson(
                    request.Ocr.Text,
                    request.Ocr.Score,
                    request.Ocr.Lines
                        .Select(line => new FrameSampleOcrLineJson(
                            line.Text,
                            line.Score,
                            line.Region.X,
                            line.Region.Y,
                            line.Region.Width,
                            line.Region.Height))
                        .ToArray()));

            try
            {
                FramePngWriter.WriteBgra32(temporaryImagePath, request.Frame);
                File.WriteAllText(
                    temporaryJsonPath,
                    JsonSerializer.Serialize(payload, JsonOptions),
                    Utf8NoBom);
                File.Move(temporaryImagePath, imagePath);
                imageMoved = true;
                File.Move(temporaryJsonPath, jsonPath);
            }
            catch
            {
                TryDelete(temporaryImagePath);
                TryDelete(temporaryJsonPath);
                if (imageMoved)
                {
                    TryDelete(imagePath);
                }

                throw;
            }

            return new FrameSampleArchiveResult(
                FrameSampleArchiveOutcome.Saved,
                request.ConsumerId,
                imagePath,
                jsonPath,
                imageHash,
                savedCount + 1,
                LimitPerConsumer);
        }
    }

    private static int CountSamples(string directory)
        => Directory.EnumerateFiles(directory, "*.png")
            .Count(path => File.Exists(Path.ChangeExtension(path, ".json")));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; the original archive exception is more useful.
        }
    }

    private static string SanitizePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(
            value
                .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character)
                    ? '-'
                    : character)
                .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized.Trim('-');
    }

    private sealed record FrameSampleJson(
        int SchemaVersion,
        string SessionId,
        string RunId,
        string ConsumerId,
        long FrameSequence,
        DateTimeOffset CapturedAt,
        DateTimeOffset ArchivedAt,
        int FrameWidth,
        int FrameHeight,
        string ImageFileName,
        string ImageSha256,
        string TextSignature,
        FrameSampleOcrJson Ocr);

    private sealed record FrameSampleOcrJson(
        string Text,
        double Score,
        IReadOnlyList<FrameSampleOcrLineJson> Lines);

    private sealed record FrameSampleOcrLineJson(
        string Text,
        double Score,
        int X,
        int Y,
        int Width,
        int Height);
}

internal static class FramePngWriter
{
    private static readonly byte[] Signature =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
    ];

    public static void WriteBgra32(string path, BgraFrame frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "图片尺寸必须大于零。");
        }

        var expectedLength = checked(frame.Width * frame.Height * 4);
        if (frame.Pixels.Length < expectedLength)
        {
            throw new ArgumentException("BGRA 像素数据长度不足。", nameof(frame));
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), frame.Height);
        header[8] = 8;
        header[9] = 6;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(stream, "IHDR", header);

        var raw = CreateRawRgba(frame);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        WriteChunk(stream, "IDAT", compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        WriteChunk(stream, "IEND", []);
    }

    private static byte[] CreateRawRgba(BgraFrame frame)
    {
        var rowLength = checked(frame.Width * 4);
        var raw = new byte[checked((rowLength + 1) * frame.Height)];
        var target = 0;
        for (var y = 0; y < frame.Height; y++)
        {
            raw[target++] = 0;
            var source = y * frame.Stride;
            for (var x = 0; x < frame.Width; x++)
            {
                raw[target++] = frame.Pixels[source + 2];
                raw[target++] = frame.Pixels[source + 1];
                raw[target++] = frame.Pixels[source];
                raw[target++] = frame.Pixels[source + 3];
                source += 4;
            }
        }

        return raw;
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            var crc = uint.MaxValue;
            crc = Update(crc, first);
            crc = Update(crc, second);
            return ~crc;
        }

        private static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? 0xEDB88320u ^ (value >> 1)
                        : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }
}