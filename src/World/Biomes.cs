using System;
using Godot;

namespace Heimskringla.World;

public enum Biome : byte
{
    Ocean,
    Beach,
    Meadow,      // Eng
    Forest,      // Lauvskog / blandingsskog
    PineForest,  // Barskog
    Swamp,       // Myr
    Tundra,      // Lynghei / tundra
    Mountain,    // Fjell
    Snow,        // Snø / isbre
}

/// <summary>
/// Klimasoner og bakkefarger. Samme funksjoner brukes av terrengmesh, vegetasjon og kart,
/// slik at alt stemmer overens.
/// </summary>
public static class Biomes
{
    public static readonly Color Sand = new(0.80f, 0.74f, 0.55f);
    public static readonly Color SeaFloor = new(0.52f, 0.48f, 0.36f);
    public static readonly Color MeadowGrass = new(0.37f, 0.53f, 0.20f);
    public static readonly Color ForestFloor = new(0.28f, 0.39f, 0.17f);
    public static readonly Color PineFloor = new(0.24f, 0.31f, 0.15f);
    public static readonly Color SwampMud = new(0.34f, 0.36f, 0.19f);
    public static readonly Color Heath = new(0.52f, 0.49f, 0.33f);
    public static readonly Color Rock = new(0.45f, 0.43f, 0.40f);
    public static readonly Color Snow = new(0.93f, 0.95f, 0.98f);
    public static readonly Color Trail = new(0.42f, 0.36f, 0.26f);
    public static readonly Color Dirt = new(0.36f, 0.34f, 0.20f);   // bratte bakker i lavlandet (jord og gress)
    public static readonly Color Scree = new(0.52f, 0.50f, 0.46f);  // ur ved fjellfoten

    public static string Name(Biome b) => b switch
    {
        Biome.Ocean => "Hav",
        Biome.Beach => "Strand",
        Biome.Meadow => "Eng",
        Biome.Forest => "Lauvskog",
        Biome.PineForest => "Barskog",
        Biome.Swamp => "Myr",
        Biome.Tundra => "Lynghei",
        Biome.Mountain => "Fjell",
        Biome.Snow => "Snø",
        _ => b.ToString(),
    };

    public static float SmoothStep(float e0, float e1, float x)
    {
        float t = Mathf.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Diskret klimasone for et punkt. ny = normalens y-komponent (1 = flatt).</summary>
    public static Biome Classify(float h, float temp, float moist, float ny)
    {
        if (h < 0.2f) return Biome.Ocean;
        if (h < 3.0f && ny > 0.8f) return Biome.Beach;
        if (temp < 0.12f || h > 240f) return Biome.Snow;
        if (h > 125f || ny < 0.72f) return Biome.Mountain;
        if (temp < 0.30f) return Biome.Tundra;
        if (moist > 0.68f && h < 28f) return Biome.Swamp;
        if (moist > 0.42f) return temp < 0.52f ? Biome.PineForest : Biome.Forest;
        return Biome.Meadow;
    }

    /// <summary>Kontinuerlig bakkefarge (sRGB) som blander sonene mykt.</summary>
    // Teksturlag i terrengshaderen (samme rekkefølge som scripts/fetch_terrain_textures.py)
    public const int LGrass = 0, LForest = 1, LPine = 2, LHeath = 3, LRock = 4, LScree = 5, LSand = 6, LSnow = 7, Layers = 8;

    private static void Toward(Span<float> w, int layer, float t)
    {
        if (t <= 0f) return;
        t = Math.Min(t, 1f);
        for (int i = 0; i < w.Length; i++) w[i] *= 1f - t;
        w[layer] += t;
    }

    /// <summary>
    /// Vekter for de 8 teksturlagene (summerer til 1). Speiler GroundColor steg for steg, så
    /// teksturene nær kameraet stemmer med fargene på avstand.
    /// </summary>
    public static void GroundWeights(float h, float temp, float moist, float ny, float rock, Span<float> w)
    {
        w.Clear();
        if (h < 0f)
        {
            w[LScree] = 1f;
            Toward(w, LSand, SmoothStep(-6f, 0f, h));
            return;
        }
        float forest = SmoothStep(0.36f, 0.48f, moist);
        w[LGrass] = 1f - forest;
        w[LForest] = forest;
        Toward(w, LPine, SmoothStep(0.58f, 0.46f, temp) * forest);
        Toward(w, LHeath, SmoothStep(0.62f, 0.72f, moist) * SmoothStep(32f, 18f, h));
        Toward(w, LHeath, SmoothStep(0.34f, 0.26f, temp));
        Toward(w, LRock, SmoothStep(105f, 150f, h) * 0.85f);
        Toward(w, LSand, SmoothStep(3.6f, 1.6f, h) * SmoothStep(0.62f, 0.8f, ny));
        if (rock < 0f) rock = SmoothStep(30f, 80f, h);
        Toward(w, LHeath, SmoothStep(0.86f, 0.7f, ny) * (1f - rock) * 0.6f);
        Toward(w, LRock, SmoothStep(0.82f, 0.62f, ny) * rock + SmoothStep(0.6f, 0.45f, ny) * (1f - rock));
        Toward(w, LScree, rock * SmoothStep(0.9f, 0.8f, ny) * SmoothStep(0.62f, 0.75f, ny) * 0.5f);
        float snow = Math.Max(SmoothStep(0.16f, 0.09f, temp), SmoothStep(225f, 260f, h));
        Toward(w, LSnow, snow * SmoothStep(0.50f, 0.72f, ny));
    }

    /// <param name="rock">bergmaske 0..1 (WorldGenerator.GetRock); -1 = ukjent (bruk høyden)</param>
    public static Color GroundColor(float h, float temp, float moist, float ny, float rock = -1f)
    {
        if (h < 0f)
            return SeaFloor.Lerp(Sand, SmoothStep(-6f, 0f, h));

        float forest = SmoothStep(0.36f, 0.48f, moist);
        Color c = MeadowGrass.Lerp(ForestFloor, forest);
        c = c.Lerp(PineFloor, SmoothStep(0.58f, 0.46f, temp) * forest);
        c = c.Lerp(SwampMud, SmoothStep(0.62f, 0.72f, moist) * SmoothStep(32f, 18f, h));
        c = c.Lerp(Heath, SmoothStep(0.34f, 0.26f, temp));
        c = c.Lerp(Rock, SmoothStep(105f, 150f, h) * 0.85f);
        c = c.Lerp(Sand, SmoothStep(3.6f, 1.6f, h) * SmoothStep(0.62f, 0.8f, ny));   // sand bare der det er slakt
        if (rock < 0f) rock = SmoothStep(30f, 80f, h);
        // Bratt i lavlandet: jord/mørkere gress. Berg bare der det er fjell (eller svært bratt).
        c = c.Lerp(Dirt, SmoothStep(0.86f, 0.7f, ny) * (1f - rock) * 0.6f);
        c = c.Lerp(Rock, SmoothStep(0.82f, 0.62f, ny) * rock + SmoothStep(0.6f, 0.45f, ny) * (1f - rock));
        // Hyller i fjellet: gress og lav der det er flatt nok
        c = c.Lerp(Scree, rock * SmoothStep(0.9f, 0.8f, ny) * SmoothStep(0.62f, 0.75f, ny) * 0.5f);
        float snow = Mathf.Max(SmoothStep(0.16f, 0.09f, temp), SmoothStep(225f, 260f, h));
        c = c.Lerp(Snow, snow * SmoothStep(0.50f, 0.72f, ny));
        return c;
    }
}
