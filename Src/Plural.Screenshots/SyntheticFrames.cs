namespace Plural.Screenshots;

/// <summary>
/// Frames for the video tiles in the screenshots: a coloured gradient with a big "F" (asymmetric, so a wrong rotation shows) drawn upright
/// in display orientation, then stored the way a camera would: rotated back by the track's rotation. 480 x 270 BGRA.
/// </summary>
internal static class SyntheticFrames
{
    public const int SensorWidth = 480;
    public const int SensorHeight = 270;

    public static byte[] Make(int seed, int rotation)
    {
        var quarter = ((rotation / 90) % 4 + 4) % 4;
        int sw = SensorWidth, sh = SensorHeight;
        var dw = quarter % 2 == 0 ? sw : sh;
        var dh = quarter % 2 == 0 ? sh : sw;
        var display = new int[dw * dh];
        var hue = (seed * 67) % 360;
        for (var y = 0; y < dh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                var t = (x / (double)dw + y / (double)dh) / 2;
                var c = Hsv(hue + t * 40, 0.55, 0.35 + 0.45 * (1 - t));
                var (fx, fy) = (x / (double)dw, y / (double)dh);
                if (IsF(fx, fy)) { c = -1; }
                else if ((fx - 0.72) * (fx - 0.72) * 1.0 + (fy - 0.4) * (fy - 0.4) * (dw / (double)dh) * (dw / (double)dh) < 0.018) { c = Hsv(hue, 0.3, 0.9); }
                if (x < 3 || y < 3 || x >= dw - 3 || y >= dh - 3) { c = -1; }
                display[y * dw + x] = c;
            }
        }
        var bytes = new byte[sw * sh * 4];
        for (var sy = 0; sy < sh; sy++)
        {
            for (var sx = 0; sx < sw; sx++)
            {
                var (x, y) = quarter switch
                {
                    1 => (sh - 1 - sy, sx),
                    2 => (sw - 1 - sx, sh - 1 - sy),
                    3 => (sy, sw - 1 - sx),
                    _ => (sx, sy),
                };
                BitConverter.TryWriteBytes(bytes.AsSpan((sy * sw + sx) * 4), display[y * dw + x]);
            }
        }
        return bytes;
    }

    private static bool IsF(double x, double y) =>
        (x is >= 0.14 and <= 0.26 && y is >= 0.14 and <= 0.86)
        || (x is >= 0.14 and <= 0.52 && y is >= 0.14 and <= 0.26)
        || (x is >= 0.14 and <= 0.44 && y is >= 0.44 and <= 0.56);

    private static int Hsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360 / 60;
        var i = (int)h;
        var f = h - i;
        var p = v * (1 - s);
        var q = v * (1 - s * f);
        var t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return (0xFF << 24) | ((int)(r * 255) << 16) | ((int)(g * 255) << 8) | (int)(b * 255);
    }
}
