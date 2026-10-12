using System.Buffers.Binary;
using System.IO.Compression;

namespace WreckfestController.Services.Catalogue;

/// <summary>
/// Tells a layout outline from a picture (#260). The game's variant images are mostly white
/// track outlines on a transparent background, drawn over the track's picture; some mods
/// ship pictures instead. An 8-bit RGBA or grey+alpha PNG whose pixels are mostly clear is
/// an outline. Anything else - other PNG kinds, JPEG, WebP, or a PNG this cannot read - is
/// taken for a picture.
/// </summary>
public static class PngTransparency
{
    /// <summary>The share of clear pixels above which an image is an outline, not a picture.</summary>
    public const double OverlayShare = 0.5;

    /// <summary>The largest side read: menu images are at most 1920 wide.</summary>
    private const int MaxSide = 4096;

    public static bool IsOverlay(ReadOnlySpan<byte> png) => ClearShare(png) > OverlayShare;

    /// <summary>The share of pixels with alpha under half, or 0 for anything it does not read.</summary>
    public static double ClearShare(ReadOnlySpan<byte> png)
    {
        try
        {
            return Read(png) ?? 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            return 0;
        }
    }

    private static double? Read(ReadOnlySpan<byte> png)
    {
        // Signature (8), then IHDR: length (4), type (4), width, height, bit depth, colour type.
        if (png.Length < 33 || !png[12..16].SequenceEqual("IHDR"u8))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(png[16..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(png[20..]);
        var bitDepth = png[24];
        var colourType = png[25];
        var interlace = png[28];
        var channels = colourType switch { 6 => 4, 4 => 2, _ => 0 };
        if (channels == 0 || bitDepth != 8 || interlace != 0 || width is < 1 or > MaxSide || height is < 1 or > MaxSide)
        {
            return null;
        }

        using var compressed = new MemoryStream();
        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            if (length < 0 || offset + 12 + length > png.Length)
            {
                return null;
            }

            var type = png.Slice(offset + 4, 4);
            if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(png.Slice(offset + 8, length));
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset += 12 + length;
        }

        var stride = width * channels;
        var expected = (stride + 1) * height;
        var raw = new byte[expected];
        compressed.Position = 0;
        using (var inflate = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            // Reads exactly the image: a stream that holds more is not read past it.
            inflate.ReadExactly(raw);
        }

        var previous = new byte[stride];
        var line = new byte[stride];
        long clear = 0;
        for (var row = 0; row < height; row++)
        {
            var filter = raw[row * (stride + 1)];
            Array.Copy(raw, (row * (stride + 1)) + 1, line, 0, stride);
            Unfilter(filter, line, previous, channels);
            for (var x = channels - 1; x < stride; x += channels)
            {
                if (line[x] < 128)
                {
                    clear++;
                }
            }

            (previous, line) = (line, previous);
        }

        return (double)clear / ((long)width * height);
    }

    private static void Unfilter(byte filter, byte[] line, byte[] previous, int bpp)
    {
        for (var x = 0; x < line.Length; x++)
        {
            int left = x >= bpp ? line[x - bpp] : 0;
            int up = previous[x];
            int upLeft = x >= bpp ? previous[x - bpp] : 0;
            line[x] = filter switch
            {
                0 => line[x],
                1 => (byte)(line[x] + left),
                2 => (byte)(line[x] + up),
                3 => (byte)(line[x] + ((left + up) / 2)),
                4 => (byte)(line[x] + Paeth(left, up, upLeft)),
                _ => throw new InvalidDataException("Unknown PNG filter."),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
