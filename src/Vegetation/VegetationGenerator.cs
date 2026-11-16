using System;
using System.Collections.Generic;
using Godot;
using Heimskringla.World;

namespace Heimskringla.Vegetation;

public sealed class PropInstance
{
    public PropKind Kind;
    public int Variant;
    public bool Cold;
    public Vector3 Pos;
    public float Yaw;
    public float Scale;
    public long Id;
    public int MmIndex;
    public bool Removed;
    public Node3D Node;
    internal object Cell;

    public Transform3D Transform => new(new Basis(Vector3.Up, Yaw).Scaled(new Vector3(Scale, Scale, Scale)), Pos);
}

/// <summary>Deterministisk plassering av vegetasjon og steiner per celle. Kjøres i bakgrunnstråd.</summary>
public static class VegetationGenerator
{
    public const int PlacedKinds = 14; // Spruce..Driftwood (Log plasseres ikke)
    private const int TreeKinds = 5;    // Spruce..DeadTree påvirkes av skogholt-støy
    private const float Spacing = 3f;

    //                     Gran    Furu    Bjørk   Eik     Tørr    Busk    Bregne  Blomst  Sopp    Blokk   Stein   Grein   Klippe  Drivved
    private static readonly float[,] Density =
    {
        /* Ocean    */ { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },
        /* Beach    */ { 0f, 0f, 0f, 0f, 0.002f, 0.004f, 0f, 0f, 0f, 0.008f, 0.03f, 0.012f, 0.00090f, 0.012f },
        /* Meadow   */ { 0.002f, 0f, 0.012f, 0.012f, 0f, 0.02f, 0.006f, 0.05f, 0.002f, 0.005f, 0.02f, 0.012f, 0.00012f, 0f },
        /* Forest   */ { 0.02f, 0f, 0.10f, 0.05f, 0.003f, 0.04f, 0.04f, 0.012f, 0.012f, 0.008f, 0.015f, 0.06f, 0.00024f, 0f },
        /* Pine     */ { 0.13f, 0.035f, 0.012f, 0f, 0.004f, 0.015f, 0.035f, 0f, 0.014f, 0.02f, 0.02f, 0.06f, 0.00045f, 0f },
        /* Swamp    */ { 0.005f, 0.004f, 0.03f, 0f, 0.02f, 0.06f, 0.05f, 0.006f, 0.008f, 0f, 0.005f, 0.03f, 0f, 0f },
        /* Tundra   */ { 0.004f, 0f, 0.02f, 0f, 0.006f, 0.03f, 0f, 0.01f, 0.002f, 0.03f, 0.04f, 0.01f, 0.00090f, 0f },
        /* Mountain */ { 0.012f, 0.008f, 0f, 0f, 0.004f, 0.004f, 0f, 0f, 0f, 0.05f, 0.04f, 0f, 0.00210f, 0f },
        /* Snow     */ { 0f, 0f, 0f, 0f, 0.002f, 0f, 0f, 0f, 0f, 0.012f, 0.005f, 0f, 0.00120f, 0f },
    };

    private static readonly float[] MinNormalY = { 0.82f, 0.82f, 0.82f, 0.85f, 0.8f, 0.75f, 0.78f, 0.8f, 0.8f, 0.5f, 0.7f, 0.75f, 0.35f, 0.85f };
    private const int FlowerKind = (int)PropKind.Flower, CliffKind = (int)PropKind.Cliff;

    public static long MakeId(int cx, int cz, int index) =>
        ((long)(cx + 4096) << 40) | ((long)(cz + 4096) << 20) | (long)index;

    public static List<PropInstance>[] Generate(WorldGenerator w, int cx, int cz, float cellSize)
    {
        var lists = new List<PropInstance>[PlacedKinds];
        for (int k = 0; k < PlacedKinds; k++) lists[k] = new List<PropInstance>();

        int hash = HashCode.Combine(w.Seed, cx, cz, 0x5EED);
        var rng = new Random(hash);
        int per = (int)(cellSize / Spacing);
        float x0 = cx * cellSize, z0 = cz * cellSize;
        float half = WorldSettings.HalfSize - 8f;
        var dens = new float[PlacedKinds];
        int index = 0;

        for (int gz = 0; gz < per; gz++)
        for (int gx = 0; gx < per; gx++)
        {
            float jx = rng.NextSingle(), jz = rng.NextSingle(), roll = rng.NextSingle();
            float yaw = rng.NextSingle() * Mathf.Tau, sc = rng.NextSingle(), vr = rng.NextSingle();
            int myIndex = index++;

            float x = x0 + (gx + jx) * Spacing, z = z0 + (gz + jz) * Spacing;
            if (MathF.Abs(x) > half || MathF.Abs(z) > half) continue;
            float h = w.GetHeight(x, z);
            if (h < 0.4f) continue;
            if (w.GetLakeWater(x, z) > h - 0.3f) continue;
            float temp = w.GetTemp(x, z), moist = w.GetMoist(x, z);
            var n = w.GetNormal(x, z, 1.5f);
            var biome = Biomes.Classify(h, temp, moist, n.Y);
            int b = (int)biome;

            float clump = Biomes.SmoothStep(-0.35f, 0.35f, w.ForestClump(x, z));
            float treeMul = 0.15f + 1.2f * clump;
            // Dyretråkk: bare småstein og greiner langs stien
            float trail = w.TrailMask(x, z, h, n.Y);
            float total = 0f;
            for (int k = 0; k < PlacedKinds; k++)
            {
                float d = Density[b, k];
                if (k < TreeKinds) d *= treeMul;
                if (k == FlowerKind) d *= 1f + 3f * (1f - clump);          // blomstereng i lysningene
                if (k == CliffKind) d *= 0.25f + 2.5f * Biomes.SmoothStep(0.97f, 0.7f, n.Y);   // helst i skrenter
                if (trail > 0.3f && k != (int)PropKind.Stone && k != (int)PropKind.Branch) d = 0f;
                if (n.Y < MinNormalY[k]) d = 0f;
                dens[k] = d;
                total += d;
            }
            if (roll >= total) continue;

            float cum = 0f;
            int kind = -1;
            for (int k = 0; k < PlacedKinds; k++)
            {
                cum += dens[k];
                if (roll < cum) { kind = k; break; }
            }
            if (kind < 0) continue;

            var def = PropLibrary.Defs[kind];
            bool cold = temp < 0.24f || biome == Biome.Snow || (biome == Biome.Tundra && temp < 0.3f);
            int vFrom = 0, vCount = def.Variants.Length;
            if (def.ColdStart >= 0)
            {
                if (cold) { vFrom = def.ColdStart; vCount = def.Variants.Length - def.ColdStart; }
                else vCount = def.ColdStart;
            }
            int variant = vFrom + Math.Min((int)(vr * vCount), vCount - 1);
            float scale = Mathf.Lerp(def.MinScale, def.MaxScale, sc) * def.Variants[variant].BaseScale;
            if (biome == Biome.Tundra && def.Role == PropRole.Tree && !cold) scale *= 0.7f;
            lists[kind].Add(new PropInstance
            {
                Kind = (PropKind)kind,
                Variant = variant,
                Cold = cold,
                Pos = new Vector3(x, h - def.Sink * scale, z),
                Yaw = yaw,
                Scale = scale,
                Id = MakeId(cx, cz, myIndex),
            });
        }
        return lists;
    }
}
