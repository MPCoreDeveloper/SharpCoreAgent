namespace Demo.Imaging;

/// Converts between HSV and RGB, the step a hue rotation needs before it can touch a pixel.
public static class ColorConverter
{
    public static (double Hue, double Saturation, double Value) ToHsv(byte red, byte green, byte blue)
    {
        var maximum = Math.Max(red, Math.Max(green, blue)) / 255.0;
        var minimum = Math.Min(red, Math.Min(green, blue)) / 255.0;
        var delta = maximum - minimum;
        if (delta == 0)
        {
            return (0, 0, maximum);
        }

        var hue = maximum == red / 255.0
            ? 60 * (((green - blue) / 255.0 / delta) % 6)
            : maximum == green / 255.0
                ? 60 * ((((blue - red) / 255.0) / delta) + 2)
                : 60 * ((((red - green) / 255.0) / delta) + 4);

        return (hue < 0 ? hue + 360 : hue, delta / maximum, maximum);
    }

    public static (byte Red, byte Green, byte Blue) ToRgb(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var sector = hue / 60;
        var second = chroma * (1 - Math.Abs((sector % 2) - 1));
        var (red, green, blue) = sector switch
        {
            < 1 => (chroma, second, 0.0),
            < 2 => (second, chroma, 0.0),
            < 3 => (0.0, chroma, second),
            < 4 => (0.0, second, chroma),
            < 5 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second),
        };
        var match = value - chroma;
        return ((byte)((red + match) * 255), (byte)((green + match) * 255), (byte)((blue + match) * 255));
    }
}
