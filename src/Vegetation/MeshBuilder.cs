using System;
using System.Collections.Generic;
using Godot;

namespace Heimskringla.Vegetation;

/// <summary>
/// Enkel byggeklasse for flatskyggede low-poly-mesher med vertexfarger.
/// Trekanter orienteres automatisk utover fra et oppgitt senterpunkt.
/// </summary>
public sealed class MeshBuilder
{
    private readonly List<Vector3> _v = new();
    private readonly List<Vector3> _n = new();
    private readonly List<Color> _c = new();
    public Transform3D Xf = Transform3D.Identity;

    public void Tri(Vector3 a, Vector3 b, Vector3 c, Color col, Vector3 center)
    {
        a = Xf * a; b = Xf * b; c = Xf * c; center = Xf * center;
        var n = (c - a).Cross(b - a);
        if (n.Dot((a + b + c) / 3f - center) < 0f)
        {
            (b, c) = (c, b);
            n = -n;
        }
        n = n.Normalized();
        _v.Add(a); _v.Add(b); _v.Add(c);
        _n.Add(n); _n.Add(n); _n.Add(n);
        _c.Add(col); _c.Add(col); _c.Add(col);
    }

    /// <summary>Vertikalt prisme/kjegle fra y0 til y0+h, sentrert på (bx, bz).</summary>
    public void Prism(Vector3 basePos, int seg, float r0, float r1, float h, Color col, Random rng = null,
                      float colJitter = 0f, Color? capColor = null, bool bottomCap = true, float twist = 0f)
    {
        var center = basePos + new Vector3(0, h * 0.5f, 0);
        var top = basePos + new Vector3(0, h, 0);
        var cap = capColor ?? col;
        for (int s = 0; s < seg; s++)
        {
            float a0 = Mathf.Tau * s / seg, a1 = Mathf.Tau * (s + 1) / seg;
            var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
            var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
            var t0 = new Vector3(Mathf.Cos(a0 + twist), 0, Mathf.Sin(a0 + twist));
            var t1 = new Vector3(Mathf.Cos(a1 + twist), 0, Mathf.Sin(a1 + twist));
            var b0 = basePos + d0 * r0; var b1 = basePos + d1 * r0;
            Color fc = Jitter(col, rng, colJitter);
            if (r1 <= 0.0001f)
            {
                Tri(b0, b1, top, fc, center);
            }
            else
            {
                var u0 = top + t0 * r1; var u1 = top + t1 * r1;
                Tri(b0, b1, u0, fc, center);
                Tri(b1, u1, u0, fc, center);
                Tri(top, u0, u1, cap, center);
            }
            if (bottomCap) Tri(basePos, b1, b0, r1 <= 0.0001f ? fc.Darkened(0.25f) : cap, center);
        }
    }

    public void Icosphere(Vector3 center, Vector3 radius, int subdiv, Color col, Random rng, float jitter, float colJitter)
    {
        var (verts, faces) = IcoData(subdiv);
        var pts = new Vector3[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            float j = rng != null ? 1f + ((float)rng.NextDouble() - 0.5f) * 2f * jitter : 1f;
            pts[i] = center + verts[i] * radius * j;
        }
        foreach (var f in faces)
            Tri(pts[f.Item1], pts[f.Item2], pts[f.Item3], Jitter(col, rng, colJitter), center);
    }

    private static Color Jitter(Color c, Random rng, float amount)
    {
        if (rng == null || amount <= 0f) return c;
        float k = 1f + ((float)rng.NextDouble() - 0.5f) * 2f * amount;
        return new Color(c.R * k, c.G * k, c.B * k);
    }

    public ArrayMesh ToMesh(Material mat)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _v.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = _n.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = _c.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, mat);
        return mesh;
    }

    // ---------------- Icosphere-data (cachet per subdiv-nivå)
    private static readonly Dictionary<int, (List<Vector3>, List<(int, int, int)>)> IcoCache = new();

    private static (List<Vector3>, List<(int, int, int)>) IcoData(int subdiv)
    {
        lock (IcoCache)
        {
            if (IcoCache.TryGetValue(subdiv, out var cached)) return cached;
            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            var v = new List<Vector3>
            {
                new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
                new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
                new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
            };
            for (int i = 0; i < v.Count; i++) v[i] = v[i].Normalized();
            var f = new List<(int, int, int)>
            {
                (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
                (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
                (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
                (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
            };
            for (int s = 0; s < subdiv; s++)
            {
                var mid = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (mid.TryGetValue(key, out int m)) return m;
                    v.Add(((v[a] + v[b]) * 0.5f).Normalized());
                    mid[key] = v.Count - 1;
                    return v.Count - 1;
                }
                var nf = new List<(int, int, int)>();
                foreach (var (a, b, c) in f)
                {
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    nf.Add((a, ab, ca)); nf.Add((b, bc, ab)); nf.Add((c, ca, bc)); nf.Add((ab, bc, ca));
                }
                f = nf;
            }
            var res = (v, f);
            IcoCache[subdiv] = res;
            return res;
        }
    }
}
