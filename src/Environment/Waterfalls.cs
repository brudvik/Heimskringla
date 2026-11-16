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

            float drop = c.Top.Y - c.Bottom.Y;
            if (drop > 5f && c.Width > 4f && mists < 400)
            {
                AddChild(MakeMist(c.Bottom, c.Width, drop));
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
        GD.Print($"Fossefall: {w.Cascades.Count} bratte elvesteg i {tiles.Count} fliser, {mists} med dis");
    }

    /// <summary>Et bånd fra toppen til bunnen, litt over terrenget, med en buet profil på bratte fall.</summary>
    private static void AddRibbon(SurfaceTool st, Cascade c, Vector3 origin, WorldGenerator w)
    {
        var dir = c.Bottom - c.Top;
        var flat = new Vector3(dir.X, 0, dir.Z);
        if (flat.LengthSquared() < 0.01f) return;
        var side = new Vector3(-flat.Z, 0, flat.X).Normalized() * c.Width * 0.5f;
        // Strekk båndet litt forbi endepunktene så nabobånd overlapper (ingen glipper)
        var ext = flat.Normalized() * c.Width * 0.25f;
        var top = c.Top - ext - origin;
        var bot = c.Bottom + ext - origin;
        const int Seg = 10;
        float steep = Mathf.Clamp(c.Steep, 0f, 1f);
        float len = dir.Length();
        var col = new Color(steep, 0, 0);
        // Følg terrenget (detaljstøyen gjør fjellsiden ujevn), men aldri under den rette linja
        Vector3 P(float t)
        {
            float x = Mathf.Lerp(top.X, bot.X, t), z = Mathf.Lerp(top.Z, bot.Z, t);
            float gx = x + origin.X, gz = z + origin.Z;
            float ground = Mathf.Max(Mathf.Max(w.GetHeight(gx + side.X * 0.8f, gz + side.Z * 0.8f), w.GetHeight(gx - side.X * 0.8f, gz - side.Z * 0.8f)), w.GetHeight(gx, gz));
            return new Vector3(x, Mathf.Max(Mathf.Lerp(top.Y, bot.Y, t), ground + 0.35f), z);
        }
        for (int s = 0; s < Seg; s++)
        {
            float t0 = (float)s / Seg, t1 = (float)(s + 1) / Seg;
            var a = P(t0); var b = P(t1);
            // V følger høyden (nedover = større), så stripene fortsetter mellom nabobånd
            float v0 = -a.Y - origin.Y + t0 * len * 0.3f, v1 = -b.Y - origin.Y + t1 * len * 0.3f;
            st.SetColor(col); st.SetUV(new Vector2(0, v0)); st.AddVertex(a - side);
            st.SetColor(col); st.SetUV(new Vector2(1, v0)); st.AddVertex(a + side);
            st.SetColor(col); st.SetUV(new Vector2(0, v1)); st.AddVertex(b - side);
            st.SetColor(col); st.SetUV(new Vector2(1, v0)); st.AddVertex(a + side);
            st.SetColor(col); st.SetUV(new Vector2(1, v1)); st.AddVertex(b + side);
            st.SetColor(col); st.SetUV(new Vector2(0, v1)); st.AddVertex(b - side);
        }
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
