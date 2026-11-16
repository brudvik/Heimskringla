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
        /* Forest   */ { 0.05f, 0f, 0.17f, 0.075f, 0.004f, 0.06f, 0.07f, 0.012f, 0.014f, 0.01f, 0.015f, 0.06f, 0.00024f, 0f },
        /* Pine     */ { 0.27f, 0.06f, 0.03f, 0f, 0.006f, 0.02f, 0.06f, 0f, 0.016f, 0.025f, 0.02f, 0.06f, 0.00045f, 0f },
        /* Swamp    */ { 0.005f, 0.004f, 0.03f, 0f, 0.02f, 0.06f, 0.05f, 0.006f, 0.008f, 0f, 0.005f, 0.03f, 0f, 0f },
        /* Tundra   */ { 0.004f, 0f, 0.02f, 0f, 0.006f, 0.03f, 0f, 0.01f, 0.002f, 0.03f, 0.04f, 0.01f, 0.00090f, 0f },
        /* Mountain */ { 0.012f, 0.008f, 0f, 0f, 0.004f, 0.004f, 0f, 0f, 0f, 0.05f, 0.04f, 0f, 0.00210f, 0f },
        /* Snow     */ { 0f, 0f, 0f, 0f, 0.002f, 0f, 0f, 0f, 0f, 0.012f, 0.005f, 0f, 0.00120f, 0f },
    };

    private static readonly float[] MinNormalY = { 0.82f, 0.82f, 0.82f, 0.85f, 0.8f, 0.75f, 0.78f, 0.8f, 0.8f, 0.5f, 0.7f, 0.75f, 0.35f, 0.85f };
    /// <summary>
    /// Steiner og klipper står på det laveste punktet under seg (ellers henger nedsiden i lufta i
    /// bratt terreng); andre props på senterhøyden.
    /// </summary>
    private static float BaseHeight(WorldGenerator w, PropDef def, int variant, float scale, float x, float z, float h)
    {
        if (def.Role != PropRole.Rock && def.Role != PropRole.Obstacle) return h;
        var size = def.Variants[variant].Aabb.Size;
        float r = Math.Max(size.X, size.Z) * 0.4f * scale;
        float m = h;
        for (int i = 0; i < 6; i++)
        {
            float a = i * MathF.Tau / 6f;
            m = MathF.Min(m, w.GetHeight(x + MathF.Cos(a) * r, z + MathF.Sin(a) * r));
        }
        return m;
    }

    private const int FlowerKind = (int)PropKind.Flower, CliffKind = (int)PropKind.Cliff;
    private const int SpruceKind = (int)PropKind.Spruce, BirchKind = (int)PropKind.Birch, OakKind = (int)PropKind.Oak;
    private const int FernKind = (int)PropKind.Fern, BushKind = (int)PropKind.Bush;

    /// <summary>
    /// Trær får en aldersfordeling i stedet for jevn størrelse: ~25 % unge (små, smale i kronen),
    /// ~62 % voksne (def-ens vanlige spenn) og ~13 % gamle kjemper. sc er jevnt fordelt 0..1.
    /// </summary>
    private static float TreeScale(PropDef def, float sc)
    {
        if (sc < 0.25f) return Mathf.Lerp(def.MinScale * 0.38f, def.MinScale * 0.8f, sc / 0.25f);
        if (sc < 0.87f) return Mathf.Lerp(def.MinScale, def.MaxScale, (sc - 0.25f) / 0.62f);
        return Mathf.Lerp(def.MaxScale * 1.05f, def.MaxScale * 1.4f, (sc - 0.87f) / 0.13f);
    }

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
            float treeMul = 0.2f + 1.05f * clump;
            // Bestand: i barskogen står det bjørkeholt, i lauvskogen granholt (store flekker)
            float stand = w.ColorNoise(x * 0.013f + 517f, z * 0.013f - 211f);
            float birchStand = Biomes.SmoothStep(0.15f, 0.5f, stand), spruceStand = Biomes.SmoothStep(0.15f, 0.5f, -stand);
            // Dyretråkk: bare småstein og greiner langs stien
            float trail = w.TrailMask(x, z, h, n.Y);
            float total = 0f;
            for (int k = 0; k < PlacedKinds; k++)
            {
                float d = Density[b, k];
                if (biome == Biome.PineForest)
                {
                    if (k == SpruceKind) d *= 1f - 0.7f * birchStand;
                    else if (k == BirchKind) d += 0.17f * birchStand;
                }
                else if (biome == Biome.Forest)
                {
                    if (k == SpruceKind) d += 0.16f * spruceStand;
                    else if (k == BirchKind || k == OakKind) d *= 1f - 0.6f * spruceStand;
                }
                if (k < TreeKinds) d *= treeMul;
                // Undervegetasjon (bregner/busker) der skogen er tett
                if (k == FernKind || k == BushKind) d *= 0.6f + clump * (biome is Biome.Forest or Biome.PineForest ? 1.2f : 0.4f);
                if (k == FlowerKind) d *= 1f + 3f * (1f - clump);          // blomstereng i lysningene
                if (k == CliffKind) d *= 0.2f + 4f * Biomes.SmoothStep(0.9f, 0.6f, n.Y);   // helst i skrenter
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
            var mv = def.Variants[variant];
            // Klippeskall (bare forsiden skannet) hører hjemme i bratte skrenter; på slak mark
            // ligger de som bleke plater
            if (mv.FaceDir != null && n.Y > 0.78f) continue;
            float scale = (def.Role == PropRole.Tree ? TreeScale(def, sc) : Mathf.Lerp(def.MinScale, def.MaxScale, sc)) * mv.BaseScale;
            // Skannede klippeskall: forsiden ut fra skråningen (baksiden inn i fjellet)
            if (mv.FaceDir is Vector3 fd && new Vector2(n.X, n.Z).LengthSquared() > 1e-4f)
                yaw = MathF.Atan2(n.X, n.Z) - MathF.Atan2(fd.X, fd.Z) + (vr - 0.5f) * 0.6f;
            float sink = mv.SinkFrac >= 0f ? mv.SinkFrac * mv.Aabb.Size.Y : def.Sink;
            if (biome == Biome.Tundra && def.Role == PropRole.Tree && !cold) scale *= 0.7f;
            lists[kind].Add(new PropInstance
            {
                Kind = (PropKind)kind,
                Variant = variant,
                Cold = cold,
                Pos = new Vector3(x, BaseHeight(w, def, variant, scale, x, z, h) - sink * scale, z),
                Yaw = yaw,
                Scale = scale,
                Id = MakeId(cx, cz, myIndex),
            });
        }
        return lists;
    }
}
