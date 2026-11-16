using System.Collections.Generic;
using Godot;
using Heimskringla.World;

namespace Heimskringla.Terrain;

public sealed class ChunkMeshData
{
    public Vector3[] Vertices;
    public Vector3[] Normals;
    public Color[] Colors;
    public int[] Indices;
    public Vector3[] WaterVertices;
    public int[] WaterIndices;
}

/// <summary>Bygger mesh-data for én terreng-chunk. Kjøres i bakgrunnstråd.</summary>
public static class TerrainMeshBuilder
{
    public const int Quads = 64;

    public static ChunkMeshData Build(WorldGenerator w, float x0, float z0, float size)
    {
        int n = Quads + 1;
        float step = size / Quads;
        int nb = n + 2;
        var h = new float[nb * nb];
        for (int j = 0; j < nb; j++)
        for (int i = 0; i < nb; i++)
            h[j * nb + i] = w.GetHeight(x0 + (i - 1) * step, z0 + (j - 1) * step);

        int skirtCount = 4 * n;
        var verts = new Vector3[n * n + skirtCount];
        var normals = new Vector3[verts.Length];
        var colors = new Color[verts.Length];

        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int hb = (j + 1) * nb + i + 1;
            float hc = h[hb];
            float hl = h[hb - 1], hr = h[hb + 1], hd = h[hb - nb], hu = h[hb + nb];
            var nrm = new Vector3(hl - hr, 2f * step, hd - hu).Normalized();
            float x = x0 + i * step, z = z0 + j * step;
            // Normal for fargeformål beregnes fra 1 m-skala nært, ellers fra mesh-oppløsningen
            Color c = Biomes.GroundColor(hc, w.GetTemp(x, z), w.GetMoist(x, z), nrm.Y);
            float cn = 1f + w.ColorNoise(x, z) * 0.07f;
            int v = j * n + i;
            verts[v] = new Vector3(i * step, hc, j * step);
            normals[v] = nrm;
            colors[v] = new Color(c.R * cn, c.G * cn, c.B * cn);
        }

        var idx = new List<int>(Quads * Quads * 6 + skirtCount * 6);
        for (int j = 0; j < Quads; j++)
        for (int i = 0; i < Quads; i++)
        {
            int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
            idx.Add(a); idx.Add(b); idx.Add(c);
            idx.Add(b); idx.Add(d); idx.Add(c);
        }

        // Skjørt rundt kanten skjuler sprekker mellom ulike LOD-nivåer
        float skirt = step * 5f + 2f;
        int sv = n * n;
        var edges = new int[4][];
        for (int e = 0; e < 4; e++) edges[e] = new int[n];
        for (int k = 0; k < n; k++)
        {
            edges[0][k] = k;                 // z = 0
            edges[1][k] = (n - 1) * n + k;   // z = max
            edges[2][k] = k * n;             // x = 0
            edges[3][k] = k * n + n - 1;     // x = max
        }
        foreach (var edge in edges)
        {
            int start = sv;
            for (int k = 0; k < n; k++)
            {
                int src = edge[k];
                verts[sv] = verts[src] - new Vector3(0, skirt, 0);
                normals[sv] = normals[src];
                colors[sv] = colors[src];
                sv++;
            }
            for (int k = 0; k < n - 1; k++)
            {
                int e0 = edge[k], e1 = edge[k + 1], s0 = start + k, s1 = start + k + 1;
                idx.Add(e0); idx.Add(e1); idx.Add(s0);
                idx.Add(e1); idx.Add(s1); idx.Add(s0);
            }
        }

        var data = new ChunkMeshData { Vertices = verts, Normals = normals, Colors = colors, Indices = idx.ToArray() };
        BuildWater(w, x0, z0, step, n, h, nb, data);
        return data;
    }

    private static void BuildWater(WorldGenerator w, float x0, float z0, float step, int n, float[] h, int nb, ChunkMeshData data)
    {
        var lw = new float[n * n];
        bool any = false;
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            float l = w.GetLakeWater(x0 + i * step, z0 + j * step);
            lw[j * n + i] = l;
            if (l > h[(j + 1) * nb + i + 1]) any = true;
        }
        if (!any) return;

        var remap = new int[n * n];
        System.Array.Fill(remap, -1);
        var wv = new List<Vector3>();
        var wi = new List<int>();
        int Vert(int k)
        {
            if (remap[k] < 0)
            {
                remap[k] = wv.Count;
                wv.Add(new Vector3((k % n) * step, lw[k], (k / n) * step));
            }
            return remap[k];
        }
        bool Wet(int k) => lw[k] > h[(k / n + 1) * nb + (k % n) + 1];
        void Tri(int a, int b, int c)
        {
            if (lw[a] <= WorldGenerator.NoWater || lw[b] <= WorldGenerator.NoWater || lw[c] <= WorldGenerator.NoWater) return;
            if (!Wet(a) && !Wet(b) && !Wet(c)) return;
            wi.Add(Vert(a)); wi.Add(Vert(b)); wi.Add(Vert(c));
        }
        for (int j = 0; j < n - 1; j++)
        for (int i = 0; i < n - 1; i++)
        {
            int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
            Tri(a, b, c);
            Tri(b, d, c);
        }
        if (wi.Count == 0) return;
        data.WaterVertices = wv.ToArray();
        data.WaterIndices = wi.ToArray();
    }
}
