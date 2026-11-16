using Godot;
using Heimskringla.World;

namespace Heimskringla.Environment;

/// <summary>
/// Sol, måne, himmel (egen shader med skyer), tåke og etterbehandling, med dag/natt-syklus.
/// Hold T for å spole tiden.
/// </summary>
public partial class DayNight : Node3D
{
    public float TimeOfDay = 0.32f;            // 0 = midnatt, 0.5 = middag
    public int Day = 4;                         // dager siden vårjevndøgn (4 = sein vår)
    public float DayLengthSeconds = 24f * 60f;
    public const float Latitude = 62f;          // grader nord (Vestlandet/Trøndelag)
    public Weather Weather;
    public float Aurora;                        // nordlysstyrke nå (0..1)
    private float _auroraNight = -1f;           // styrken i natt (trekkes én gang per natt)

    private DirectionalLight3D _sun, _moon;
    private Godot.Environment _env;
    private ShaderMaterial _sky;

    public override void _Ready()
    {
        // Global verdi som vannet bruker til refleksjon av horisonten
        // sky_horizon er deklarert under [shader_globals] i project.godot

        _sky = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sky.gdshader") };
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _sky, ProcessMode = Sky.ProcessModeEnum.Incremental, RadianceSize = Sky.RadianceSizeEnum.Size128 },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            TonemapExposure = 1.05f,

            SsaoEnabled = true,
            SsaoRadius = 1.6f,
            SsaoIntensity = 2.2f,
            SsaoPower = 1.6f,
            SsaoDetail = 0.6f,

            GlowEnabled = true,
            GlowIntensity = 0.35f,
            GlowBloom = 0.04f,
            GlowHdrThreshold = 1.2f,

            FogEnabled = true,
            FogDensity = 0.0004f,
            FogSkyAffect = 0.45f,
            FogAerialPerspective = 0.85f,
            FogSunScatter = 0.35f,
            FogHeight = 25f,
            FogHeightDensity = 0.0f,

            AdjustmentEnabled = true,
            AdjustmentSaturation = 1.08f,
            AdjustmentContrast = 1.06f,
        };
        if (OS.GetEnvironment("HK_NOSSAO") == "1") _env.SsaoEnabled = false;   // testbryter
        AddChild(new WorldEnvironment { Environment = _env });

        _sun = new DirectionalLight3D
        {
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowMaxDistance = 320f,
            DirectionalShadowSplit1 = 0.06f,
            DirectionalShadowSplit2 = 0.18f,
            DirectionalShadowSplit3 = 0.45f,
            DirectionalShadowBlendSplits = true,
            ShadowBlur = 1.0f,
            ShadowNormalBias = 1.2f,
            LightAngularDistance = 0.4f,
        };
        AddChild(_sun);
        _moon = new DirectionalLight3D { ShadowEnabled = true, LightColor = new Color(0.62f, 0.72f, 1f), DirectionalShadowMaxDistance = 120f };
        AddChild(_moon);
        Apply();
    }

    public override void _Process(double delta)
    {
        float speed = Input.IsPhysicalKeyPressed(Key.T) && Input.MouseMode == Input.MouseModeEnum.Captured ? 90f : 1f;
        TimeOfDay += (float)delta / DayLengthSeconds * speed;
        if (TimeOfDay >= 1f) { TimeOfDay -= 1f; Day++; }
        Apply();
    }

    /// <summary>Retning mot sola for gitt dag/tid (x = øst, y = opp, z = sør).</summary>
    public Vector3 SunDirection(float yearT, float timeOfDay)
    {
        float decl = Mathf.DegToRad(23.4f) * Mathf.Sin(Mathf.Tau * yearT);   // sommersolverv ved yearT = 0.25
        float lat = Mathf.DegToRad(Latitude);
        float hAng = (timeOfDay - 0.5f) * Mathf.Tau;                           // timevinkel, 0 ved middag
        float up = Mathf.Sin(lat) * Mathf.Sin(decl) + Mathf.Cos(lat) * Mathf.Cos(decl) * Mathf.Cos(hAng);
        float east = -Mathf.Cos(decl) * Mathf.Sin(hAng);
        float north = Mathf.Cos(lat) * Mathf.Sin(decl) - Mathf.Sin(lat) * Mathf.Cos(decl) * Mathf.Cos(hAng);
        return new Vector3(east, up, -north).Normalized();
    }

    private void Apply()
    {
        var wx = Weather;
        float yearT = wx != null ? wx.YearT : 0.15f;
        var toSun = SunDirection(yearT, TimeOfDay);
        _sun.Basis = Basis.LookingAt(-toSun, Vector3.Up);       // lyset går langs -Z
        // Månen: grovt motsatt sola, men alltid litt over horisonten om natta
        var toMoon = new Vector3(-toSun.X, Mathf.Abs(toSun.Y) * 0.8f + 0.18f, -toSun.Z).Normalized();
        _moon.Basis = Basis.LookingAt(-toMoon, Vector3.Up);
        float e = toSun.Y;

        float clouds = wx?.Clouds ?? 0.45f, dark = wx?.Dark ?? 0f, fog = wx?.Fog ?? 0f, flash = wx?.Flash ?? 0f;
        float precip = wx?.Precip ?? 0f;

        float day = Biomes.SmoothStep(-0.08f, 0.2f, e);
        float golden = 1f - Biomes.SmoothStep(0.0f, 0.35f, Mathf.Abs(e));
        float sunBlock = 1f - dark * 0.85f;                                     // skyer demper direkte sollys
        _sun.LightEnergy = Biomes.SmoothStep(-0.03f, 0.12f, e) * 1.9f * sunBlock;
        _sun.LightColor = new Color(1f, 0.58f, 0.32f).Lerp(new Color(1f, 0.96f, 0.88f), Biomes.SmoothStep(0.03f, 0.45f, e));
        _sun.Visible = e > -0.05f && sunBlock > 0.05f;
        _sun.ShadowOpacity = Mathf.Lerp(1f, 0.35f, dark);
        _moon.LightEnergy = (1f - day) * 0.16f * (1f - dark * 0.8f);
        _moon.Visible = day < 0.9f;

        // Nordlys: noen netter, mest på klare netter (trekkes ved skumring)
        float nightAmt = 1f - day;
        if (nightAmt > 0.5f && _auroraNight < 0f)
        {
            var rng = new RandomNumberGenerator { Seed = (ulong)(Day * 7907 + Main.Seed) };
            float r = rng.Randf();
            _auroraNight = r < 0.45f ? 0f : Mathf.Lerp(0.35f, 1f, rng.Randf());
            if (ForceAurora) _auroraNight = 1f;
        }
        else if (nightAmt < 0.2f) _auroraNight = -1f;
        float auroraTarget = Mathf.Max(_auroraNight, 0f) * Biomes.SmoothStep(0.6f, 0.95f, nightAmt) * (1f - Biomes.SmoothStep(0.45f, 0.8f, clouds));
        Aurora = Mathf.Lerp(Aurora, auroraTarget, 0.02f);

        var dayTop = new Color(0.22f, 0.45f, 0.82f);
        var dayHorizon = new Color(0.66f, 0.77f, 0.88f);
        var nightTop = new Color(0.008f, 0.012f, 0.035f);
        var nightHorizon = new Color(0.035f, 0.05f, 0.09f);
        var sunset = new Color(0.98f, 0.52f, 0.28f);
        // Overskyet: grå himmel
        var greyTop = new Color(0.42f, 0.45f, 0.5f);
        var greyHorizon = new Color(0.55f, 0.58f, 0.62f);
        dayTop = dayTop.Lerp(greyTop, dark).Darkened(dark * 0.35f);
        dayHorizon = dayHorizon.Lerp(greyHorizon, dark).Darkened(dark * 0.3f);

        var top = nightTop.Lerp(dayTop, day);
        var horizon = nightHorizon.Lerp(dayHorizon, day).Lerp(sunset, golden * 0.65f * (1f - dark * 0.7f) * Biomes.SmoothStep(-0.15f, 0.05f, e));
        // Nordlyset farger natthimmelen svakt grønn
        horizon = horizon.Lerp(new Color(0.05f, 0.14f, 0.1f), Aurora * 0.35f * nightAmt);
        var flashCol = new Color(0.75f, 0.8f, 1f);
        top = top.Lerp(flashCol, flash * 0.6f);
        horizon = horizon.Lerp(flashCol, flash * 0.6f);

        _sky.SetShaderParameter("top_color", top);
        _sky.SetShaderParameter("horizon_color", horizon);
        _sky.SetShaderParameter("ground_color", horizon.Darkened(0.45f));
        _sky.SetShaderParameter("sun_dir", toSun);
        _sky.SetShaderParameter("moon_dir", toMoon);
        _sky.SetShaderParameter("sun_color", _sun.LightColor);
        _sky.SetShaderParameter("night", nightAmt);
        _sky.SetShaderParameter("aurora", Aurora);
        _sky.SetShaderParameter("cloud_coverage", clouds);
        _sky.SetShaderParameter("cloud_dark", dark);
        _sky.SetShaderParameter("wind_offset", _cloudOffset);
        _sky.SetShaderParameter("cloud_light", new Color(1f, 0.97f, 0.94f).Lerp(sunset, golden * 0.5f).Lerp(flashCol, flash));
        _sky.SetShaderParameter("cloud_shadow", top.Lerp(horizon, 0.5f).Darkened(0.15f + dark * 0.2f));

        _env.AmbientLightEnergy = Mathf.Lerp(0.12f, 0.55f, day) * (1f - dark * 0.25f) + flash * 1.5f + Aurora * nightAmt * 0.06f;
        _env.FogLightColor = horizon;
        _env.FogLightEnergy = Mathf.Lerp(0.25f, 1f, day);
        _env.FogDensity = 0.0004f + fog * 0.0035f + precip * 0.0012f;
        _env.FogSunScatter = 0.35f * (1f - dark);
        // Morgendis i dalene rundt soloppgang
        float dawn = Biomes.SmoothStep(0.18f, 0.27f, TimeOfDay) * (1f - Biomes.SmoothStep(0.30f, 0.38f, TimeOfDay));
        _env.FogHeightDensity = 0.0006f + dawn * 0.006f + fog * 0.01f;
        RenderingServer.GlobalShaderParameterSet("sky_horizon", new Vector3(horizon.R, horizon.G, horizon.B));
        RenderingServer.GlobalShaderParameterSet("sky_zenith", new Vector3(top.R, top.G, top.B));
    }

    public bool ForceAurora;
    private Vector2 _cloudOffset;

    public override void _PhysicsProcess(double delta)
    {
        // Skyene driver med vinden
        var wx = Weather;
        if (wx != null) _cloudOffset += wx.WindDir * (0.002f + wx.Wind * 0.012f) * (float)delta;
    }
}
