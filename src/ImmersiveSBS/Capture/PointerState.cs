namespace ImmersiveSBS.Capture;

/// <summary>
/// The cursor as Desktop Duplication reports it, converted to two BGRA images the renderer can
/// draw in two passes: <see cref="Color"/> with straight alpha, then <see cref="Xor"/> with an
/// inverting blend (result = src·(1−dst) + dst·(1−src), an exact XOR for 0 and 1).
/// </summary>
public sealed class PointerState
{
    private const uint TypeMonochrome = 1;
    private const uint TypeColor = 2;
    private const uint TypeMaskedColor = 4;

    /// <summary>Top-left of the shape in source pixels (the hot spot is already applied).</summary>
    public int X { get; set; }

    public int Y { get; set; }

    public bool Visible { get; set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public byte[] Color { get; private set; } = [];

    public byte[] Xor { get; private set; } = [];

    /// <summary>False when no pixel inverts, so the second pass can be skipped.</summary>
    public bool HasXor { get; private set; }

    /// <summary>Incremented on every shape change; the renderer re-uploads when it differs.</summary>
    public int ShapeVersion { get; private set; }

    /// <summary>Converts a DXGI_OUTDUPL_POINTER_SHAPE_INFO buffer.</summary>
    public void SetShape(uint type, int width, int height, int pitch, ReadOnlySpan<byte> buffer)
    {
        // A monochrome shape stacks the AND mask over the XOR mask, so it is twice as tall.
        if (type == TypeMonochrome) height /= 2;

        var color = new byte[width * height * 4];
        var xor = new byte[width * height * 4];
        bool hasXor = false;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;

                if (type == TypeMonochrome)
                {
                    int bit = 0x80 >> (x & 7);
                    bool and = (buffer[y * pitch + x / 8] & bit) != 0;
                    bool xr = (buffer[(y + height) * pitch + x / 8] & bit) != 0;

                    if (!and)
                    {
                        byte v = xr ? (byte)255 : (byte)0;
                        color[o] = color[o + 1] = color[o + 2] = v;
                        color[o + 3] = 255;
                    }
                    else if (xr)
                    {
                        xor[o] = xor[o + 1] = xor[o + 2] = 255;
                        hasXor = true;
                    }
                }
                else
                {
                    int s = y * pitch + x * 4;
                    byte b = buffer[s], g = buffer[s + 1], r = buffer[s + 2], a = buffer[s + 3];

                    if (type == TypeColor)
                    {
                        color[o] = b; color[o + 1] = g; color[o + 2] = r; color[o + 3] = a;
                    }
                    else if (type == TypeMaskedColor && a == 0)
                    {
                        color[o] = b; color[o + 1] = g; color[o + 2] = r; color[o + 3] = 255;
                    }
                    else if (type == TypeMaskedColor)
                    {
                        xor[o] = b; xor[o + 1] = g; xor[o + 2] = r;
                        hasXor |= (b | g | r) != 0;
                    }
                }
            }
        }

        Width = width;
        Height = height;
        Color = color;
        Xor = xor;
        HasXor = hasXor;
        ShapeVersion++;
    }
}
