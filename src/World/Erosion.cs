using System;
using System.Collections.Generic;

namespace Heimskringla.World;

/// <summary>
/// Erosion on the coarse map. Droplet-based hydraulic erosion (after Hans Theobald Beyer, 2015)
/// produces ravines, ridges and fans of deposited material; thermal erosion slumps overly steep cliffs.
/// Deterministic for a given seed.
/// </summary>
public static class Erosion
{
    public sealed class Settings
    {
        public int Droplets = 600_000;
        public int MaxLifetime = 48;
        public float Inertia = 0.3f;
        public float CapacityFactor = 5f;
        public float MinCapacity = 0.01f;
        public float DepositSpeed = 0.3f;
        public float ErodeSpeed = 0.35f;
        public float EvaporateSpeed = 0.015f;
        public float Gravity = 4f;
        public int Radius = 3;
        /// <summary>Heights are divided by this before erosion, so slope becomes "meters per meter".</summary>
        public float HeightScale = 10f;
    }

    /// <summary>Runs hydraulic erosion on an N×N map (meters). Droplets only start above sea level.</summary>
    public static void Hydraulic(float[] map, int n, int seed, Settings s, Action<float> progress = null)
    {
        float inv = 1f / s.HeightScale;
        for (int i = 0; i < map.Length; i++) map[i] *= inv;

        // Brush: cells within the radius, with weights summing to 1
        var offsets = new List<int>();
        var weights = new List<float>();
        float wsum = 0f;
        for (int dy = -s.Radius; dy <= s.Radius; dy++)
        for (int dx = -s.Radius; dx <= s.Radius; dx++)
        {
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d > s.Radius) continue;
            float w = 1f - d / s.Radius;
            offsets.Add(dy * n + dx);
            weights.Add(w);
            wsum += w;
        }
        var bo = offsets.ToArray();
        var bw = weights.ToArray();
        for (int i = 0; i < bw.Length; i++) bw[i] /= wsum;
        int margin = s.Radius + 1;

        var rng = new Random(seed);
        for (int drop = 0; drop < s.Droplets; drop++)
        {
            if (progress != null && (drop & 0xFFFF) == 0) progress(drop / (float)s.Droplets);
            float px = rng.NextSingle() * (n - 2 * margin - 1) + margin;
            float py = rng.NextSingle() * (n - 2 * margin - 1) + margin;
            if (map[(int)py * n + (int)px] <= 0f) continue;

            float dirX = 0, dirY = 0, speed = 1f, water = 1f, sediment = 0f;
            for (int life = 0; life < s.MaxLifetime; life++)
            {
                int nx = (int)px, ny = (int)py;
                int idx = ny * n + nx;
                float cx = px - nx, cy = py - ny;
                HeightGradient(map, n, px, py, out float h, out float gx, out float gy);

                dirX = dirX * s.Inertia - gx * (1f - s.Inertia);
                dirY = dirY * s.Inertia - gy * (1f - s.Inertia);
                float len = MathF.Sqrt(dirX * dirX + dirY * dirY);
                if (len < 1e-6f) break;
                dirX /= len; dirY /= len;
                px += dirX; py += dirY;
                if (px < margin || py < margin || px >= n - margin - 1 || py >= n - margin - 1) break;

                HeightGradient(map, n, px, py, out float newH, out _, out _);
                float dh = newH - h;
                if (newH <= 0f && h <= 0f) break; // reached the sea
                float capacity = MathF.Max(-dh * speed * water * s.CapacityFactor, s.MinCapacity);

                if (sediment > capacity || dh > 0f)
                {
                    float amount = dh > 0f ? MathF.Min(dh, sediment) : (sediment - capacity) * s.DepositSpeed;
                    sediment -= amount;
                    map[idx] += amount * (1 - cx) * (1 - cy);
                    map[idx + 1] += amount * cx * (1 - cy);
                    map[idx + n] += amount * (1 - cx) * cy;
                    map[idx + n + 1] += amount * cx * cy;
                }
                else
                {
                    float amount = MathF.Min((capacity - sediment) * s.ErodeSpeed, -dh);
                    for (int b = 0; b < bo.Length; b++)
                    {
                        float e = amount * bw[b];
                        map[idx + bo[b]] -= e;
                        sediment += e;
                    }
                }
                speed = MathF.Sqrt(MathF.Max(0f, speed * speed - dh * s.Gravity));
                water *= 1f - s.EvaporateSpeed;
            }
        }

        for (int i = 0; i < map.Length; i++) map[i] *= s.HeightScale;
    }

    private static void HeightGradient(float[] map, int n, float px, float py, out float h, out float gx, out float gy)
    {
        int x = (int)px, y = (int)py;
        float u = px - x, v = py - y;
        int i = y * n + x;
        float nw = map[i], ne = map[i + 1], sw = map[i + n], se = map[i + n + 1];
        gx = (ne - nw) * (1 - v) + (se - sw) * v;
        gy = (sw - nw) * (1 - u) + (se - ne) * u;
        h = nw * (1 - u) * (1 - v) + ne * u * (1 - v) + sw * (1 - u) * v + se * u * v;
    }

    /// <summary>
    /// Thermal erosion: where the height difference to a neighbor exceeds talus (meters per cell),
    /// material is moved downhill. Removes unrealistically vertical walls.
    /// </summary>
    public static void Thermal(float[] map, int n, float talus, int iterations, float rate = 0.25f)
    {
        int[] nb = { -1, 1, -n, n };
        for (int it = 0; it < iterations; it++)
        {
            // Alternate direction to avoid bias
            bool rev = (it & 1) == 1;
            for (int jj = 1; jj < n - 1; jj++)
            {
                int j = rev ? n - 1 - jj : jj;
                for (int ii = 1; ii < n - 1; ii++)
                {
                    int i = rev ? n - 1 - ii : ii;
                    int c = j * n + i;
                    float hc = map[c];
                    if (hc <= 0f) continue;
                    foreach (int o in nb)
                    {
                        float diff = hc - map[c + o];
                        if (diff <= talus) continue;
                        float move = (diff - talus) * rate;
                        map[c] -= move;
                        map[c + o] += move;
                        hc -= move;
                    }
                }
            }
        }
    }
}
