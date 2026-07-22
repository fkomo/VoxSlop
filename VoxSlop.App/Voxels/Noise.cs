namespace VoxSlop.App.Voxels;

/// <summary>Hash-based value noise. No dependencies, deterministic for a given seed.</summary>
internal static class Noise
{
    private static float Hash(int x, int z, int seed)
    {
        uint h = (uint)(x * 374761393 + z * 668265263 + seed * 1442695041);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0xFFFFFF;
    }

    private static float ValueNoise(float x, float z, int seed)
    {
        int xi = (int)MathF.Floor(x), zi = (int)MathF.Floor(z);
        float fx = x - xi, fz = z - zi;
        // Smoothstep the interpolants so octaves don't show grid creases.
        fx = fx * fx * (3f - 2f * fx);
        fz = fz * fz * (3f - 2f * fz);

        float a = Hash(xi, zi, seed), b = Hash(xi + 1, zi, seed);
        float c = Hash(xi, zi + 1, seed), d = Hash(xi + 1, zi + 1, seed);
        return float.Lerp(float.Lerp(a, b, fx), float.Lerp(c, d, fx), fz);
    }

    /// <summary>Fractal sum of value noise, returned in roughly [0, 1].</summary>
    public static float Fbm(float x, float z, int seed, int octaves)
    {
        float sum = 0f, amp = 0.5f, norm = 0f, freq = 1f;
        for (int i = 0; i < octaves; i++)
        {
            sum += ValueNoise(x * freq, z * freq, seed + i * 7919) * amp;
            norm += amp;
            amp *= 0.5f;
            freq *= 2f;
        }
        return sum / norm;
    }
}
