using System.Collections.Generic;
using Godot;
using Heimskringla.World;

namespace Heimskringla.Environment;

/// <summary>
/// Havflaten: et tett rutenett rundt kameraet (bølgene trenger hjørnepunkter) som blir glissent
/// utover, pluss store flater ut til horisonten. Følger kameraet i hele rutesteg, så bølgene står stille
/// i verden mens nettet flyttes.
/// </summary>
public partial class Ocean : MeshInstance3D
{
    private const int Quads = 256;          // per akse i det tette nettet
    private const float Extent = 1600f;     // halv bredde på det tette nettet (m)
    private const float Far = 22000f;       // halv bredde totalt
    private const float Snap = 8f;          // flytt i steg på 8 m

    public Ocean(Material mat)
    {
        Name = "Ocean";
        Mesh = Build();
        MaterialOverride = mat;
        CastShadow = ShadowCastingSetting.Off;
        // Bølgene forskyver punktene; ikke la Godot kutte nettet bort
        ExtraCullMargin = 20f;
    }

    private static float Warp(float u)
    {
        // u i [-1, 1] -> meter. Tett nær midten (~0.9 m), glissent ytterst.
        float a = Mathf.Abs(u);
        return Mathf.Sign(u) * Extent * (0.09f * a + 0.91f * a * a * a);
    }

    private static ArrayMesh Build()
    {
        int n = Quads + 1;
        var v = new List<Vector3>(n * n + 16);
        var idx = new List<int>(Quads * Quads * 6 + 64);
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
            v.Add(new Vector3(Warp(i * 2f / Quads - 1f), 0, Warp(j * 2f / Quads - 1f)));
        for (int j = 0; j < Quads; j++)
        for (int i = 0; i < Quads; i++)
        {
            int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
            idx.Add(a); idx.Add(b); idx.Add(c);
            idx.Add(b); idx.Add(d); idx.Add(c);
        }
        // Ytre ramme: kantpunktene i nettet kobles til et stort kvadrat (ingen sprekker)
        int o = v.Count;
        v.Add(new Vector3(-Far, 0, -Far)); v.Add(new Vector3(Far, 0, -Far));
        v.Add(new Vector3(Far, 0, Far)); v.Add(new Vector3(-Far, 0, Far));
        for (int k = 0; k < Quads; k++)
        {
            // nord (j = 0)
            idx.Add(k); idx.Add(o); idx.Add(k + 1);
            // sør (j = n-1)
            int s0 = (n - 1) * n + k;
            idx.Add(s0); idx.Add(s0 + 1); idx.Add(o + 3);
            // vest (i = 0)
            int w0 = k * n;
            idx.Add(w0); idx.Add(w0 + n); idx.Add(o);
            // øst (i = n-1)
            int e0 = k * n + n - 1;
            idx.Add(e0); idx.Add(o + 1); idx.Add(e0 + n);
        }
        // Hjørnetrekantene
        idx.Add(n - 1); idx.Add(o); idx.Add(o + 1);
        idx.Add((n - 1) * n + n - 1); idx.Add(o + 2); idx.Add(o + 3);
        idx.Add((n - 1) * n); idx.Add(o + 3); idx.Add(o);
        idx.Add(n * n - 1); idx.Add(o + 1); idx.Add(o + 2);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        var norms = new Vector3[v.Count];
        System.Array.Fill(norms, Vector3.Up);
        arrays[(int)Mesh.ArrayType.Normal] = norms;
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(new Vector3(-Far, -5, -Far), new Vector3(Far * 2, 10, Far * 2));
        return mesh;
    }

    public override void _Process(double delta)
    {
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return;
        var p = cam.GlobalPosition;
        GlobalPosition = new Vector3(Mathf.Round(p.X / Snap) * Snap, WorldSettings.SeaLevel, Mathf.Round(p.Z / Snap) * Snap);
    }
}
