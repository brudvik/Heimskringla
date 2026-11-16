using System.Collections.Generic;
using Godot;
using Heimskringla.Core;
using Heimskringla.World;

namespace Heimskringla.Environment;

/// <summary>
/// Tegner stryk og fossefall (WorldGenerator.Cascades) som skummende vannbånd, gruppert i
/// 512 m-fliser så de kan kuttes bort, og legger vannsprut/dis nederst i de store fallene.
/// </summary>
public partial class Waterfalls : Node3D
{
    private const float Tile = 512f;

    public override void _Ready()
    {
        var w = Game.World;
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/waterfall.gdshader") };
        var tiles = new Dictionary<(int, int), SurfaceTool>();
        var origins = new Dictionary<(int, int), Vector3>();
        int mists = 0;
        foreach (var c in w.Cascades)
        {
            var key = ((int)Mathf.Floor(c.Top.X / Tile), (int)Mathf.Floor(c.Top.Z / Tile));
            if (!tiles.TryGetValue(key, out var st))
            {
                st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                tiles[key] = st;
                origins[key] = new Vector3(key.Item1 * Tile, 0, key.Item2 * Tile);
            }
            AddRibbon(st, c, origins[key], w);

            float drop = c.Drop;
            float width = c.Widths[^1];
            if (drop > 5f && width > 4f && mists < 400)
            {
                AddChild(MakeMist(c.Bottom, width, drop));
                mists++;
            }
        }
        foreach (var (key, st) in tiles)
        {
            st.GenerateNormals();
            var mesh = st.Commit();
            mesh.SurfaceSetMaterial(0, mat);
            AddChild(new MeshInstance3D
            {
                Mesh = mesh,
                Position = origins[key],
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEnd = 1600f,
            });
        }
        GD.Print($"Fossefall: {w.Cascades.Count} kjeder i {tiles.Count} fliser, {mists} med dis");
    }

    /// <summary>
    /// Ett sammenhengende bånd langs hele kjeden: Catmull-Rom-glattet sti, høyden følger terrenget
    /// (aldri under det), bredden glir mellom punktene. UV.y = meter langs elva, COLOR.r = bratthet,
    /// COLOR.g = inntoning i endene.
    /// </summary>
    private static void AddRibbon(SurfaceTool st, Cascade c, Vector3 origin, WorldGenerator w)
    {
        var pts = c.Points;
        int n = pts.Count;
        // Forleng litt opp i elva over og ut i kulpen under, så overgangene skjules
        var first = pts[0] + (pts[0] - pts[1]) with { Y = 0 } * 0.6f;
        var last = pts[n - 1] + (pts[n - 1] - pts[n - 2]) with { Y = 0 } * 0.8f;
        last.Y = pts[n - 1].Y;
        var P = new List<Vector3> { first };
        P.AddRange(pts);
        P.Add(last);
        var W = new List<float> { c.Widths[0] };
        W.AddRange(c.Widths);
        W.Add(c.Widths[n - 1] * 1.2f);

        const int Sub = 5;
        var path = new List<Vector3>();
        var widths = new List<float>();
        for (int k = 0; k < P.Count - 1; k++)
        {
            var p0 = P[Mathf.Max(k - 1, 0)]; var p1 = P[k]; var p2 = P[k + 1]; var p3 = P[Mathf.Min(k + 2, P.Count - 1)];
            for (int s = 0; s < Sub; s++)
            {
                float t = (float)s / Sub;
                var q = CatmullRom(p0, p1, p2, p3, t);
                // Høyden: rett mellom punktene, men aldri under terrenget (over hele bredden)
                q.Y = Mathf.Lerp(p1.Y, p2.Y, t);
                path.Add(q);
                widths.Add(Mathf.Lerp(W[k], W[k + 1], t));
            }
        }
        path.Add(P[^1]);
        widths.Add(W[^1]);

        float total = 0f;
        var dist = new float[path.Count];
        for (int k = 1; k < path.Count; k++) { total += path[k].DistanceTo(path[k - 1]); dist[k] = total; }

        Vector3 prevL = default, prevR = default; float prevV = 0; Color prevC = default;
        for (int k = 0; k < path.Count; k++)
        {
            var dir = (path[Mathf.Min(k + 1, path.Count - 1)] - path[Mathf.Max(k - 1, 0)]) with { Y = 0 };
            if (dir.LengthSquared() < 1e-4f) dir = Vector3.Forward;
            var side = new Vector3(-dir.Z, 0, dir.X).Normalized() * widths[k] * 0.5f;
            var p = path[k];
            float ground = Mathf.Max(w.GetHeight(p.X, p.Z), Mathf.Max(w.GetHeight(p.X + side.X * 0.7f, p.Z + side.Z * 0.7f), w.GetHeight(p.X - side.X * 0.7f, p.Z - side.Z * 0.7f)));
            p.Y = Mathf.Max(p.Y, ground + 0.3f);
            // Bratthet lokalt (fall per meter) til shaderen
            var a = path[Mathf.Max(k - 1, 0)]; var b = path[Mathf.Min(k + 1, path.Count - 1)];
            float run = new Vector2(b.X - a.X, b.Z - a.Z).Length();
            float steep = Mathf.Clamp((a.Y - b.Y) / Mathf.Max(run, 0.5f), 0f, 1f);
            float endFade = Mathf.Min(Biomes.SmoothStep(0f, 6f, dist[k]), Biomes.SmoothStep(total, total - 8f, dist[k]));
            var col = new Color(steep, endFade, 0);
            var L = p - side - origin; var R = p + side - origin;
            float v = dist[k];
            if (k > 0)
            {
                st.SetColor(prevC); st.SetUV(new Vector2(0, prevV)); st.AddVertex(prevL);
                st.SetColor(prevC); st.SetUV(new Vector2(1, prevV)); st.AddVertex(prevR);
                st.SetColor(col); st.SetUV(new Vector2(0, v)); st.AddVertex(L);
                st.SetColor(prevC); st.SetUV(new Vector2(1, prevV)); st.AddVertex(prevR);
                st.SetColor(col); st.SetUV(new Vector2(1, v)); st.AddVertex(R);
                st.SetColor(col); st.SetUV(new Vector2(0, v)); st.AddVertex(L);
            }
            prevL = L; prevR = R; prevV = v; prevC = col;
        }
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private static GpuParticles3D MakeMist(Vector3 at, float width, float drop)
    {
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(width * 0.4f, 0.3f, width * 0.4f),
            Direction = Vector3.Up,
            Spread = 60f,
            InitialVelocityMin = 0.5f,
            InitialVelocityMax = 1.5f + drop * 0.08f,
            Gravity = new Vector3(0, 0.15f, 0),
            DampingMin = 0.5f, DampingMax = 1f,
            ScaleMin = 1f, ScaleMax = 2.5f,
            ScaleCurve = new CurveTexture { Curve = Ramp(0.4f, 1f) },
            Color = new Color(1, 1, 1, 0.35f),
            AlphaCurve = new CurveTexture { Curve = Fade() },
        };
        var mat = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true,
            AlbedoTexture = Weather.SoftDot(32),
            ProximityFadeEnabled = true,
            ProximityFadeDistance = 1f,
        };
        return new GpuParticles3D
        {
            Amount = 40,
            Lifetime = 3f,
            Preprocess = 3f,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = new Vector2(1.6f, 1.6f), Material = mat },
            Position = at,
            VisibilityRangeEnd = 350f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-width, -2, -width), new Vector3(width * 2, drop + 8, width * 2)),
        };
    }

    public static Curve Ramp(float a, float b)
    {
        var c = new Curve();
        c.AddPoint(new Vector2(0, a));
        c.AddPoint(new Vector2(1, b));
        return c;
    }

    public static Curve Fade()
    {
        var c = new Curve();
        c.AddPoint(new Vector2(0, 0));
        c.AddPoint(new Vector2(0.2f, 1));
        c.AddPoint(new Vector2(1, 0));
        return c;
    }
}
