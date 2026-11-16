using System.Collections.Generic;
using Godot;

namespace Heimskringla.Core;

/// <summary>En modellvariant: mesh (med Godots automatiske LOD) og grunnskala.</summary>
public sealed class ModelVariant
{
    public Mesh Mesh;
    public Aabb Aabb;
    public float BaseScale = 1f;
}

/// <summary>Laster mesher fra importerte glTF/GLB-scener (res://assets/...).</summary>
public static class ModelLibrary
{
    private static readonly Dictionary<string, Mesh> Cache = new();

    public static bool Exists(string path) => ResourceLoader.Exists(path);

    /// <summary>Henter første mesh i en importert scene. Returnerer null hvis filen mangler.</summary>
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

    /// <summary>Laster en serie, f.eks. Pine_{0}.gltf for 1..5. Hopper over manglende filer.</summary>
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

    /// <summary>Kopi av meshen med samme materiale på alle overflater.</summary>
    public static Mesh WithMaterialAll(Mesh mesh, Material mat)
    {
        var copy = (Mesh)mesh.Duplicate();
        if (copy is ArrayMesh am)
            for (int s = 0; s < am.GetSurfaceCount(); s++) am.SurfaceSetMaterial(s, mat);
        return copy;
    }

    /// <summary>Kopi av meshen med nytt materiale på en gitt overflate.</summary>
    public static Mesh WithMaterial(Mesh mesh, int surface, Material mat)
    {
        var copy = (Mesh)mesh.Duplicate();
        if (copy is ArrayMesh am) am.SurfaceSetMaterial(surface, mat);
        return copy;
    }
}
