using System.Collections.Generic;
using Godot;
using Heimskringla.Core;
using Heimskringla.World;

namespace Heimskringla.Terrain;

public readonly record struct ChunkKey(int Level, int X, int Z)
{
    public float Size => TerrainManager.RootSize / (1 << Level);
    public float MinX => -WorldSettings.HalfSize + X * Size;
    public float MinZ => -WorldSettings.HalfSize + Z * Size;

    public bool Overlaps(ChunkKey o)
    {
        float s = Size, os = o.Size;
        return MinX < o.MinX + os && o.MinX < MinX + s && MinZ < o.MinZ + os && o.MinZ < MinZ + s;
    }
}

/// <summary>
/// Quadtree-basert terreng med LOD. Hele verden er alltid dekket: 1024 m-chunks langt unna,
/// ned til 64 m-chunks (1 m oppløsning) nær spilleren. Gamle chunks fjernes først når
/// erstatningene er ferdige, så det aldri blir hull.
/// </summary>
public partial class TerrainManager : Node3D
{
    public const float RootSize = 1024f;
    public const int MaxLevel = 4;          // 1024 / 2^4 = 64 m
    public const float SplitFactor = 1.5f;
    private static readonly int RootsPerAxis = (int)(WorldSettings.WorldSize / RootSize);

    private sealed class Chunk
    {
        public ChunkKey Key;
        public MeshInstance3D Mesh;
        public MeshInstance3D Water;
        public bool Ready;
        public bool Visible;
        public JobQueue.Job Job;
    }

    private readonly Dictionary<ChunkKey, Chunk> _chunks = new();
    private HashSet<ChunkKey> _desired = new();
    private Vector3 _lastFocus = new(float.MaxValue, 0, 0);
    private float _timer;
    private bool _dirty = true;

    public int ChunkCount => _chunks.Count;
    public int DesiredCount => _desired.Count;
    public int PendingCount { get; private set; }

    public override void _Process(double delta)
    {
        _timer += (float)delta;
        if (_dirty || _timer > 0.3f || Game.Focus.DistanceSquaredTo(_lastFocus) > 16f)
        {
            _timer = 0;
            _dirty = false;
            _lastFocus = Game.Focus;
            UpdateTree();
        }
    }

    /// <summary>True når alle ønskede chunks innenfor radius er ferdige (brukes ved oppstart).</summary>
    public bool IsAreaReady(Vector3 p, float radius)
    {
        foreach (var k in _desired)
        {
            float cx = Mathf.Clamp(p.X, k.MinX, k.MinX + k.Size), cz = Mathf.Clamp(p.Z, k.MinZ, k.MinZ + k.Size);
            if (new Vector2(cx - p.X, cz - p.Z).Length() > radius) continue;
            if (!_chunks.TryGetValue(k, out var c) || !c.Visible) return false;
        }
        return true;
    }

    private void UpdateTree()
    {
        var w = Game.World;
        var focus = Game.Focus;
        float above = Mathf.Max(0f, focus.Y - w.GetHeight(focus.X, focus.Z) - 10f);
        var desired = new HashSet<ChunkKey>();
        for (int z = 0; z < RootsPerAxis; z++)
        for (int x = 0; x < RootsPerAxis; x++)
            Visit(new ChunkKey(0, x, z), focus, above, desired);
        _desired = desired;

        // Nye chunks
        foreach (var key in desired)
        {
            float dist = Distance(key, focus, above);
            if (_chunks.TryGetValue(key, out var existing))
            {
                if (existing.Job != null) existing.Job.Priority = dist;
                continue;
            }
            var chunk = new Chunk { Key = key };
            _chunks[key] = chunk;
            var k = key;
            chunk.Job = Game.Jobs.Enqueue(dist,
                () => TerrainMeshBuilder.Build(w, k.MinX, k.MinZ, k.Size),
                result => OnBuilt(chunk, (ChunkMeshData)result));
        }

        // Fjern chunks som ikke lenger trengs (når erstatningen er klar)
        var remove = new List<Chunk>();
        foreach (var c in _chunks.Values)
        {
            if (desired.Contains(c.Key)) continue;
            if (!c.Ready) { remove.Add(c); continue; }
            bool covered = true;
            foreach (var dk in desired)
            {
                if (!dk.Overlaps(c.Key)) continue;
                if (!_chunks[dk].Ready) { covered = false; break; }
            }
            if (covered) remove.Add(c);
        }
        foreach (var c in remove) RemoveChunk(c);

        // Vis ferdige chunks som ikke overlapper gamle, synlige chunks
        int pending = 0;
        foreach (var dk in desired)
        {
            var c = _chunks[dk];
            if (!c.Ready) { pending++; continue; }
            if (c.Visible) continue;
            bool blocked = false;
            foreach (var o in _chunks.Values)
            {
                if (o.Visible && !desired.Contains(o.Key) && o.Key.Overlaps(dk)) { blocked = true; break; }
            }
            if (!blocked) SetVisible(c, true);
        }
        PendingCount = pending;
    }

    private static float Distance(ChunkKey k, Vector3 p, float above)
    {
        float s = k.Size;
        float dx = Mathf.Max(Mathf.Max(k.MinX - p.X, 0f), p.X - (k.MinX + s));
        float dz = Mathf.Max(Mathf.Max(k.MinZ - p.Z, 0f), p.Z - (k.MinZ + s));
        return Mathf.Sqrt(dx * dx + dz * dz + above * above);
    }

    private static void Visit(ChunkKey k, Vector3 p, float above, HashSet<ChunkKey> outSet)
    {
        if (k.Level < MaxLevel && Distance(k, p, above) < k.Size * SplitFactor)
        {
            int l = k.Level + 1, x = k.X * 2, z = k.Z * 2;
            Visit(new ChunkKey(l, x, z), p, above, outSet);
            Visit(new ChunkKey(l, x + 1, z), p, above, outSet);
            Visit(new ChunkKey(l, x, z + 1), p, above, outSet);
            Visit(new ChunkKey(l, x + 1, z + 1), p, above, outSet);
        }
        else outSet.Add(k);
    }

    private void OnBuilt(Chunk chunk, ChunkMeshData data)
    {
        if (!_chunks.TryGetValue(chunk.Key, out var cur) || cur != chunk) return;
        chunk.Job = null;
        var pos = new Vector3(chunk.Key.MinX, 0, chunk.Key.MinZ);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = data.Normals;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, Game.TerrainMaterial);
        chunk.Mesh = new MeshInstance3D { Mesh = mesh, Position = pos, Visible = false };
        AddChild(chunk.Mesh);

        if (data.WaterVertices != null)
        {
            var wa = new Godot.Collections.Array();
            wa.Resize((int)Mesh.ArrayType.Max);
            wa[(int)Mesh.ArrayType.Vertex] = data.WaterVertices;
            wa[(int)Mesh.ArrayType.Index] = data.WaterIndices;
            var wm = new ArrayMesh();
            wm.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, wa);
            wm.SurfaceSetMaterial(0, Game.WaterMaterial);
            chunk.Water = new MeshInstance3D
            {
                Mesh = wm, Position = pos, Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(chunk.Water);
        }
        chunk.Ready = true;
        _dirty = true;
    }

    private static void SetVisible(Chunk c, bool v)
    {
        c.Visible = v;
        if (c.Mesh != null) c.Mesh.Visible = v;
        if (c.Water != null) c.Water.Visible = v;
    }

    private void RemoveChunk(Chunk c)
    {
        if (c.Job != null) c.Job.Cancelled = true;
        c.Mesh?.QueueFree();
        c.Water?.QueueFree();
        _chunks.Remove(c.Key);
    }
}
