namespace Automation.Core.Frames;

public sealed record BgraFrame(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;

    public BgraFrame Crop(int x, int y, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        width = Math.Min(width, Width - x);
        height = Math.Min(height, Height - y);

        var cropped = new byte[width * height * 4];
        var sourceStride = Stride;
        var targetStride = width * 4;
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(
                Pixels,
                ((y + row) * sourceStride) + (x * 4),
                cropped,
                row * targetStride,
                targetStride);
        }

        return new BgraFrame(width, height, cropped);
    }
}