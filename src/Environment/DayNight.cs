using Godot;
using Heimskringla.Core;
using Heimskringla.World;

namespace Heimskringla.Environment;

/// <summary>
/// Sun, moon, sky (custom shader with clouds), fog and post-processing, with a day/night cycle.
/// Hold T to fast-forward time.
///
/// The lighting is built as "light direction" over the course of the day:
///  - The sun is strong and direct, the sky light weaker, so shapes get a clear lit and shadow side.
///  - Volumetric fog gives light shafts through the forest, haze in the morning sun and a glow around the campfire.
///  - Auto exposure: the eye adapts when going from open terrain into dense forest.
///  - Color grading (curves per channel): cool shadows and warm light, strongest at golden hour,
///    blue and muted at night, neutral and flatter in grey weather.
/// </summary>
public partial class DayNight : Node3D
{
    public float TimeOfDay = 0.32f;            // 0 = midnight, 0.5 = noon
    public int Day = 4;                         // days since the spring equinox (4 = late spring)
    public float DayLengthSeconds = 24f * 60f;
    public const float Latitude = 62f;          // degrees north (Western Norway/Trøndelag)
    public Weather Weather;
    public float Aurora;                        // current aurora strength (0..1)
    private float _auroraNight = -1f;           // tonight's strength (rolled once per night)

    private DirectionalLight3D _sun, _moon;
    private Godot.Environment _env;
    private ShaderMaterial _sky;
    private CameraAttributesPractical _cam;
    private FogVolume _mist;
    private ShaderMaterial _mistMat;
    private readonly GradientTexture1D _grade = new() { Width = 128 };
    private float _gradeTimer;

    public override void _Ready()
    {
        // Global value the water uses for reflecting the horizon
        // sky_horizon is declared under [shader_globals] in project.godot

        _sky = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sky.gdshader") };
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _sky, ProcessMode = Sky.ProcessModeEnum.Incremental, RadianceSize = Sky.RadianceSizeEnum.Size128 },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            TonemapExposure = 1.0f,
            TonemapWhite = 6f,

            SsaoEnabled = true,
            SsaoRadius = 1.6f,
            SsaoIntensity = 2.2f,
            SsaoPower = 1.6f,
            SsaoDetail = 0.6f,

            GlowEnabled = true,
            GlowIntensity = 0.45f,
            GlowBloom = 0.05f,
            GlowHdrThreshold = 1.05f,

            FogEnabled = true,
            FogDensity = 0.0004f,
            FogSkyAffect = 0.45f,
            FogAerialPerspective = 0.85f,
            FogSunScatter = 0.35f,
            FogHeight = 25f,
            FogHeightDensity = 0.0f,

            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.002f,
            VolumetricFogAlbedo = new Color(0.92f, 0.94f, 0.97f),
            VolumetricFogAnisotropy = 0.62f,     // strongly forward-scattering: the light glows toward the sun
            VolumetricFogLength = 140f,
            VolumetricFogDetailSpread = 2f,
            VolumetricFogAmbientInject = 0.7f,
            VolumetricFogSkyAffect = 0f,
            VolumetricFogTemporalReprojectionEnabled = true,

            AdjustmentEnabled = true,
            AdjustmentSaturation = 1.0f,
            AdjustmentContrast = 1.0f,
            AdjustmentColorCorrection = _grade,
        };
        _grade.Gradient = new Gradient();
        UpdateGrade(0f, 0f, 0f, 0f);
        if (OS.GetEnvironment("HK_NOSSAO") == "1") _env.SsaoEnabled = false;   // test switch
        if (OS.GetEnvironment("HK_NOVOLFOG") == "1") _env.VolumetricFogEnabled = false;   // test switch
        // Auto exposure (like the eye): brighter inside dense forest, darker in snow and sunshine.
        // Limited range, so night stays dark and the midday sun stays bright.
        _cam = new CameraAttributesPractical
        {
            AutoExposureEnabled = OS.GetEnvironment("HK_NOAUTOEXP") != "1",
            AutoExposureScale = 0.38f,
            AutoExposureSpeed = 0.6f,
            AutoExposureMinSensitivity = 55f,
            AutoExposureMaxSensitivity = 240f,
        };
        AddChild(new WorldEnvironment { Environment = _env, CameraAttributes = _cam });
        Settings.Changed += ApplyQuality;

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
            LightVolumetricFogEnergy = 1.4f,
        };
        AddChild(_sun);
        // Low mist in the valleys and over the fjord (follows the camera, see shaders/mist.gdshader)
        _mistMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/mist.gdshader") };
        _mist = new FogVolume { Size = new Vector3(300f, 420f, 300f), Shape = RenderingServer.FogVolumeShape.Box, Material = _mistMat };
        AddChild(_mist);
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

    /// <summary>Direction toward the sun for a given day/time (x = east, y = up, z = south).</summary>
    public Vector3 SunDirection(float yearT, float timeOfDay)
    {
        float decl = Mathf.DegToRad(23.4f) * Mathf.Sin(Mathf.Tau * yearT);   // summer solstice at yearT = 0.25
        float lat = Mathf.DegToRad(Latitude);
        float hAng = (timeOfDay - 0.5f) * Mathf.Tau;                           // hour angle, 0 at noon
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
        _sun.Basis = Basis.LookingAt(-toSun, Vector3.Up);       // the light travels along -Z
        // Moon: roughly opposite the sun, but always a bit above the horizon at night
        var toMoon = new Vector3(-toSun.X, Mathf.Abs(toSun.Y) * 0.8f + 0.18f, -toSun.Z).Normalized();
        _moon.Basis = Basis.LookingAt(-toMoon, Vector3.Up);
        float e = toSun.Y;

        float clouds = wx?.Clouds ?? 0.45f, dark = wx?.Dark ?? 0f, fog = wx?.Fog ?? 0f, flash = wx?.Flash ?? 0f;
        float precip = wx?.Precip ?? 0f;

        float day = Biomes.SmoothStep(-0.08f, 0.2f, e);
        float golden = 1f - Biomes.SmoothStep(0.0f, 0.35f, Mathf.Abs(e));
        float sunBlock = 1f - dark * 0.85f;                                     // clouds dim direct sunlight
        // Low sun is weaker (longer path through the air) but warm; high sun is strong and slightly warm-white
        _sun.LightEnergy = Biomes.SmoothStep(-0.03f, 0.12f, e) * Mathf.Lerp(1.7f, 2.5f, Biomes.SmoothStep(0.1f, 0.6f, e)) * sunBlock;
        _sun.LightColor = new Color(1f, 0.52f, 0.26f).Lerp(new Color(1f, 0.8f, 0.58f), Biomes.SmoothStep(0.0f, 0.18f, e))
                                                    .Lerp(new Color(1f, 0.95f, 0.86f), Biomes.SmoothStep(0.15f, 0.5f, e));
        _sun.Visible = e > -0.05f && sunBlock > 0.05f;
        _sun.ShadowOpacity = Mathf.Lerp(1f, 0.35f, dark);
        _moon.LightEnergy = (1f - day) * 0.16f * (1f - dark * 0.8f);
        _moon.Visible = day < 0.9f;

        // Aurora: some nights, mostly on clear nights (rolled at dusk)
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
        // Overcast: grey sky
        var greyTop = new Color(0.42f, 0.45f, 0.5f);
        var greyHorizon = new Color(0.55f, 0.58f, 0.62f);
        dayTop = dayTop.Lerp(greyTop, dark).Darkened(dark * 0.35f);
        dayHorizon = dayHorizon.Lerp(greyHorizon, dark).Darkened(dark * 0.3f);

        var top = nightTop.Lerp(dayTop, day);
        var horizon = nightHorizon.Lerp(dayHorizon, day).Lerp(sunset, golden * 0.65f * (1f - dark * 0.7f) * Biomes.SmoothStep(-0.15f, 0.05f, e));
        // The aurora tints the night sky faintly green
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

        // Sky light: lower than before during the day (more contrast against the sun), but raised in grey weather when the sun
        // is gone, so overcast becomes soft and even instead of just dark
        _env.AmbientLightEnergy = Mathf.Lerp(0.14f, 0.42f, day) * (1f + dark * 0.35f) + flash * 1.5f + Aurora * nightAmt * 0.08f;
        _env.FogLightColor = horizon;
        _env.FogLightEnergy = Mathf.Lerp(0.25f, 1f, day);
        _env.FogDensity = 0.0004f + fog * 0.0035f + precip * 0.0012f;
        _env.FogSunScatter = 0.35f * (1f - dark);
        // Morning haze in the valleys around sunrise
        float dawn = Biomes.SmoothStep(0.18f, 0.27f, TimeOfDay) * (1f - Biomes.SmoothStep(0.30f, 0.38f, TimeOfDay));
        _env.FogHeightDensity = 0.0006f + dawn * 0.006f + fog * 0.01f;

        // Volumetric fog: light haze during the day (light shafts in the forest), dense in the morning sun and at
        // golden hour, thick in fog and rain. A little at night so moonlight and firelight get a glow.
        float lowSun = golden * day;
        _env.VolumetricFogDensity = 0.0014f + dawn * 0.0045f + lowSun * 0.0035f + fog * 0.018f + precip * 0.004f + nightAmt * 0.0015f;
        _env.VolumetricFogAlbedo = new Color(0.92f, 0.94f, 0.97f).Lerp(new Color(0.8f, 0.82f, 0.86f), dark);

        // Fog banks: thickest in the morning sun and in foggy weather, some in the evening and after rain,
        // nearly gone in the middle of a clear day (the sun "burns off" the haze)
        float dusk = Biomes.SmoothStep(0.74f, 0.8f, TimeOfDay) * (1f - Biomes.SmoothStep(0.86f, 0.95f, TimeOfDay));
        float mist = 0.004f + dawn * 0.05f + dusk * 0.02f + fog * 0.06f + precip * 0.02f + nightAmt * 0.012f;
        _mistMat.SetShaderParameter("amount", mist);
        _mistMat.SetShaderParameter("thickness", 7f + fog * 14f + dawn * 4f);
        var cam = GetViewport()?.GetCamera3D();
        if (cam != null)
        {
            var cp = cam.GlobalPosition;
            _mist.GlobalPosition = new Vector3(Mathf.Round(cp.X / 8f) * 8f, cp.Y - 120f, Mathf.Round(cp.Z / 8f) * 8f);
        }

        // Color grading (infrequent; the texture is rebuilt)
        _gradeTimer -= (float)GetProcessDeltaTime();
        if (_gradeTimer <= 0f)
        {
            _gradeTimer = 0.25f;
            float warm = (0.35f + lowSun * 0.9f) * (1f - dark * 0.8f) * day;
            UpdateGrade(warm, nightAmt, dark, fog);
        }
        RenderingServer.GlobalShaderParameterSet("sky_horizon", new Vector3(horizon.R, horizon.G, horizon.B));
        RenderingServer.GlobalShaderParameterSet("sky_zenith", new Vector3(top.R, top.G, top.B));
        // The clouds again for water reflections (same values the sky shader gets)
        var cl = new Color(1f, 0.97f, 0.94f).Lerp(sunset, golden * 0.5f).Lerp(flashCol, flash);
        var cs = top.Lerp(horizon, 0.5f).Darkened(0.15f + dark * 0.2f);
        RenderingServer.GlobalShaderParameterSet("cloud_light", new Vector3(cl.R, cl.G, cl.B));
        RenderingServer.GlobalShaderParameterSet("cloud_shadow", new Vector3(cs.R, cs.G, cs.B));
        RenderingServer.GlobalShaderParameterSet("cloud_params", new Vector4(clouds, dark, nightAmt, 0f));
        RenderingServer.GlobalShaderParameterSet("cloud_offset", _cloudOffset);
        RenderingServer.GlobalShaderParameterSet("sun_direction", toSun);
    }

    /// <summary>
    /// Builds the color curves (one per channel, Godot looks up r, g and b separately):
    ///  - S-curve for contrast (less in grey weather and fog, where the light is flat)
    ///  - cool, bluish shadows and warm highlights ("teal/orange", but subtle)
    ///  - night: blue shift and lifted blacks, so darkness becomes moon-blue instead of black
    /// </summary>
    private void UpdateGrade(float warm, float night, float dark, float fog)
    {
        var g = _grade.Gradient;
        const int n = 9;
        var offs = new float[n];
        var cols = new Color[n];
        float contrast = 0.11f * (1f - dark * 0.6f) * (1f - fog * 0.7f) * (1f - night * 0.4f);
        for (int i = 0; i < n; i++)
        {
            float x = i / (float)(n - 1);
            // S-curve: darker below the midpoint, lighter above (zero at the ends and in the middle)
            float y = x - contrast * Mathf.Sin(x * Mathf.Tau) * 0.5f;
            float shadow = 1f - Biomes.SmoothStep(0f, 0.55f, x);     // weight for dark areas
            float high = Biomes.SmoothStep(0.45f, 1f, x);            // weight for bright areas
            float r = y + high * warm * 0.035f - shadow * (0.012f + night * 0.02f);
            float gg = y + high * warm * 0.012f - shadow * 0.004f;
            float b = y - high * warm * 0.04f + shadow * (0.018f + night * 0.035f);
            // Lift the deepest blacks a bit (air and light in the shadows, especially at night)
            float lift = (0.008f + night * 0.012f) * (1f - x);
            offs[i] = x;
            cols[i] = new Color(Mathf.Clamp(r + lift * 0.6f, 0f, 1f), Mathf.Clamp(gg + lift * 0.8f, 0f, 1f), Mathf.Clamp(b + lift * 1.4f, 0f, 1f));
        }
        g.Offsets = offs;
        g.Colors = cols;
    }

    public override void _ExitTree() => Settings.Changed -= ApplyQuality;

    /// <summary>Graphics quality: low = no volumetric fog or SSAO, shorter shadows.</summary>
    private void ApplyQuality()
    {
        int q = Settings.Quality;
        _env.VolumetricFogEnabled = q >= 1 && OS.GetEnvironment("HK_NOVOLFOG") != "1";
        _env.SsaoEnabled = q >= 1 && OS.GetEnvironment("HK_NOSSAO") != "1";
        _env.SsaoDetail = q >= 2 ? 0.6f : 0.3f;
        if (_sun != null)
        {
            _sun.DirectionalShadowMaxDistance = q == 0 ? 180f : q == 1 ? 260f : 320f;
            _sun.DirectionalShadowMode = q == 0 ? DirectionalLight3D.ShadowMode.Parallel2Splits : DirectionalLight3D.ShadowMode.Parallel4Splits;
        }
    }

    public bool ForceAurora;
    private Vector2 _cloudOffset;

    public override void _PhysicsProcess(double delta)
    {
        // The clouds drift with the wind
        var wx = Weather;
        if (wx != null) _cloudOffset += wx.WindDir * (0.002f + wx.Wind * 0.012f) * (float)delta;
    }
}
