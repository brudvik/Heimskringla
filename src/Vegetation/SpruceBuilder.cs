using System;
using System.Collections.Generic;
using Godot;
using Heimskringla.Core;

namespace Heimskringla.Vegetation;

/// <summary>
/// Norway spruce (Picea abies), procedurally generated: straight, tapering trunk with whorls of branches that
/// hang down and bend the tip back up ("comb spruce"), narrow and pointed crown. Each branch is a
/// curved, V-shaped card with a painted needle texture (alpha), so the silhouette becomes ragged and lively
/// instead of flat plates.
///
/// Performance: ~1700 triangles up close, with two LOD levels in the same vertex buffer (just new index lists):
///   LOD1 (~60 m+): each branch one straight card from root to tip
///   LOD2 (~200 m+): only every other whorl
/// Surface 0 = bark ("Bark"), 1 = needles ("Needles"); PropLibrary.ApplyWind converts them to
/// the foliage shader (bark detail/moss and wind, foliage normals and seasonal colors).
/// </summary>
public static class SpruceBuilder
{
    private static ImageTexture _needles;

    public static List<ModelVariant> Variants()
    {
        var bark = new StandardMaterial3D { ResourceName = "Bark", AlbedoColor = new Color(0.4f, 0.33f, 0.28f), Roughness = 0.92f };
        var needles = new StandardMaterial3D
        {
            ResourceName = "Needles",
            AlbedoTexture = _needles ??= NeedleTexture(),
            // The foliage shader pulls conifers toward blue-green (made for the lime-green low-poly models);
            // compensate for that, so the spruce needles become dark forest green and not cyan
            AlbedoColor = new Color(1.25f, 1.0f, 0.8f),
            Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
            AlphaScissorThreshold = 0.5f,
            Roughness = 0.8f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = true,   // lightness per branch and brown, dry branches at the bottom
            VertexColorIsSrgb = false,
        };
        var list = new List<ModelVariant>();
        int seed = 1;
        foreach (float h in new[] { 12.5f, 14.5f, 16.5f })
        {
            var mesh = Build(h, seed++, bark, needles);
            list.Add(new ModelVariant { Mesh = mesh, Aabb = mesh.GetAabb(), BaseScale = 0.85f });
        }
        return list;
    }

    // ------------------------------------------------------------ Geometry
    private static ArrayMesh Build(float H, int seed, Material bark, Material needles)
    {
        var rng = new Random(seed * 7919);
        float R() => (float)rng.NextDouble();
        var mesh = new ArrayMesh();

        // ---- Trunk: 8 sides, tapering, slight bend
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var idx = new List<int>();
            const int Sides = 8, Rings = 7;
            float r0 = H * 0.012f;
            var lean = new Vector2(R() - 0.5f, R() - 0.5f) * 0.25f;
            for (int k = 0; k <= Rings; k++)
            {
                float t = (float)k / Rings;
                float y = t * H;
                // Root flare at the bottom, then almost linearly narrower toward the top
                float r = r0 * (1f - t * 0.93f) * (1f + 0.6f * Mathf.Exp(-y * 2.5f));
                var c = new Vector3(lean.X * t * t, y, lean.Y * t * t);
                for (int s = 0; s <= Sides; s++)
                {
                    float a = s * Mathf.Tau / Sides;
                    var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    v.Add(c + d * r); n.Add(d); uv.Add(new Vector2((float)s / Sides, y));
                }
            }
            for (int k = 0; k < Rings; k++)
            for (int s = 0; s < Sides; s++)
            {
                int a = k * (Sides + 1) + s, b = a + Sides + 1;
                // Godot: the front face is clockwise seen from outside (otherwise the trunk is lit from inside and looks hollow)
                idx.Add(a); idx.Add(a + 1); idx.Add(b);
                idx.Add(a + 1); idx.Add(b + 1); idx.Add(b);
            }
            AddSurface(mesh, v, n, uv, null, idx, null, bark);
        }

        // ---- Branches
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var col = new List<Color>();
            var lod0 = new List<int>(); var lod1 = new List<int>(); var lod2 = new List<int>();
            const int Seg = 3;
            float rMax = H * 0.2f;
            float yStart = H * 0.1f;
            float spacing = 0.42f;
            int whorl = 0;
            float yaw0 = R() * Mathf.Tau;
            for (float y = yStart; y < H * 0.985f; y += spacing * (0.85f + R() * 0.3f), whorl++)
            {
                float t = y / H;
                // Crown profile: narrow cone, slightly curved (widest some way up from the bottom)
                float prof = Mathf.Pow(1f - t, 0.82f) * Mathf.Min(1f, 0.55f + t * 3f);
                int count = t > 0.92f ? 3 : 6;
                // The lowest branches are dry and short (little light under the crowns)
                bool dead = t < 0.2f && R() < 0.6f;
                for (int i = 0; i < count; i++)
                {
                    float yaw = yaw0 + whorl * 2.4f + i * Mathf.Tau / count + (R() - 0.5f) * 0.7f;
                    var dir = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
                    var side = new Vector3(-dir.Z, 0, dir.X);
                    float L = (rMax * prof * (0.8f + R() * 0.4f) + 0.35f) * (dead ? 0.55f : 1f);
                    // High up the branches point upward; lower down they hang and the tip bends up
                    float a = Mathf.Lerp(-0.08f, 0.5f, Mathf.Pow(t, 1.6f));
                    float b = Mathf.Lerp(0.42f, 0.05f, t);
                    float c = Mathf.Lerp(0.2f, 0f, t);
                    float hw = L * (dead ? 0.22f : 0.5f);
                    float wing = hw * Mathf.Lerp(0.55f, 0.25f, t);   // the branches "hang" to the sides (comb spruce)
                    var root = new Vector3(0, y, 0) + dir * H * 0.012f * (1f - t);
                    int b0 = v.Count;
                    float shade = 0.75f + R() * 0.25f;
                    for (int k = 0; k <= Seg; k++)
                    {
                        float s = (float)k / Seg;
                        var p = root + dir * (L * s) + Vector3.Up * (L * (a * s - b * s * s + c * s * s * s));
                        var tangent = (dir + Vector3.Up * (a - 2f * b * s + 3f * c * s * s)).Normalized();
                        var up = tangent.Cross(side).Normalized();
                        if (up.Y < 0f) up = -up;
                        float w = hw * Mathf.Min(1f, 0.35f + s * 2f);
                        var down = Vector3.Down * wing * Mathf.Min(1f, s * 2f + 0.2f);
                        v.Add(p - side * w + down); n.Add((up - side * 0.5f).Normalized()); uv.Add(new Vector2(s, 0f));
                        v.Add(p + Vector3.Up * 0.04f * L); n.Add(up); uv.Add(new Vector2(s, 0.5f));
                        v.Add(p + side * w + down); n.Add((up + side * 0.5f).Normalized()); uv.Add(new Vector2(s, 1f));
                        var cc = dead ? new Color(0.55f, 0.42f, 0.3f) : new Color(shade, shade, shade);
                        col.Add(cc); col.Add(cc); col.Add(cc);
                    }
                    for (int k = 0; k < Seg; k++)
                    {
                        int q = b0 + k * 3, q2 = q + 3;
                        Quad(lod0, q, q + 1, q2, q2 + 1);
                        Quad(lod0, q + 1, q + 2, q2 + 1, q2 + 2);
                    }
                    int e = b0 + Seg * 3;
                    Quad(lod1, b0, b0 + 1, e, e + 1);
                    Quad(lod1, b0 + 1, b0 + 2, e + 1, e + 2);
                    if (whorl % 2 == 0 || t > 0.85f)
                    {
                        Quad(lod2, b0, b0 + 1, e, e + 1);
                        Quad(lod2, b0 + 1, b0 + 2, e + 1, e + 2);
                    }
                }
            }
            // Leader shoot: a couple of small, upright cards
            for (int i = 0; i < 2; i++)
            {
                float yaw = i * Mathf.Pi * 0.5f;
                var side = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
                int b0 = v.Count;
                var top = new Vector3(0, H * 1.03f, 0);
                var bot = new Vector3(0, H * 0.94f, 0);
                v.Add(bot - side * 0.25f); v.Add(bot + side * 0.25f); v.Add(top - side * 0.05f); v.Add(top + side * 0.05f);
                for (int k = 0; k < 4; k++) { n.Add(Vector3.Up); col.Add(Colors.White); }
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(0, 1)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1));
                foreach (var l in new[] { lod0, lod1, lod2 }) Quad(l, b0, b0 + 1, b0 + 2, b0 + 3);
            }
            var lods = new Godot.Collections.Dictionary
            {
                { 0.12f, lod1.ToArray() },
                { 0.35f, lod2.ToArray() },
            };
            AddSurface(mesh, v, n, uv, col, lod0, lods, needles);
        }
        return mesh;
    }

    private static void Quad(List<int> l, int a, int b, int c, int d)
    {
        // a-b along the root, c-d along the next ring
        l.Add(a); l.Add(c); l.Add(b);
        l.Add(b); l.Add(c); l.Add(d);
    }

    private static void AddSurface(ArrayMesh mesh, List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<Color> col, List<int> idx,
                                   Godot.Collections.Dictionary lods, Material mat)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = n.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uv.ToArray();
        if (col != null) arrays[(int)Mesh.ArrayType.Color] = col.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, lods);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
    }

    // ------------------------------------------------------------ Needle texture
    /// <summary>
    /// Draws a spruce twig seen from above: brown main axis along u, side shoots angled forward, and densely
    /// covered with short needles all around. Alpha = needles. The mipmaps are generated with preserved coverage (otherwise
    /// the branches thin out and vanish at a distance), and the color bleeds out into the transparent areas.
    /// </summary>
    private static ImageTexture NeedleTexture()
    {
        const int W = 512, Hh = 256;
        var rgb = new Vector3[W * Hh];
        var alpha = new float[W * Hh];
        var rng = new Random(4242);
        float R() => (float)rng.NextDouble();

        // Outline: narrow at the root, widest a little past the middle, pointed tip
        float HalfWidth(float u) => 0.5f * Mathf.Min(1f, 0.25f + u * 1.8f) * Mathf.Pow(Mathf.Max(0f, 1f - u), 0.45f) * 0.95f;

        void Line(Vector2 a, Vector2 b, float width, Vector3 c, float alphaMul = 1f)
        {
            float len = a.DistanceTo(b);
            int steps = Mathf.Max(1, (int)(len * 1.5f));
            for (int i = 0; i <= steps; i++)
            {
                var p = a.Lerp(b, (float)i / steps);
                int r = Mathf.CeilToInt(width);
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = (int)p.X + dx, y = (int)p.Y + dy;
                    if (x < 0 || y < 0 || x >= W || y >= Hh) continue;
                    float d = new Vector2(x + 0.5f - p.X, y + 0.5f - p.Y).Length();
                    float cov = Mathf.Clamp(width - d + 0.5f, 0f, 1f) * alphaMul;
                    if (cov <= 0f) continue;
                    int k = y * W + x;
                    rgb[k] = rgb[k].Lerp(c, cov);
                    alpha[k] = Mathf.Max(alpha[k], cov);
                }
            }
        }

        Vector3 Needle()
        {
            // Dark, blue-green spruce needles with variation (light, fresh shoot tips here and there)
            float l = 0.65f + R() * 0.5f;
            var c = new Vector3(0.07f, 0.16f, 0.1f) * l;
            if (R() < 0.08f) c = new Vector3(0.2f, 0.3f, 0.12f);
            return c;
        }

        void NeedledTwig(Vector2 a, Vector2 b, float twigW, float needleLen)
        {
            var dir = (b - a).Normalized();
            var perp = new Vector2(-dir.Y, dir.X);
            float len = a.DistanceTo(b);
            Line(a, b, twigW, new Vector3(0.22f, 0.15f, 0.1f));
            for (float s = 0; s < len; s += 1.6f)
            {
                var p = a + dir * s;
                float taper = 1f - 0.5f * (s / len);
                for (int side = -1; side <= 1; side += 2)
                {
                    float ang = (0.6f + R() * 0.5f);
                    var nd = (dir * Mathf.Cos(ang) + perp * side * Mathf.Sin(ang)).Normalized();
                    float nl = needleLen * taper * (0.7f + R() * 0.5f);
                    Line(p, p + nd * nl, 0.6f, Needle());
                }
            }
        }

        // Main axis
        var a0 = new Vector2(4, Hh * 0.5f);
        var a1 = new Vector2(W - 6, Hh * 0.5f);
        // Side shoots (both sides), second-order shoots on the longest ones
        for (float u = 0.04f; u < 0.94f; u += 0.035f + R() * 0.02f)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                float hw = HalfWidth(u) * Hh * (0.75f + R() * 0.3f);
                var p0 = new Vector2(u * W, Hh * 0.5f);
                float ang = 0.75f + R() * 0.35f;
                var d = new Vector2(Mathf.Cos(ang), side * Mathf.Sin(ang));
                float L = hw / Mathf.Sin(ang);
                var p1 = p0 + d * L;
                p1.Y = Mathf.Clamp(p1.Y, 3, Hh - 4);
                p1.X = Mathf.Min(p1.X, W - 4);
                NeedledTwig(p0, p1, 0.9f, 9f);
                if (L > 40f)
                {
                    var m = p0.Lerp(p1, 0.45f);
                    var d2 = (d + new Vector2(0.6f, 0)).Normalized();
                    NeedledTwig(m, m + d2 * L * 0.35f, 0.6f, 7f);
                }
            }
        }
        NeedledTwig(a0, a1, 1.6f, 11f);

        // Fill the color out into the transparent pixels (average color), so mipmaps do not get dark edges
        var mean = Vector3.Zero; float wsum = 0f;
        for (int k = 0; k < rgb.Length; k++) { mean += rgb[k] * alpha[k]; wsum += alpha[k]; }
        mean /= Mathf.Max(wsum, 1f);
        var data = new byte[W * Hh * 4];
        for (int k = 0; k < rgb.Length; k++)
        {
            var c = alpha[k] > 0.01f ? rgb[k] : mean;
            data[k * 4] = (byte)(Mathf.Clamp(Mathf.Pow(c.X, 1f / 2.2f), 0f, 1f) * 255f);
            data[k * 4 + 1] = (byte)(Mathf.Clamp(Mathf.Pow(c.Y, 1f / 2.2f), 0f, 1f) * 255f);
            data[k * 4 + 2] = (byte)(Mathf.Clamp(Mathf.Pow(c.Z, 1f / 2.2f), 0f, 1f) * 255f);
            data[k * 4 + 3] = (byte)(Mathf.Clamp(alpha[k], 0f, 1f) * 255f);
        }
        var img = Image.CreateFromData(W, Hh, false, Image.Format.Rgba8, data);
        img.GenerateMipmaps();
        PreserveCoverage(img, 0.5f);
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Scales alpha in each mipmap so the same fraction is above the threshold as in level 0.</summary>
    internal static void PreserveCoverage(Image img, float cut)
    {
        var d = img.GetData();
        int levels = img.GetMipmapCount() + 1;
        float Coverage(int off, int count, float scale)
        {
            int n = 0;
            for (int i = 0; i < count; i++) if (d[off + i * 4 + 3] / 255f * scale > cut) n++;
            return (float)n / count;
        }
        int w = img.GetWidth(), h = img.GetHeight();
        float target = Coverage(0, w * h, 1f);
        for (int l = 1; l < levels; l++)
        {
            int off = (int)img.GetMipmapOffset(l);
            int lw = Math.Max(1, w >> l), lh = Math.Max(1, h >> l);
            int count = lw * lh;
            float lo = 1f, hi = 8f;
            for (int it = 0; it < 12; it++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Coverage(off, count, mid) < target) lo = mid; else hi = mid;
            }
            for (int i = 0; i < count; i++)
            {
                int k = off + i * 4 + 3;
                d[k] = (byte)Math.Min(255f, d[k] * hi);
            }
        }
        img.SetData(w, h, true, Image.Format.Rgba8, d);
    }
}
