using System.Security.Cryptography;
using System.Text;
using Automation.Core.Targeting;
using Automation.Core.Vision;

namespace Automation.Core.Arbitration;

public readonly record struct PageFingerprint(string Value)
{
    public static PageFingerprint FromOcr(
        OcrFrameResult recognition,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(recognition);
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        var lines = recognition.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => new FingerprintLine(
                Text: Normalize(line.Text),
                XBucket: Math.Clamp((line.Region.X + (line.Region.Width / 2)) * 10 / width, 0, 9),
                YBucket: Math.Clamp((line.Region.Y + (line.Region.Height / 2)) * 10 / height, 0, 9)))
            .Where(line => line.Text.Length > 0)
            .OrderBy(line => line.YBucket)
            .ThenBy(line => line.XBucket)
            .ThenBy(line => line.Text, StringComparer.Ordinal)
            .Select(line => $"{line.YBucket}:{line.XBucket}:{line.Text}")
            .ToArray();

        var canonical = lines.Length == 0
            ? $"empty:{width}x{height}"
            : string.Join('\n', lines);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new PageFingerprint(Convert.ToHexString(hash));
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var alreadyAddedDigitMarker = false;
        foreach (var character in text)
        {
            if (char.IsLetter(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (char.IsDigit(character) && !alreadyAddedDigitMarker)
            {
                builder.Append('#');
                alreadyAddedDigitMarker = true;
            }
        }

        return builder.ToString();
    }

    private readonly record struct FingerprintLine(string Text, int XBucket, int YBucket);
}

public sealed record PageObservation(
    string SessionId,
    long FrameSequence,
    int Width,
    int Height,
    PageFingerprint Fingerprint,
    bool HasCenterConfirmation = false,
    RelativePoint? CenterConfirmationPoint = null,
    DateTimeOffset CapturedAt = default)
{
    public static PageObservation FromOcr(
        string sessionId,
        long frameSequence,
        int width,
        int height,
        OcrFrameResult recognition,
        RelativePoint? centerConfirmationPoint = null,
        DateTimeOffset capturedAt = default)
        => new(
            sessionId,
            frameSequence,
            width,
            height,
            PageFingerprint.FromOcr(recognition, width, height),
            centerConfirmationPoint is not null,
            centerConfirmationPoint,
            capturedAt);
}