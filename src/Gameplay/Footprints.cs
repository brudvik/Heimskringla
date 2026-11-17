using System.Collections.Generic;
using Godot;
using Heimskringla.Core;
using Heimskringla.World;

namespace Heimskringla.Gameplay;

/// <summary>Footprints (decals) in sand, snow, bog and on trails, and splashes in shallow water.</summary>
public static class Footprints
{
    private const int MaxPrints = 90;
    private static readonly Queue<Decal> Prints = new();
    private static Texture2D _tex;
    public static int Spawned;
    public static string LastInfo = "";

    /// <summary>Called on every footstep. Returns the surface type (for sound later).</summary>
    public static string OnStep(Node parent, int side, Vector3 pos, float yaw)
    {
        var w = Game.World;
        float ground = w.GetHeight(pos.X, pos.Z);
        float water = w.GetWaterLevel(pos.X, pos.Z);
        if (water > WorldGenerator.NoWater && water > ground + 0.05f)
        {
            float depth = water - ground;
            if (depth < 1.1f)
                Effects.Burst(parent, new Vector3(pos.X, water + 0.05f, pos.Z), new Color(0.85f, 0.92f, 1f), 10, 0.15f);
            return "water";
        }

        var n = w.GetNormal(pos.X, pos.Z);
        var biome = Biomes.Classify(ground, w.GetTemp(pos.X, pos.Z), w.GetMoist(pos.X, pos.Z), n.Y);
        float trail = w.TrailMask(pos.X, pos.Z, ground, n.Y);
        bool snow = biome == Biome.Snow || ground > 230f;
        float strength = biome switch
        {
            Biome.Beach => 0.75f,
            Biome.Swamp => 0.7f,
            Biome.Snow => 0.85f,
            Biome.Tundra => 0.25f,
            _ => 0f,
        };
        strength = Mathf.Max(strength, trail * 0.5f);
        if (snow) strength = 0.85f;
        if (strength > 0.05f) Spawn(parent, side, pos, yaw, strength, snow);
        return snow ? "snow" : biome == Biome.Beach ? "sand" : biome == Biome.Swamp ? "bog" : n.Y < 0.75f ? "rock" : "grass";
    }

    private static void Spawn(Node parent, int side, Vector3 pos, float yaw, float strength, bool snow)
    {
        _tex ??= MakeTexture();
        var right = new Vector3(Mathf.Cos(yaw), 0, -Mathf.Sin(yaw));
        var d = new Decal
        {
            Size = new Vector3(0.32f, 0.6f, 0.64f),
            TextureAlbedo = _tex,
            Modulate = snow ? new Color(0.55f, 0.6f, 0.72f, strength) : new Color(0.3f, 0.24f, 0.17f, strength),
            NormalFade = 0.4f,
            CullMask = 1,
        };
        parent.AddChild(d);
        d.GlobalPosition = pos + right * (side == 0 ? -0.11f : 0.11f) + new Vector3(0, 0.1f, 0);
        d.Rotation = new Vector3(0, yaw, 0);
        Prints.Enqueue(d);
        Spawned++;
        LastInfo = $"print #{Spawned} pos={d.GlobalPosition} ground={Game.World.GetHeight(pos.X, pos.Z):F2} strength={strength:F2}";
        while (Prints.Count > MaxPrints)
        {
            var old = Prints.Dequeue();
            if (GodotObject.IsInstanceValid(old)) old.QueueFree();
        }
        var tw = d.CreateTween();
        tw.TweenInterval(35.0);
        tw.TweenProperty(d, "modulate:a", 0f, 8.0);
        tw.TweenCallback(Callable.From(d.QueueFree));
    }

    /// <summary>Creates a simple shoe-print texture (sole + heel) in code.</summary>
    private static Texture2D MakeTexture()
    {
        int w = 48, h = 96;
        var img = Image.CreateEmpty(w, h, true, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float u = (x + 0.5f) / w * 2f - 1f, v = (y + 0.5f) / h;
            // Forefoot (ellipse) and heel (smaller ellipse)
            float fore = Ellipse(u, v, 0f, 0.30f, 0.85f, 0.26f);
            float heel = Ellipse(u, v, 0f, 0.78f, 0.62f, 0.17f);
            float a = Mathf.Clamp(Mathf.Max(fore, heel), 0f, 1f);
            img.SetPixel(x, y, new Color(1, 1, 1, a));
        }
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    private static float Ellipse(float u, float v, float cu, float cv, float ru, float rv)
    {
        float du = (u - cu) / ru, dv = (v - cv) / rv;
        float d = Mathf.Sqrt(du * du + dv * dv);
        return 1f - Mathf.SmoothStep(0.75f, 1f, d);
    }
}
