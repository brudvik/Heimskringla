using System.Collections.Generic;
using Godot;

namespace Heimskringla.Core;

/// <summary>A model variant: mesh (with Godot's automatic LOD) and base scale.</summary>
public sealed class ModelVariant
{
    public Mesh Mesh;
    public Aabb Aabb;
    public float BaseScale = 1f;
    /// <summary>Scanned "shells": the direction the front faces (object space, xz), or null. Set from the slope.</summary>
    public Vector3? FaceDir;
    /// <summary>Sinking (fraction of the height) that overrides PropDef.Sink, or negative for default.</summary>
    public float SinkFrac = -1f;

    /// <summary>Average of all normals in the mesh (per vertex): where the front faces.</summary>
    public static Vector3 AverageNormal(Mesh mesh)
    {
        var sum = Vector3.Zero;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arr = mesh.SurfaceGetArrays(s);
            // The normals (not the winding, which Godot flips on import)
            foreach (var nrm in arr[(int)Mesh.ArrayType.Normal].AsVector3Array()) sum += nrm;
        }
        return sum.LengthSquared() > 0f ? sum.Normalized() : Vector3.Back;
    }
}

/// <summary>Loads meshes from imported glTF/GLB scenes (res://assets/...).</summary>
public static class ModelLibrary
{
    private static readonly Dictionary<string, Mesh> Cache = new();

    public static bool Exists(string path) => ResourceLoader.Exists(path);

    /// <summary>Gets the first mesh in an imported scene. Returns null if the file is missing.</summary>
    public static Mesh LoadMesh(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        if (!ResourceLoader.Exists(path)) return null;
        var scene = GD.Load<PackedScene>(path);
        var root = scene.Instantiate();
        Mesh mesh = null;
        foreach (var n in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            mesh = ((MeshInstance3D)n).Mesh;
            break;
        }
        if (mesh == null && root is MeshInstance3D rmi) mesh = rmi.Mesh;
        root.Free();
        Cache[path] = mesh;
        return mesh;
    }

    public static ModelVariant Variant(string path, float baseScale = 1f)
    {
        var mesh = LoadMesh(path);
        return mesh == null ? null : new ModelVariant { Mesh = mesh, Aabb = mesh.GetAabb(), BaseScale = baseScale };
    }

    /// <summary>Loads a series, e.g. Pine_{0}.gltf for 1..5. Skips missing files.</summary>
    public static List<ModelVariant> Series(string pattern, int from, int to, float baseScale = 1f)
    {
        var list = new List<ModelVariant>();
        for (int i = from; i <= to; i++)
        {
            var v = Variant(string.Format(pattern, i), baseScale);
            if (v != null) list.Add(v);
        }
        return list;
    }

    /// <summary>Copy of the mesh with the same material on all surfaces.</summary>
    public static Mesh WithMaterialAll(Mesh mesh, Material mat)
    {
        var copy = (Mesh)mesh.Duplicate();
        if (copy is ArrayMesh am)
            for (int s = 0; s < am.GetSurfaceCount(); s++) am.SurfaceSetMaterial(s, mat);
        return copy;
    }

    /// <summary>Copy of the mesh with a new material on a given surface.</summary>
    public static Mesh WithMaterial(Mesh mesh, int surface, Material mat)
    {
        var copy = (Mesh)mesh.Duplicate();
        if (copy is ArrayMesh am) am.SurfaceSetMaterial(surface, mat);
        return copy;
    }
}
