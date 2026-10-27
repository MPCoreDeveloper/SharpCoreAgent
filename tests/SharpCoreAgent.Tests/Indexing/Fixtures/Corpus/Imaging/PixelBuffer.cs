namespace Demo.Imaging;

/// A raw 32-bit RGBA buffer with an explicit stride, the format every resampler works on.
public sealed class PixelBuffer
{
    public PixelBuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new uint[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    public uint[] Pixels { get; }

    public int Stride => Width * sizeof(uint);

    public uint this[int x, int y]
    {
        get => Pixels[(y * Width) + x];
        set => Pixels[(y * Width) + x] = value;
    }
}
