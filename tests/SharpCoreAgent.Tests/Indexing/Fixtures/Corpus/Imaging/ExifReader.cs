using System.Buffers.Binary;

namespace Demo.Imaging;

/// Reads the orientation tag out of an EXIF block, so a photo taken sideways is shown upright.
public static class ExifReader
{
    private const ushort OrientationTag = 0x0112;

    public static int Orientation(ReadOnlySpan<byte> exif)
    {
        if (exif.Length < 14 || !exif[..2].SequenceEqual("II"u8) && !exif[..2].SequenceEqual("MM"u8))
        {
            return 1;
        }

        var littleEndian = exif[0] == (byte)'I';
        var count = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(exif[8..10])
            : BinaryPrimitives.ReadUInt16BigEndian(exif[8..10]);

        for (var index = 0; index < count; index++)
        {
            var entry = exif.Slice(10 + (index * 12), 12);
            var tag = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(entry)
                : BinaryPrimitives.ReadUInt16BigEndian(entry);
            if (tag != OrientationTag)
            {
                continue;
            }

            var orientation = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(entry[8..])
                : BinaryPrimitives.ReadUInt16BigEndian(entry[8..]);
            return orientation is >= 1 and <= 8 ? orientation : 1;
        }

        return 1;
    }
}
