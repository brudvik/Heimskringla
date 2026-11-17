using System;
using System.Collections.Generic;
using Godot;
using Heimskringla.Core;
using Heimskringla.World;

namespace Heimskringla.Vegetation;

/// <summary>Grass tufts in 32 m cells near the player, with wind and distance fading in the shader.</summary>
public partial class GrassManager : Node3D
{
    private const float CellSize = 32f;
    private const float Radius = 60f;
    private const float Spacing = 0.5f;

    private sealed class GrassCell
    {
        public int X, Z;
        public MultiMeshInstance3D Mmi;
        public JobQueue.Job Job;
    }

    private readonly Dictionary<(int, int), GrassCell> _cells = new();
    private float _timer;
    public int CellCount => _cells.Count;

    private static readonly float[] BiomeDensity = { 0f, 0.08f, 1f, 0.85f, 0.5f, 0.9f, 0.6f, 0.15f, 0f };

    public override void _Process(double delta)
    {
        _timer += (float)delta;
        if (_timer < 0.2f) return;
        _timer = 0;
        var p = Game.Focus;
        int pcx = Mathf.FloorToInt(p.X / CellSize), pcz = Mathf.FloorToInt(p.Z / CellSize);
        int r = Mathf.CeilToInt(Radius / CellSize) + 1;
        var w = Game.World;
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            int cx = pcx + dx, cz = pcz + dz;
            float d = Dist(cx, cz, p);
            if (d > Radius || _cells.ContainsKey((cx, cz))) continue;
            var cell = new GrassCell { X = cx, Z = cz };
            _cells[(cx, cz)] = cell;
            cell.Job = Game.Jobs.Enqueue(d + 20f, () => Generate(w, cx, cz), res => OnBuilt(cell, (float[])res));
        }
        var remove = new List<GrassCell>();
        foreach (var c in _cells.Values)
            if (Dist(c.X, c.Z, p) > Radius + 24f) remove.Add(c);
        foreach (var c in remove)
        {
            if (c.Job != null) c.Job.Cancelled = true;
            c.Mmi?.QueueFree();
            _cells.Remove((c.X, c.Z));
        }
    }

    private static float Dist(int cx, int cz, Vector3 p)
    {
        float x0 = cx * CellSize, z0 = cz * CellSize;
        float dx = Mathf.Max(Mathf.Max(x0 - p.X, 0f), p.X - (x0 + CellSize));
        float dz = Mathf.Max(Mathf.Max(z0 - p.Z, 0f), p.Z - (z0 + CellSize));
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static float[] Generate(WorldGenerator w, int cx, int cz)
    {
        var rng = new Random(HashCode.Combine(w.Seed, cx, cz, 0x6A55));
        int per = (int)(CellSize / Spacing);
        var data = new List<float>(per * per * 16);
        float x0 = cx * CellSize, z0 = cz * CellSize;
        for (int gz = 0; gz < per; gz++)
        for (int gx = 0; gx < per; gx++)
        {
            float jx = rng.NextSingle(), jz = rng.NextSingle(), roll = rng.NextSingle(), yaw = rng.NextSingle() * Mathf.Tau, sc = rng.NextSingle();
            float x = x0 + (gx + jx) * Spacing, z = z0 + (gz + jz) * Spacing;
            float h = w.GetHeight(x, z);
            if (h < 1.5f || w.GetLakeWater(x, z) > h - 0.1f) continue;
            float hx = w.GetHeight(x + 0.7f, z) - h, hz = w.GetHeight(x, z + 0.7f) - h;
            float ny = 0.7f / MathF.Sqrt(hx * hx + hz * hz + 0.49f);
            float temp = w.GetTemp(x, z), moist = w.GetMoist(x, z);
            var biome = Biomes.Classify(h, temp, moist, ny);
            float density = BiomeDensity[(int)biome] * Biomes.SmoothStep(0.66f, 0.84f, ny)
                            * (1f - w.TrailMask(x, z, h, ny));
            // Under dense forest it is too dark for much grass (ferns and moss take over); clearings are green
            if (biome is Biome.Forest or Biome.PineForest)
                density *= 1f - 0.65f * Biomes.SmoothStep(-0.35f, 0.35f, w.ForestClump(x, z));
            if (roll >= density) continue;
            var col = Biomes.GroundColor(h, temp, moist, ny, w.GetRock(x, z));
            // Larger tufts where it is lush, and "meadows" with taller grass
            float meadow = w.ColorNoise(x * 0.15f, z * 0.15f) * 0.5f + 0.5f;
            float s = (0.75f + sc * 0.5f) * (0.8f + meadow * 0.45f) * (0.8f + moist * 0.4f);
            var basis = new Basis(Vector3.Up, yaw).Scaled(new Vector3(s, s, s));
            data.Add(basis.X.X); data.Add(basis.Y.X); data.Add(basis.Z.X); data.Add(x);
            data.Add(basis.X.Y); data.Add(basis.Y.Y); data.Add(basis.Z.Y); data.Add(h - 0.05f);
            data.Add(basis.X.Z); data.Add(basis.Y.Z); data.Add(basis.Z.Z); data.Add(z);
            float v = 0.9f + sc * 0.2f;
            data.Add(col.R * v); data.Add(col.G * v); data.Add(col.B * v); data.Add(rng.NextSingle());
        }
        return data.ToArray();
    }

    private void OnBuilt(GrassCell cell, float[] data)
    {
        if (!_cells.TryGetValue((cell.X, cell.Z), out var cur) || cur != cell) return;
        cell.Job = null;
        int count = data.Length / 16;
        if (count == 0) return;
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = PropLibrary.GrassMesh,
        };
        mm.InstanceCount = count;
        mm.Buffer = data;
        cell.Mmi = new MultiMeshInstance3D { Multimesh = mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(cell.Mmi);
    }
}
