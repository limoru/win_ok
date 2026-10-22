using System.Text;

namespace Automation.MemoryProbe;

public sealed record TextScanOptions(
    string Text,
    long MaxBytes = 256L * 1024 * 1024,
    int MaxMatches = 20,
    int ChunkSize = 1024 * 1024)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Text);
        if (MaxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBytes));
        }

        if (MaxMatches <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMatches));
        }

        if (ChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkSize));
        }
    }
}

public sealed record TextScanMatch(long Address, string Encoding);

public sealed record TextScanResult(
    long BytesRead,
    int RegionsVisited,
    bool Truncated,
    IReadOnlyList<TextScanMatch> Matches,
    string? Error);

public static class ProcessMemoryTextScanner
{
    public static TextScanResult Scan(
        ProcessMemorySession session,
        TextScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var utf8Pattern = Encoding.UTF8.GetBytes(options.Text);
        var utf16Pattern = Encoding.Unicode.GetBytes(options.Text);
        var patterns = new[]
        {
            new ScanPattern("utf-8", utf8Pattern),
            new ScanPattern("utf-16le", utf16Pattern)
        };
        var overlapLength = Math.Max(utf8Pattern.Length, utf16Pattern.Length) - 1;
        if (options.ChunkSize <= overlapLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "ChunkSize must be larger than the longest encoded search pattern.");
        }

        var matches = new List<TextScanMatch>(options.MaxMatches);
        var seenAddresses = new HashSet<long>();
        long bytesRead = 0;
        var regionsVisited = 0;
        var truncated = false;

        foreach (var region in session.EnumerateReadableRegions())
        {
            regionsVisited++;
            var regionOffset = 0L;
            while (regionOffset < region.Size)
            {
                var remainingBudget = options.MaxBytes - bytesRead;
                if (remainingBudget <= 0 || matches.Count >= options.MaxMatches)
                {
                    truncated = true;
                    break;
                }

                var requestedLength = (int)Math.Min(
                    Math.Min(options.ChunkSize, remainingBudget),
                    region.Size - regionOffset);
                if (requestedLength <= 0)
                {
                    break;
                }

                var buffer = new byte[requestedLength];
                var address = region.BaseAddress + regionOffset;
                if (!session.TryRead((nint)address, buffer, out var win32Error))
                {
                    return new TextScanResult(
                        bytesRead,
                        regionsVisited,
                        truncated,
                        matches,
                        $"ReadProcessMemory failed at 0x{address:X}: Win32 error {win32Error}.");
                }

                bytesRead += requestedLength;
                foreach (var pattern in patterns)
                {
                    foreach (var offset in MemoryPatternSearch.FindOffsets(buffer, pattern.Bytes))
                    {
                        var matchAddress = address + offset;
                        if (seenAddresses.Add(matchAddress))
                        {
                            matches.Add(new TextScanMatch(matchAddress, pattern.Encoding));
                            if (matches.Count >= options.MaxMatches)
                            {
                                truncated = true;
                                break;
                            }
                        }
                    }

                    if (matches.Count >= options.MaxMatches)
                    {
                        break;
                    }
                }

                if (matches.Count >= options.MaxMatches)
                {
                    break;
                }

                regionOffset += Math.Max(1, requestedLength - Math.Min(overlapLength, requestedLength - 1));
            }

            if (truncated)
            {
                break;
            }
        }

        return new TextScanResult(bytesRead, regionsVisited, truncated, matches, Error: null);
    }

    private sealed record ScanPattern(string Encoding, byte[] Bytes);
}