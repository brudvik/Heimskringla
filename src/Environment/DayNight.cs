using Godot;
using Heimskringla.World;

namespace Heimskringla.Environment;

/// <summary>Sol, måne, himmel og tåke med dag/natt-syklus. Hold T for å spole tiden.</summary>
public partial class DayNight : Node3D
{
    public float TimeOfDay = 0.32f;            // 0 = midnatt, 0.5 = middag
    public float DayLengthSeconds = 24f * 60f;

    private DirectionalLight3D _sun, _moon;
    private Godot.Environment _env;
    private ProceduralSkyMaterial _sky;

    public override void _Ready()
    {
        _sky = new ProceduralSkyMaterial { SunAngleMax = 20f, SunCurve = 0.1f };
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _sky },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            TonemapWhite = 6f,
            FogEnabled = true,
            FogDensity = 0.00032f,
            FogSkyAffect = 0.55f,
            FogAerialPerspective = 0.35f,
            FogSunScatter = 0.25f,
        };
        AddChild(new WorldEnvironment { Environment = _env });

        _sun = new DirectionalLight3D
        {
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowMaxDistance = 160f,
            ShadowBlur = 1.2f,
        };
        AddChild(_sun);
        _moon = new DirectionalLight3D { ShadowEnabled = false, LightColor = new Color(0.6f, 0.7f, 1f) };
        AddChild(_moon);
        Apply();
    }

    public override void _Process(double delta)
    {
        float speed = Input.IsPhysicalKeyPressed(Key.T) && Input.MouseMode == Input.MouseModeEnum.Captured ? 90f : 1f;
        TimeOfDay = Mathf.PosMod(TimeOfDay + (float)delta / DayLengthSeconds * speed, 1f);
        Apply();
    }

    private void Apply()
    {
        float angle = (TimeOfDay - 0.25f) * Mathf.Tau;
        float elev = Mathf.Sin(angle);
        _sun.Basis = new Basis(Vector3.Up, 0.6f) * new Basis(Vector3.Right, -angle);
        _moon.Basis = new Basis(Vector3.Up, 0.6f) * new Basis(Vector3.Right, -angle + Mathf.Pi);

        float day = Biomes.SmoothStep(-0.08f, 0.2f, elev);
        float golden = 1f - Biomes.SmoothStep(0.0f, 0.35f, Mathf.Abs(elev));
        _sun.LightEnergy = Biomes.SmoothStep(-0.04f, 0.12f, elev) * 1.25f;
        _sun.LightColor = new Color(1f, 0.62f, 0.38f).Lerp(new Color(1f, 0.97f, 0.9f), Biomes.SmoothStep(0.02f, 0.4f, elev));
        _sun.Visible = elev > -0.1f;
        _moon.LightEnergy = (1f - day) * 0.12f;
        _moon.Visible = day < 0.95f;

        var dayTop = new Color(0.28f, 0.50f, 0.84f);
        var dayHorizon = new Color(0.68f, 0.78f, 0.88f);
        var nightTop = new Color(0.01f, 0.015f, 0.04f);
        var nightHorizon = new Color(0.04f, 0.06f, 0.10f);
        var sunset = new Color(0.95f, 0.55f, 0.32f);

        var top = nightTop.Lerp(dayTop, day);
        var horizon = nightHorizon.Lerp(dayHorizon, day).Lerp(sunset, golden * 0.6f * Biomes.SmoothStep(-0.15f, 0.05f, elev));
        _sky.SkyTopColor = top;
        _sky.SkyHorizonColor = horizon;
        _sky.GroundHorizonColor = horizon;
        _sky.GroundBottomColor = top.Darkened(0.5f);
        _env.AmbientLightEnergy = Mathf.Lerp(0.18f, 1.0f, day);
        _env.FogLightColor = horizon;
        _env.FogLightEnergy = Mathf.Lerp(0.3f, 1f, day);
    }
}
