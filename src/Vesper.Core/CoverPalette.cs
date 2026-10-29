namespace Vesper.Core;

public static class CoverPalette
{
    // Small decoded BGRA sample; ignore transparent pixels and near-white/black margins.
    public static uint DominantArgb(ReadOnlySpan<byte> pixels)
    {
        var counts = new int[512];
        var red = new long[512]; var green = new long[512]; var blue = new long[512];
        long fallbackR = 0, fallbackG = 0, fallbackB = 0; int total = 0;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 128) continue;
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            fallbackR += r; fallbackG += g; fallbackB += b; total++;
            if (Math.Max(r, Math.Max(g, b)) < 20 || Math.Min(r, Math.Min(g, b)) > 240) continue;
            int bin = (r >> 5) * 64 + (g >> 5) * 8 + (b >> 5);
            counts[bin]++; red[bin] += r; green[bin] += g; blue[bin] += b;
        }
        int best = 0;
        for (int i = 1; i < counts.Length; i++) if (counts[i] > counts[best]) best = i;
        if (counts[best] > 0)
        {
            int centerR = (int)(red[best] / counts[best]), centerG = (int)(green[best] / counts[best]), centerB = (int)(blue[best] / counts[best]);
            long rSum = 0, gSum = 0, bSum = 0; int neighbors = 0;
            // Include neighboring bins so nearly identical colors on a quantization boundary stay together.
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (pixels[i + 3] < 128 || Math.Max(r, Math.Max(g, b)) < 20 || Math.Min(r, Math.Min(g, b)) > 240) continue;
                if (Math.Abs(r - centerR) > 32 || Math.Abs(g - centerG) > 32 || Math.Abs(b - centerB) > 32) continue;
                rSum += r; gSum += g; bSum += b; neighbors++;
            }
            return Pack(rSum, gSum, bSum, neighbors);
        }
        return total > 0 ? Pack(fallbackR, fallbackG, fallbackB, total) : 0xFF48545C;
    }
    private static uint Pack(long r, long g, long b, int count) =>
        0xFF000000u | (uint)(r / count) << 16 | (uint)(g / count) << 8 | (uint)(b / count);
}
