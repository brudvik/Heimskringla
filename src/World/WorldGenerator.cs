using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;

namespace Heimskringla.World;

/// <summary>
/// Genererer et grovkart (høyde, vann, temperatur, fuktighet) for hele verden, og tilbyr
/// trådsikre sampling-funksjoner for detaljert høyde. Etter Generate() er alle data skrivebeskyttet,
/// så alle metoder kan kalles fra bakgrunnstråder.
///
/// Dette er "terreng-sampling"-grensesnittet: en fremtidig voxel-renderer kan bruke samme kilde.
/// </summary>
public sealed class WorldGenerator
{
    public const float NoWater = -10000f;

    public readonly int Seed;
    public readonly int N = WorldSettings.MapResolution + 1; // antall gridpunkter per akse
    private const float Cell = WorldSettings.CellSize;
    private const float Half = WorldSettings.HalfSize;

    // Grovkart (N*N, radvis: idx = j*N + i, x = -Half + i*Cell, z = -Half + j*Cell)
    public float[] Height;     // terrenghøyde etter elveutgraving
    public float[] Water;      // vannflate for innsjøer/elver (NoWater der det ikke er vann)
    public float[] Temp;       // 0..1
    public float[] Moist;      // 0..1
    public float[] DetailAmp;  // amplitude for detaljstøy
    public byte[] MapPixels;   // RGB8, (N-1)*(N-1)
    public int MapSize => N - 1;
    public string Report = "";
    public int RiverCells;
    public int LakeCells;

    private readonly FastNoiseLite _warp, _cont, _hill, _mmask, _ridge, _temp, _moist;
    private readonly FastNoiseLite _detail, _detailFine, _border, _color;

    private WorldGenerator(int seed)
    {
        Seed = seed;
        _warp = MakeNoise(seed + 1, 1f / 1500f, 3);
        _cont = MakeNoise(seed + 2, 1f / 3600f, 5);
        _hill = MakeNoise(seed + 3, 1f / 700f, 4);
        _mmask = MakeNoise(seed + 4, 1f / 2300f, 2);
        _ridge = MakeNoise(seed + 5, 1f / 1400f, 5);
        _ridge.SetFractalType(FastNoiseLite.FractalType.Ridged);
        _temp = MakeNoise(seed + 6, 1f / 2000f, 3);
        _moist = MakeNoise(seed + 7, 1f / 1500f, 4);
        _detail = MakeNoise(seed + 8, 1f / 45f, 4);
        _detailFine = MakeNoise(seed + 9, 1f / 9f, 2);
        _border = MakeNoise(seed + 10, 1f / 35f, 2);
        _color = MakeNoise(seed + 11, 1f / 25f, 2);
    }

    private static FastNoiseLite MakeNoise(int seed, float freq, int octaves)
    {
        var n = new FastNoiseLite(seed);
        n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        n.SetFrequency(freq);
        n.SetFractalType(octaves > 1 ? FastNoiseLite.FractalType.FBm : FastNoiseLite.FractalType.None);
        n.SetFractalOctaves(octaves);
        return n;
    }

    // ------------------------------------------------------------------ Generering

    public static WorldGenerator Generate(int seed, Action<string> progress)
    {
        var g = new WorldGenerator(seed);
        g.Run(progress ?? (_ => { }));
        return g;
    }

    private void Run(Action<string> progress)
    {
        var sw = Stopwatch.StartNew();
        var log = new System.Text.StringBuilder();
        void Step(string name)
        {
            log.Append($"{name}: {sw.ElapsedMilliseconds} ms\n");
            sw.Restart();
        }

        int count = N * N;
        progress("Former landskap...");
        var h = new float[count];
        Parallel.For(0, N, j =>
        {
            float z = -Half + j * Cell;
            for (int i = 0; i < N; i++)
                h[j * N + i] = BaseHeight(-Half + i * Cell, z);
        });
        Step("Grunnhøyde");

        progress("Fyller senkninger (innsjøer)...");
        float[] filled = PriorityFlood(h);
        Step("Priority flood");

        progress("Beregner elver...");
        float[] acc = FlowAccumulation(h, filled);
        Step("Vannføring");

        // Elveutgraving
        var carveRaw = new float[count];
        var riverLevel = new float[count];
        Array.Fill(riverLevel, NoWater);
        for (int j = 2; j < N - 2; j++)
        for (int i = 2; i < N - 2; i++)
        {
            int c = j * N + i;
            if (acc[c] < WorldSettings.RiverThreshold || h[c] <= 0f) continue;
            float s = MathF.Log2(acc[c] / WorldSettings.RiverThreshold);
            float depth = 1.6f + MathF.Min(s, 6f) * 0.9f;
            int radius = s > 4.5f ? 2 : s > 2.3f ? 1 : 0;
            // Bratte partier (stryk/fossefall) graves ut, men får ingen flat vannflate
            float maxDrop = 0f;
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
                maxDrop = MathF.Max(maxDrop, MathF.Abs(filled[c] - filled[c + dj * N + di]));
            float level = maxDrop > 2.5f ? NoWater : filled[c] - depth * 0.45f;
            for (int dj = -radius; dj <= radius; dj++)
            for (int di = -radius; di <= radius; di++)
            {
                if (di * di + dj * dj > radius * radius + 1) continue;
                int n = c + dj * N + di;
                float d = (di == 0 && dj == 0) ? depth : depth * 0.85f;
                carveRaw[n] = MathF.Max(carveRaw[n], d);
                riverLevel[n] = MathF.Max(riverLevel[n], level);
            }
            RiverCells++;
        }
        var carve = BoxBlur(BoxBlur(carveRaw, 1), 1);
        for (int k = 0; k < count; k++) carve[k] = MathF.Max(carveRaw[k], carve[k] * 1.4f);
        Step("Elver");

        // Vannflater
        progress("Legger vann i innsjøer og elver...");
        var water = new float[count];
        var wetMask = new float[count];
        for (int k = 0; k < count; k++)
        {
            float w = NoWater;
            if (filled[k] - h[k] > 0.25f && filled[k] > 0.3f) { w = filled[k]; LakeCells++; }
            if (riverLevel[k] > w && riverLevel[k] > 0.3f) w = riverLevel[k];
            water[k] = w;
            wetMask[k] = w > NoWater ? 1f : 0f;
        }
        // Dilater vannflaten ut og ned, så vannmeshet dykker under bredden
        for (int pass = 0; pass < 3; pass++)
        {
            var next = (float[])water.Clone();
            for (int j = 1; j < N - 1; j++)
            for (int i = 1; i < N - 1; i++)
            {
                int c = j * N + i;
                if (water[c] > NoWater) continue;
                float m = NoWater;
                for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                    m = MathF.Max(m, water[c + dj * N + di]);
                // Kun der vannflaten havner under bakken (ellers henger vannet utover stup)
                if (m > NoWater && h[c] - carve[c] > m - 2.5f + 0.3f) next[c] = m - 2.5f;
            }
            water = next;
        }

        Height = new float[count];
        for (int k = 0; k < count; k++) Height[k] = h[k] - carve[k];
        Water = water;
        Step("Vannflater");

        // Klima
        progress("Beregner klima...");
        var wetFar = BoxBlur(BoxBlur(wetMask, 8), 8);
        var wetNear = BoxBlur(wetMask, 2);
        var ocean = new float[count];
        for (int k = 0; k < count; k++) ocean[k] = h[k] < 0f ? 1f : 0f;
        var coast = BoxBlur(BoxBlur(ocean, 25), 25);

        Temp = new float[count];
        Moist = new float[count];
        DetailAmp = new float[count];
        Parallel.For(0, N, j =>
        {
            float z = -Half + j * Cell;
            for (int i = 0; i < N; i++)
            {
                int k = j * N + i;
                float x = -Half + i * Cell;
                float hh = Height[k];
                float t = 0.5f + 0.42f * (z / Half) + _temp.GetNoise(x, z) * 0.14f - MathF.Max(0f, hh) / 430f;
                Temp[k] = Math.Clamp(t, 0f, 1f);
                float m = 0.47f + _moist.GetNoise(x, z) * 0.55f + MathF.Min(wetFar[k] * 2.5f, 0.35f) + coast[k] * 0.12f;
                Moist[k] = Math.Clamp(m, 0f, 1f);
                float amp = (1.0f + 7f * Biomes.SmoothStep(60f, 220f, hh)) * (0.25f + 0.75f * Biomes.SmoothStep(1f, 8f, hh));
                amp *= 1f - 0.9f * MathF.Min(wetNear[k] * 2f, 1f);
                DetailAmp[k] = amp;
            }
        });
        Step("Klima");

        progress("Tegner verdenskart...");
        BuildMapPixels();
        Step("Kart");
        Report = log.ToString();
    }

    private float BaseHeight(float x, float z)
    {
        float wx = x + _warp.GetNoise(x, z) * 520f;
        float wz = z + _warp.GetNoise(x + 7919f, z - 3571f) * 520f;
        float r = MathF.Sqrt(x * x + z * z) / Half;

        // Kontinentmaske: land i midten, hav ytterst
        float c = _cont.GetNoise(wx, wz) * 0.55f + 0.36f - Biomes.SmoothStep(0.55f, 0.95f, r) * 1.15f;
        float land = Biomes.SmoothStep(0f, 0.22f, c);
        float h = c < 0f ? MathF.Max(c * 130f, -70f) : c * 70f;

        h += _hill.GetNoise(wx, wz) * 20f * land;

        float mm = Biomes.SmoothStep(0.05f, 0.45f, _mmask.GetNoise(wx, wz) * 0.8f + c * 0.6f);
        float rn = (_ridge.GetNoise(wx, wz) + 1f) * 0.5f;
        h += rn * rn * 390f * mm * land;
        return h;
    }

    /// <summary>Barnes et al. priority-flood: fyller senkninger slik at alt vann kan renne til havet.</summary>
    private float[] PriorityFlood(float[] h)
    {
        const float eps = 0.002f;
        var f = (float[])h.Clone();
        var closed = new bool[f.Length];
        var pq = new PriorityQueue<int, float>(f.Length / 2);
        for (int j = 0; j < N; j++)
        for (int i = 0; i < N; i++)
        {
            int k = j * N + i;
            if (i == 0 || j == 0 || i == N - 1 || j == N - 1 || h[k] <= 0f)
            {
                closed[k] = true;
                pq.Enqueue(k, f[k]);
            }
        }
        while (pq.TryDequeue(out int c, out float fc))
        {
            int ci = c % N, cj = c / N;
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int ni = ci + di, nj = cj + dj;
                if ((di == 0 && dj == 0) || ni < 0 || nj < 0 || ni >= N || nj >= N) continue;
                int n = nj * N + ni;
                if (closed[n]) continue;
                closed[n] = true;
                if (f[n] <= fc + eps) f[n] = fc + eps;
                pq.Enqueue(n, f[n]);
            }
        }
        return f;
    }

    private float[] FlowAccumulation(float[] h, float[] f)
    {
        int count = N * N;
        var down = new int[count];
        var land = new List<int>(count / 2);
        for (int j = 1; j < N - 1; j++)
        for (int i = 1; i < N - 1; i++)
        {
            int c = j * N + i;
            down[c] = -1;
            if (h[c] <= 0f) continue;
            land.Add(c);
            float best = 0f;
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                if (di == 0 && dj == 0) continue;
                int n = c + dj * N + di;
                float slope = (f[c] - f[n]) / ((di != 0 && dj != 0) ? 1.4142f : 1f);
                if (slope > best) { best = slope; down[c] = n; }
            }
        }
        var keys = new float[land.Count];
        var idx = land.ToArray();
        for (int k = 0; k < idx.Length; k++) keys[k] = -f[idx[k]];
        Array.Sort(keys, idx);

        var acc = new float[count];
        foreach (int c in idx) acc[c] += 1f;
        foreach (int c in idx)
        {
            int d = down[c];
            if (d >= 0) acc[d] += acc[c];
        }
        return acc;
    }

    /// <summary>Separabel boksblur med gitt radius (i celler).</summary>
    private float[] BoxBlur(float[] src, int radius)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        float inv = 1f / (radius * 2 + 1);
        Parallel.For(0, N, j =>
        {
            int row = j * N;
            for (int i = 0; i < N; i++)
            {
                float s = 0f;
                for (int d = -radius; d <= radius; d++)
                    s += src[row + Math.Clamp(i + d, 0, N - 1)];
                tmp[row + i] = s * inv;
            }
        });
        Parallel.For(0, N, i =>
        {
            for (int j = 0; j < N; j++)
            {
                float s = 0f;
                for (int d = -radius; d <= radius; d++)
                    s += tmp[Math.Clamp(j + d, 0, N - 1) * N + i];
                dst[j * N + i] = s * inv;
            }
        });
        return dst;
    }

    private void BuildMapPixels()
    {
        int size = MapSize;
        MapPixels = new byte[size * size * 3];
        var light = new Vector3(-0.6f, 0.8f, -0.4f).Normalized();
        Parallel.For(0, size, j =>
        {
            for (int i = 0; i < size; i++)
            {
                int k = j * N + i;
                float hh = Height[k];
                float hx = Height[k + 1] - hh, hz = Height[k + N] - hh;
                var n = new Vector3(-hx, Cell, -hz).Normalized();
                Color c;
                if (Water[k] > hh + 0.05f && Water[k] > 0f)
                    c = new Color(0.25f, 0.45f, 0.65f);
                else if (hh < 0f)
                    c = new Color(0.12f, 0.28f, 0.48f).Lerp(new Color(0.30f, 0.50f, 0.68f), Biomes.SmoothStep(-40f, 0f, hh));
                else
                {
                    c = Biomes.GroundColor(hh, Temp[k], Moist[k], n.Y);
                    float shade = 0.65f + 0.55f * MathF.Max(0f, n.Dot(light));
                    c = new Color(c.R * shade, c.G * shade, c.B * shade);
                }
                int p = (j * size + i) * 3;
                MapPixels[p] = (byte)Math.Clamp(c.R * 255f, 0, 255);
                MapPixels[p + 1] = (byte)Math.Clamp(c.G * 255f, 0, 255);
                MapPixels[p + 2] = (byte)Math.Clamp(c.B * 255f, 0, 255);
            }
        });
    }

    // ------------------------------------------------------------------ Sampling (trådsikker)

    private float SampleBilinear(float[] a, float x, float z)
    {
        float gx = Math.Clamp((x + Half) / Cell, 0f, N - 1.001f);
        float gz = Math.Clamp((z + Half) / Cell, 0f, N - 1.001f);
        int i = (int)gx, j = (int)gz;
        float fx = gx - i, fz = gz - j;
        int k = j * N + i;
        float a0 = a[k] + (a[k + 1] - a[k]) * fx;
        float a1 = a[k + N] + (a[k + N + 1] - a[k + N]) * fx;
        return a0 + (a1 - a0) * fz;
    }

    private static float CatmullRom(float p0, float p1, float p2, float p3, float t)
    {
        return p1 + 0.5f * t * (p2 - p0 + t * (2f * p0 - 5f * p1 + 4f * p2 - p3 + t * (3f * (p1 - p2) + p3 - p0)));
    }

    private float SampleBicubic(float[] a, float x, float z)
    {
        float gx = Math.Clamp((x + Half) / Cell, 0f, N - 1.001f);
        float gz = Math.Clamp((z + Half) / Cell, 0f, N - 1.001f);
        int i = (int)gx, j = (int)gz;
        float fx = gx - i, fz = gz - j;
        Span<float> rows = stackalloc float[4];
        for (int r = -1; r <= 2; r++)
        {
            int jj = Math.Clamp(j + r, 0, N - 1) * N;
            rows[r + 1] = CatmullRom(
                a[jj + Math.Clamp(i - 1, 0, N - 1)], a[jj + i],
                a[jj + Math.Min(i + 1, N - 1)], a[jj + Math.Min(i + 2, N - 1)], fx);
        }
        return CatmullRom(rows[0], rows[1], rows[2], rows[3], fz);
    }

    /// <summary>Detaljert terrenghøyde i meter.</summary>
    public float GetHeight(float x, float z)
    {
        float hc = SampleBicubic(Height, x, z);
        float amp = SampleBilinear(DetailAmp, x, z);
        return hc + (_detail.GetNoise(x, z) + _detailFine.GetNoise(x, z) * 0.12f) * amp;
    }

    public Vector3 GetNormal(float x, float z, float e = 1f)
    {
        float hl = GetHeight(x - e, z), hr = GetHeight(x + e, z);
        float hd = GetHeight(x, z - e), hu = GetHeight(x, z + e);
        return new Vector3(hl - hr, 2f * e, hd - hu).Normalized();
    }

    public float GetTemp(float x, float z) => SampleBilinear(Temp, x, z) + _border.GetNoise(x, z) * 0.035f;
    public float GetMoist(float x, float z) => SampleBilinear(Moist, x, z) + _border.GetNoise(x + 913f, z) * 0.05f;
    public float ColorNoise(float x, float z) => _color.GetNoise(x, z);

    /// <summary>Lavfrekvent støy som gir skogholt og lysninger (-1..1).</summary>
    public float ForestClump(float x, float z) => _hill.GetNoise(x * 7.7f + 1234f, z * 7.7f - 987f);

    /// <summary>Vannflate for innsjø/elv på punktet (NoWater hvis ingen). Havet (0 m) håndteres separat.</summary>
    public float GetLakeWater(float x, float z)
    {
        float gx = Math.Clamp((x + Half) / Cell, 0f, N - 1.001f);
        float gz = Math.Clamp((z + Half) / Cell, 0f, N - 1.001f);
        int i = (int)gx, j = (int)gz;
        int k = j * N + i;
        float w00 = Water[k], w10 = Water[k + 1], w01 = Water[k + N], w11 = Water[k + N + 1];
        if (w00 <= NoWater || w10 <= NoWater || w01 <= NoWater || w11 <= NoWater) return NoWater;
        float fx = gx - i, fz = gz - j;
        float a0 = w00 + (w10 - w00) * fx, a1 = w01 + (w11 - w01) * fx;
        return a0 + (a1 - a0) * fz;
    }

    /// <summary>Høyeste vannflate (hav eller innsjø/elv) over terrenget, eller NoWater.</summary>
    public float GetWaterLevel(float x, float z)
    {
        float ground = GetHeight(x, z);
        float lake = GetLakeWater(x, z);
        if (lake > ground) return lake;
        if (ground < WorldSettings.SeaLevel) return WorldSettings.SeaLevel;
        return NoWater;
    }

    public Biome GetBiome(float x, float z)
    {
        float h = GetHeight(x, z);
        if (GetLakeWater(x, z) > h) return Biome.Ocean;
        return Biomes.Classify(h, GetTemp(x, z), GetMoist(x, z), GetNormal(x, z).Y);
    }

    /// <summary>Finner et flatt, tørt startpunkt på enga, litt sør for midten.</summary>
    public Vector3 FindSpawn()
    {
        int ci = N / 2, cj = (int)(N * 0.62f);
        for (int r = 0; r < N / 2; r++)
        for (int dj = -r; dj <= r; dj++)
        for (int di = -r; di <= r; di++)
        {
            if (Math.Max(Math.Abs(di), Math.Abs(dj)) != r) continue;
            int i = ci + di, j = cj + dj;
            if (i < 2 || j < 2 || i >= N - 2 || j >= N - 2) continue;
            int k = j * N + i;
            float hh = Height[k];
            if (hh < 5f || hh > 45f || Water[k] > hh - 1f) continue;
            float slope = MathF.Abs(Height[k + 1] - Height[k - 1]) + MathF.Abs(Height[k + N] - Height[k - N]);
            if (slope > 2.5f) continue;
            if (Temp[k] < 0.4f || Moist[k] > 0.6f) continue;
            float x = -Half + i * Cell, z = -Half + j * Cell;
            return new Vector3(x, GetHeight(x, z), z);
        }
        return new Vector3(0, GetHeight(0, 0), 0);
    }
}
