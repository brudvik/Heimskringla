using System.Collections.Generic;
using Godot;
using Heimskringla.Core;

namespace Heimskringla.Terrain;

/// <summary>
/// Collision only near the player: a 5x5 grid of 64 m heightmaps (1 m resolution)
/// around the focus point. Samples the same height function as the LOD0 mesh.
/// </summary>
public partial class CollisionManager : Node3D
{
    private const int Size = 64;
    private const int Radius = 2;
    private readonly Dictionary<(int, int), StaticBody3D> _bodies = new();

    public int BodyCount => _bodies.Count;

    public override void _PhysicsProcess(double delta) => UpdateAround(Game.Focus, 2);

    public bool IsReadyAt(Vector3 p) => _bodies.ContainsKey(CellOf(p));

    private static (int, int) CellOf(Vector3 p) => (Mathf.FloorToInt(p.X / Size), Mathf.FloorToInt(p.Z / Size));

    /// <summary>Builds missing collision. maxBuild &lt; 0 builds everything at once (teleport).</summary>
    public void UpdateAround(Vector3 p, int maxBuild)
    {
        var (cx, cz) = CellOf(p);
        // Build nearest first
        int built = 0;
        for (int r = 0; r <= Radius; r++)
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;
            var key = (cx + dx, cz + dz);
            if (_bodies.ContainsKey(key)) continue;
            if (maxBuild >= 0 && built >= maxBuild) goto done;
            _bodies[key] = Build(key.Item1, key.Item2);
            built++;
        }
        done:
        var remove = new List<(int, int)>();
        foreach (var k in _bodies.Keys)
            if (Mathf.Abs(k.Item1 - cx) > Radius + 1 || Mathf.Abs(k.Item2 - cz) > Radius + 1) remove.Add(k);
        foreach (var k in remove)
        {
            _bodies[k].QueueFree();
            _bodies.Remove(k);
        }
    }

    private StaticBody3D Build(int cx, int cz)
    {
        var w = Game.World;
        int n = Size + 1;
        var data = new float[n * n];
        float x0 = cx * Size, z0 = cz * Size;
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
            data[j * n + i] = w.GetHeight(x0 + i, z0 + j);

        var shape = new HeightMapShape3D { MapWidth = n, MapDepth = n, MapData = data };
        var body = new StaticBody3D
        {
            Position = new Vector3(x0 + Size * 0.5f, 0, z0 + Size * 0.5f),
            CollisionLayer = Game.LayerWorld,
            CollisionMask = 0,
        };
        body.AddChild(new CollisionShape3D { Shape = shape });
        AddChild(body);
        return body;
    }
}
