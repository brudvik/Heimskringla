using System;
using System.Collections.Generic;
using Godot;
using Heimskringla.Core;

namespace Heimskringla.Vegetation;

/// <summary>
/// Procedurally generated deciduous trees (birch and oak) in the same style as the spruce (SpruceBuilder):
/// branch structure (trunk, main branches, side branches as tapered tubes) with leaf cards that have a
/// painted twig-with-leaves texture. Gives ragged, airy crowns instead of round "balloons".
///
///  - Birch (silver birch): slender white trunk, branches angled upward and twigs hanging
///    down (tall, narrow cards hanging from the branches), light and airy crown.
///  - Oak: short, thick trunk, broad, gnarled main branches reaching out almost horizontally, dense and wide crown.
///
/// Surface 0 = bark (birch: the birch_bark shader, oak: "Bark" → the foliage shader's bark detail),
/// surface 1 = foliage ("Leaves", alpha cards). LOD levels as index lists (like the spruce).
/// </summary>
public static class LeafTreeBuilder
{
    public enum Species { Birch, Oak }

    private static ImageTexture _leafTex;

    public static List<ModelVariant> Variants(Species sp, Material barkMat)
    {
        var leaves = new StandardMaterial3D
        {
            ResourceName = "Leaves",
            AlbedoTexture = _leafTex ??= LeafTexture(),
            AlbedoColor = sp == Species.Birch ? new Color(0.94f, 0.98f, 0.78f) : new Color(0.74f, 0.82f, 0.66f),
            Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
            AlphaScissorThreshold = 0.5f,
            Roughness = 0.75f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = false,
        };
        var list = new List<ModelVariant>();
        var heights = sp == Species.Birch ? new[] { 11f, 13.5f, 15.5f } : new[] { 10f, 12f };
        int seed = sp == Species.Birch ? 100 : 200;
        foreach (float h in heights)
        {
            var mesh = Build(sp, h, seed++, barkMat, leaves);
            list.Add(new ModelVariant { Mesh = mesh, Aabb = mesh.GetAabb(), BaseScale = 0.85f });
        }
        return list;
    }

    // ------------------------------------------------------------ Geometry
    private sealed class Geo
    {
        public readonly List<Vector3> V = new(), N = new();
        public readonly List<Vector2> UV = new();
        public readonly List<Color> C = new();
        public readonly List<int> L0 = new(), L1 = new(), L2 = new();
    }

    private static ArrayMesh Build(Species sp, float H, int seed, Material bark, Material leaves)
    {
        var rng = new Random(seed * 7919);
        float R() => (float)rng.NextDouble();
        bool birch = sp == Species.Birch;
        var wood = new Geo();
        var leaf = new Geo();

        // ---- Trunk: slightly curved line
        float r0 = birch ? H * 0.012f : H * 0.032f;
        float trunkTop = birch ? H * 0.97f : H * 0.62f;
        var lean = new Vector3(R() - 0.5f, 0, R() - 0.5f) * (birch ? 0.6f : 0.4f);
        Vector3 TrunkAt(float t) => new Vector3(0, t * trunkTop, 0) + lean * (t * t) + new Vector3(Mathf.Sin(t * 5f + seed) * 0.08f, 0, Mathf.Cos(t * 4f + seed) * 0.08f) * t;
        var trunkPts = new List<Vector3>();
        var trunkR = new List<float>();
        for (int k = 0; k <= 8; k++)
        {
            float t = k / 8f;
            trunkPts.Add(TrunkAt(t));
            trunkR.Add(r0 * (1f - t * (birch ? 0.88f : 0.55f)) * (1f + 0.5f * Mathf.Exp(-t * 18f)));
        }
        Tube(wood, trunkPts, trunkR, 8, true, true, true);

        // ---- Main branches
        int mains = birch ? 15 : 9;
        float crownStart = birch ? 0.32f : 0.5f;
        float yaw0 = R() * Mathf.Tau;
        for (int i = 0; i < mains; i++)
        {
            float t = birch ? Mathf.Lerp(crownStart, 0.95f, (i + R() * 0.6f) / mains) : Mathf.Lerp(0.82f, 1.0f, R());
            var start = TrunkAt(Mathf.Min(t, 1f));
            float yaw = yaw0 + i * 2.39996f + (R() - 0.5f) * 0.5f;
            var outDir = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
            // Crown profile: birch narrow and egg-shaped; oak wide
            float rel = (t - crownStart) / (1f - crownStart);
            float len = birch ? H * 0.28f * Mathf.Sin(Mathf.Pi * Mathf.Clamp(0.25f + rel * 0.8f, 0f, 1f)) * (0.75f + R() * 0.4f) + 0.8f
                              : H * (0.42f + R() * 0.18f);
            float up = birch ? Mathf.Lerp(0.9f, 1.6f, rel) : 0.45f + R() * 0.35f;   // slope (y per horizontal)
            var dir = (outDir + Vector3.Up * up).Normalized();
            var pts = new List<Vector3>();
            var rad = new List<float>();
            float br0 = (birch ? r0 * 0.32f : r0 * 0.45f) * (0.7f + R() * 0.3f);
            for (int k = 0; k <= 4; k++)
            {
                float s = k / 4f;
                // Birch: the tip bends outward/down; oak: gnarled, curving slightly up and out
                var p = start + dir * (len * s) + Vector3.Down * (birch ? len * 0.35f * s * s : len * 0.12f * s * s)
                        + new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f) * (birch ? 0.12f : 0.5f) * s;
                pts.Add(p);
                rad.Add(br0 * (1f - s * 0.8f));
            }
            Tube(wood, pts, rad, 5, i % 2 == 0, true, false);

            // Side branches and foliage
            int subs = birch ? 3 : 5;
            for (int j = 0; j < subs; j++)
            {
                float s0 = 0.35f + 0.6f * (j + R() * 0.5f) / subs;
                var sp0 = Sample(pts, s0);
                float syaw = yaw + (R() - 0.5f) * 2.2f;
                var sdir = (new Vector3(Mathf.Cos(syaw), 0, Mathf.Sin(syaw)) + Vector3.Up * (birch ? 0.5f : 0.35f)).Normalized();
                float slen = len * (birch ? 0.35f : 0.45f) * (0.7f + R() * 0.5f);
                var sub = new List<Vector3> { sp0, sp0 + sdir * slen * 0.5f + Vector3.Down * slen * 0.05f, sp0 + sdir * slen + Vector3.Down * slen * (birch ? 0.25f : 0.1f) };
                var srad = new List<float> { br0 * 0.45f, br0 * 0.3f, br0 * 0.12f };
                Tube(wood, sub, srad, 4, j == 0, false, false);
                LeafCluster(leaf, rng, sub, birch, outDir, j);
            }
            LeafCluster(leaf, rng, pts.GetRange(2, 3), birch, outDir, i);
        }
        // The top (birch: pointed top with foliage)
        if (birch)
            LeafCluster(leaf, rng, new List<Vector3> { TrunkAt(0.85f), TrunkAt(1f) }, true, Vector3.Right, 0);

        var mesh = new ArrayMesh();
        Add(mesh, wood, bark);
        Add(mesh, leaf, leaves);
        return mesh;
    }

    private static Vector3 Sample(List<Vector3> pts, float s)
    {
        float f = s * (pts.Count - 1);
        int i = Math.Min((int)f, pts.Count - 2);
        return pts[i].Lerp(pts[i + 1], f - i);
    }

    /// <summary>Tapered tube along the points. lod1/lod2 = include in the LOD levels.</summary>
    private static void Tube(Geo g, List<Vector3> pts, List<float> rad, int sides, bool lod1, bool lod2Main, bool trunk)
    {
        int b0 = g.V.Count;
        for (int k = 0; k < pts.Count; k++)
        {
            var dir = (k < pts.Count - 1 ? pts[k + 1] - pts[k] : pts[k] - pts[k - 1]).Normalized();
            var a = Mathf.Abs(dir.Y) < 0.9f ? Vector3.Up : Vector3.Right;
            var u = dir.Cross(a).Normalized();
            var w = dir.Cross(u).Normalized();
            for (int s = 0; s <= sides; s++)
            {
                float ang = s * Mathf.Tau / sides;
                var n = u * Mathf.Cos(ang) + w * Mathf.Sin(ang);
                g.V.Add(pts[k] + n * rad[k]); g.N.Add(n); g.UV.Add(new Vector2((float)s / sides, pts[k].Y)); g.C.Add(Colors.White);
            }
        }
        for (int k = 0; k < pts.Count - 1; k++)
        for (int s = 0; s < sides; s++)
        {
            int a = b0 + k * (sides + 1) + s, b = a + sides + 1;
            // Clockwise seen from outside (Godot's front face)
            int[] tri = { a, b, a + 1, a + 1, b, b + 1 };
            // The order above gives the front face outward given n = u*cos + w*sin with w = dir x u
            g.L0.AddRange(tri);
            if (lod1) g.L1.AddRange(tri);
            if (trunk || (lod2Main && k < 2)) g.L2.AddRange(tri);
        }
    }

    /// <summary>Leaf cards along a branch. Birch: hanging twigs (tall cards); oak: dense clusters.</summary>
    private static void LeafCluster(Geo g, Random rng, List<Vector3> branch, bool birch, Vector3 outDir, int idx)
    {
        float R() => (float)rng.NextDouble();
        int count = birch ? 5 : 7;
        for (int c = 0; c < count; c++)
        {
            var p = Sample(branch, 0.15f + 0.85f * (c + R()) / count);
            float shade = 0.72f + R() * 0.3f;
            // LOD: cards no. 0,2,4 are included in LOD1; 0 and 3 in LOD2
            bool l1 = c % 2 == 0 || c == count - 1, l2 = c % 3 == 0;
            if (birch)
            {
                // Hanging twig: the card hangs straight down from the branch, randomly twisted
                float yaw = R() * Mathf.Tau;
                var side = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
                float w = 0.45f + R() * 0.3f, h = 1.3f + R() * 0.9f;
                var top = p + Vector3.Up * 0.1f;
                var bot = p + Vector3.Down * h + outDir * 0.25f;
                Card(g, top - side * w, top + side * w, bot - side * w * 0.8f, bot + side * w * 0.8f, shade, l1, l2);
            }
            else
            {
                // Cluster: cards facing out and up, randomly twisted
                var n = (outDir * (0.4f + R() * 0.6f) + Vector3.Up * (0.6f + R()) + new Vector3(R() - 0.5f, 0, R() - 0.5f)).Normalized();
                var t1 = n.Cross(Mathf.Abs(n.Y) < 0.95f ? Vector3.Up : Vector3.Right).Normalized();
                var t2 = n.Cross(t1).Normalized();
                float rot = R() * Mathf.Tau;
                var a = t1 * Mathf.Cos(rot) + t2 * Mathf.Sin(rot);
                var b = n.Cross(a).Normalized();
                float s = 0.9f + R() * 0.6f;
                var center = p + new Vector3(R() - 0.5f, R() * 0.4f, R() - 0.5f) * 0.6f;
                Card(g, center - a * s + b * s, center + a * s + b * s, center - a * s - b * s, center + a * s - b * s, shade, l1, l2);
            }
        }
    }

    /// <summary>Quad card: tl/tr at the top (UV.y = 0), bl/br at the bottom.</summary>
    private static void Card(Geo g, Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br, float shade, bool l1, bool l2)
    {
        int b0 = g.V.Count;
        var raw = (tr - tl).Cross(bl - tl).Normalized();
        var n = raw.Y < 0f ? -raw : raw;
        // Godot: the front face is clockwise seen from where the normal points (otherwise the normal is flipped and
        // the top side of the card becomes dark)
        bool flip = raw.Dot(n) > 0f;
        var col = new Color(shade, shade, shade);
        g.V.Add(tl); g.UV.Add(new Vector2(0, 0));
        g.V.Add(tr); g.UV.Add(new Vector2(1, 0));
        g.V.Add(bl); g.UV.Add(new Vector2(0, 1));
        g.V.Add(br); g.UV.Add(new Vector2(1, 1));
        for (int i = 0; i < 4; i++) { g.N.Add(n); g.C.Add(col); }
        int[] tri = flip ? new[] { b0, b0 + 2, b0 + 1, b0 + 1, b0 + 2, b0 + 3 } : new[] { b0, b0 + 1, b0 + 2, b0 + 1, b0 + 3, b0 + 2 };
        g.L0.AddRange(tri);
        if (l1) g.L1.AddRange(tri);
        if (l2) g.L2.AddRange(tri);
    }

    private static void Add(ArrayMesh mesh, Geo g, Material mat)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = g.V.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = g.N.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = g.UV.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = g.C.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = g.L0.ToArray();
        var lods = new Godot.Collections.Dictionary { { 0.12f, g.L1.ToArray() }, { 0.35f, g.L2.ToArray() } };
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, lods);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
    }

    // ------------------------------------------------------------ Leaf texture
    /// <summary>
    /// A twig hanging from the top of the image, with small, pointed egg-shaped leaves (birch leaves)
    /// on short stalks along the twig and some side shoots. Color variation per leaf; light midrib.
    /// </summary>
    private static ImageTexture LeafTexture()
    {
        const int S = 256;
        var rgb = new Vector3[S * S];
        var alpha = new float[S * S];
        var rng = new Random(777);
        float R() => (float)rng.NextDouble();

        void Plot(int x, int y, Vector3 c, float cov)
        {
            if (x < 0 || y < 0 || x >= S || y >= S || cov <= 0f) return;
            int k = y * S + x;
            rgb[k] = rgb[k].Lerp(c, Mathf.Min(cov, 1f));
            alpha[k] = Mathf.Max(alpha[k], Mathf.Min(cov, 1f));
        }

        void Line(Vector2 a, Vector2 b, float w, Vector3 c)
        {
            int steps = Mathf.Max(1, (int)(a.DistanceTo(b) * 1.5f));
            for (int i = 0; i <= steps; i++)
            {
                var p = a.Lerp(b, (float)i / steps);
                int r = Mathf.CeilToInt(w);
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    float d = new Vector2(dx, dy).Length();
                    Plot((int)p.X + dx, (int)p.Y + dy, c, w - d + 0.5f);
                }
            }
        }

        void Leaf(Vector2 at, Vector2 dir, float len)
        {
            var perp = new Vector2(-dir.Y, dir.X);
            float l = 0.55f + R() * 0.45f;
            var c = new Vector3(0.1f, 0.24f, 0.05f) * (0.7f + l * 0.6f);
            if (R() < 0.15f) c = new Vector3(0.2f, 0.32f, 0.07f);
            float wid = len * 0.42f;
            int n = (int)(len * 1.2f);
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n;
                // Egg shape with pointed tip
                float half = wid * Mathf.Sin(Mathf.Pi * Mathf.Pow(t, 0.75f)) * (1f - t * 0.25f);
                var mid = at + dir * (t * len);
                for (float s = -half; s <= half; s += 0.5f)
                {
                    var p = mid + perp * s;
                    float edge = Mathf.Clamp(half - Mathf.Abs(s), 0f, 1f);
                    // Lighter midrib, darker edge
                    var cc = c * (1f + 0.25f * (1f - Mathf.Abs(s) / Mathf.Max(half, 0.01f))) * (Mathf.Abs(s) < 0.6f ? 1.35f : 1f);
                    Plot((int)p.X, (int)p.Y, cc, edge + 0.3f);
                }
            }
        }

        var twig = new Vector3(0.28f, 0.2f, 0.14f);
        // Main twig from the top down, slightly swaying
        var prev = new Vector2(S * 0.5f, 0);
        var pts = new List<Vector2> { prev };
        for (int i = 1; i <= 10; i++)
        {
            var p = new Vector2(S * 0.5f + Mathf.Sin(i * 0.7f) * 10f, i * (S - 8) / 10f);
            Line(prev, p, 1.3f - i * 0.08f, twig);
            pts.Add(p);
            prev = p;
        }
        // Side shoots
        for (int k = 0; k < 7; k++)
        {
            int i = 1 + k;
            var a = pts[i];
            float side = k % 2 == 0 ? 1f : -1f;
            var b = a + new Vector2(side * (35f + R() * 30f), 25f + R() * 25f);
            Line(a, b, 0.8f, twig);
            for (int m = 0; m < 4; m++)
            {
                var p = a.Lerp(b, 0.3f + m * 0.22f);
                var d = new Vector2(side * (0.4f + R() * 0.5f), 0.6f + R() * 0.4f).Normalized();
                if (m % 2 == 1) d = new Vector2(-d.X * 0.5f, d.Y).Normalized();
                Leaf(p, d, 18f + R() * 10f);
            }
            Leaf(b, (b - a).Normalized(), 20f + R() * 8f);
        }
        // Leaves along the main twig
        for (int i = 1; i < pts.Count; i++)
            for (int s = -1; s <= 1; s += 2)
                Leaf(pts[i], new Vector2(s * (0.6f + R() * 0.3f), 0.7f).Normalized(), 18f + R() * 10f);

        var mean = Vector3.Zero; float wsum = 0f;
        for (int k = 0; k < rgb.Length; k++) { mean += rgb[k] * alpha[k]; wsum += alpha[k]; }
        mean /= Mathf.Max(wsum, 1f);
        var data = new byte[S * S * 4];
        for (int k = 0; k < rgb.Length; k++)
        {
            var c = alpha[k] > 0.01f ? rgb[k] : mean;
            data[k * 4] = (byte)(Mathf.Clamp(Mathf.Pow(c.X, 1f / 2.2f), 0f, 1f) * 255f);
            data[k * 4 + 1] = (byte)(Mathf.Clamp(Mathf.Pow(c.Y, 1f / 2.2f), 0f, 1f) * 255f);
            data[k * 4 + 2] = (byte)(Mathf.Clamp(Mathf.Pow(c.Z, 1f / 2.2f), 0f, 1f) * 255f);
            data[k * 4 + 3] = (byte)(Mathf.Clamp(alpha[k], 0f, 1f) * 255f);
        }
        var img = Image.CreateFromData(S, S, false, Image.Format.Rgba8, data);
        img.GenerateMipmaps();
        SpruceBuilder.PreserveCoverage(img, 0.5f);
        return ImageTexture.CreateFromImage(img);
    }
}
