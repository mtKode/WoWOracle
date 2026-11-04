namespace WowCompanion.Desktop;

/// <summary>
/// Reads Blizzard BLP2 textures (the game's map tiles) into BGRA pixels: palettized, DXT1/3/5 and raw BGRA.
/// Only the full-size image (first mip level) is decoded.
/// </summary>
public static class Blp
{
    public sealed record Image(int Width, int Height, byte[] Bgra);

    public static Image? Decode(byte[] data)
    {
        if (data.Length < 148 || data[0] != 'B' || data[1] != 'L' || data[2] != 'P' || data[3] != '2') return null;
        int compression = data[8], alphaDepth = data[9], alphaType = data[10];
        var width = BitConverter.ToInt32(data, 12);
        var height = BitConverter.ToInt32(data, 16);
        var offset = BitConverter.ToInt32(data, 20);
        var size = BitConverter.ToInt32(data, 84);
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || offset <= 0 || offset + size > data.Length) return null;

        var pixels = new byte[width * height * 4];
        var span = new ReadOnlySpan<byte>(data, offset, size);
        switch (compression)
        {
            case 1: Palettized(data, span, width, height, alphaDepth, pixels); break;
            case 2:
                if (alphaType == 7) Dxt(span, width, height, pixels, 5);
                else if (alphaType == 1 && alphaDepth > 1) Dxt(span, width, height, pixels, 3);
                else Dxt(span, width, height, pixels, 1);
                break;
            case 3:
                span[..Math.Min(span.Length, pixels.Length)].CopyTo(pixels);
                break;
            default: return null;
        }
        return new Image(width, height, pixels);
    }

    private static void Palettized(byte[] data, ReadOnlySpan<byte> span, int width, int height, int alphaDepth, byte[] pixels)
    {
        var count = width * height;
        for (var i = 0; i < count && i < span.Length; i++)
        {
            var p = 148 + span[i] * 4;
            pixels[i * 4] = data[p];
            pixels[i * 4 + 1] = data[p + 1];
            pixels[i * 4 + 2] = data[p + 2];
            pixels[i * 4 + 3] = alphaDepth switch
            {
                1 => (byte)(((span.Length > count + i / 8 ? span[count + i / 8] : 0xFF) >> (i % 8) & 1) * 255),
                4 => (byte)(((span.Length > count + i / 2 ? span[count + i / 2] : 0xFF) >> (i % 2 * 4) & 0xF) * 17),
                8 => span.Length > count + i ? span[count + i] : (byte)255,
                _ => 255,
            };
        }
    }

    /// <param name="format">1, 3 or 5 (DXT1, DXT3, DXT5).</param>
    private static void Dxt(ReadOnlySpan<byte> span, int width, int height, byte[] pixels, int format)
    {
        var blockSize = format == 1 ? 8 : 16;
        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        Span<byte> colors = stackalloc byte[16];
        Span<byte> alphas = stackalloc byte[16];
        Span<byte> alphaTable = stackalloc byte[8];

        for (var by = 0; by < blocksHigh; by++)
        for (var bx = 0; bx < blocksWide; bx++)
        {
            var at = (by * blocksWide + bx) * blockSize;
            if (at + blockSize > span.Length) return;
            var block = span.Slice(at, blockSize);
            var colorBlock = format == 1 ? block : block[8..];

            // Alpha
            alphas.Fill(255);
            if (format == 3)
            {
                for (var i = 0; i < 16; i++)
                    alphas[i] = (byte)(((block[i / 2] >> (i % 2 * 4)) & 0xF) * 17);
            }
            else if (format == 5)
            {
                alphaTable[0] = block[0];
                alphaTable[1] = block[1];
                if (alphaTable[0] > alphaTable[1])
                    for (var i = 1; i < 7; i++) alphaTable[i + 1] = (byte)(((7 - i) * alphaTable[0] + i * alphaTable[1]) / 7);
                else
                {
                    for (var i = 1; i < 5; i++) alphaTable[i + 1] = (byte)(((5 - i) * alphaTable[0] + i * alphaTable[1]) / 5);
                    alphaTable[6] = 0;
                    alphaTable[7] = 255;
                }
                ulong bits = 0;
                for (var i = 0; i < 6; i++) bits |= (ulong)block[2 + i] << (8 * i);
                for (var i = 0; i < 16; i++) alphas[i] = alphaTable[(int)((bits >> (3 * i)) & 7)];
            }

            // Colour
            var c0 = (ushort)(colorBlock[0] | colorBlock[1] << 8);
            var c1 = (ushort)(colorBlock[2] | colorBlock[3] << 8);
            Expand(c0, colors[..4]);
            Expand(c1, colors.Slice(4, 4));
            var fourColors = c0 > c1 || format != 1;
            for (var ch = 0; ch < 3; ch++)
            {
                if (fourColors)
                {
                    colors[8 + ch] = (byte)((2 * colors[ch] + colors[4 + ch]) / 3);
                    colors[12 + ch] = (byte)((colors[ch] + 2 * colors[4 + ch]) / 3);
                }
                else
                {
                    colors[8 + ch] = (byte)((colors[ch] + colors[4 + ch]) / 2);
                    colors[12 + ch] = 0;
                }
            }
            colors[3] = colors[7] = colors[11] = 255;
            colors[15] = (byte)(fourColors ? 255 : 0);

            var indices = (uint)(colorBlock[4] | colorBlock[5] << 8 | colorBlock[6] << 16 | colorBlock[7] << 24);
            for (var i = 0; i < 16; i++)
            {
                var x = bx * 4 + i % 4;
                var y = by * 4 + i / 4;
                if (x >= width || y >= height) continue;
                var c = (int)((indices >> (2 * i)) & 3) * 4;
                var p = (y * width + x) * 4;
                pixels[p] = colors[c];
                pixels[p + 1] = colors[c + 1];
                pixels[p + 2] = colors[c + 2];
                pixels[p + 3] = format == 1 ? colors[c + 3] : alphas[i];
            }
        }
    }

    /// <summary>RGB565 to B, G, R, A bytes.</summary>
    private static void Expand(ushort color, Span<byte> bgra)
    {
        var r = (color >> 11) & 0x1F;
        var g = (color >> 5) & 0x3F;
        var b = color & 0x1F;
        bgra[0] = (byte)((b << 3) | (b >> 2));
        bgra[1] = (byte)((g << 2) | (g >> 4));
        bgra[2] = (byte)((r << 3) | (r >> 2));
        bgra[3] = 255;
    }
}
