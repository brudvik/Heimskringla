using System;
using Godot;
using Heimskringla.Core;
using Heimskringla.World;

namespace Heimskringla.Environment;

public enum WeatherKind { Clear, Cloudy, Overcast, Rain, Storm, Fog }

/// <summary>
/// Vær og årstider. Velger nytt vær med jevne mellomrom (påvirket av årstiden), glir mykt mellom
/// tilstandene og setter globale shader-parametre for vind, snø, løvfarge og våthet.
/// Nedbøren (regn/snø) er partikler som følger kameraet. DayNight leser skydekke, mørke og tåke herfra.
/// </summary>
public partial class Weather : Node3D
{
    public const int DaysPerSeason = 6;
    public static readonly string[] SeasonNames = { "Vår", "Sommer", "Høst", "Vinter" };

    public static Weather Current;
    public WeatherKind Kind = WeatherKind.Cloudy;
    public bool Locked;                     // autotest/debug: ikke bytt vær av seg selv

    // Nåverdier (glir mot målet)
    public float Clouds = 0.45f, Dark, Wind = 0.3f, Precip, Fog, Lightning;
    public float Wetness, SnowAccum;
    public bool PrecipIsSnow;
    public Vector2 WindDir = new(0.8f, 0.6f);
    public float Flash;                     // lynglimt 0..1 (DayNight legger det på lyset)

    private float _windAngle = 0.64f, _windAngleTarget = 0.64f;
    private float _timer = 120f;
    private float _lightningTimer = 8f;
    private readonly RandomNumberGenerator _rng = new();
    private GpuParticles3D _rain, _snow;
    private GpuParticlesCollisionHeightField3D _collider;
    private DayNight _dn;

    private struct Target { public float Clouds, Dark, Wind, Precip, Fog, Lightning; }

    private static Target For(WeatherKind k) => k switch
    {
        WeatherKind.Clear => new Target { Clouds = 0.18f, Wind = 0.18f },
        WeatherKind.Cloudy => new Target { Clouds = 0.5f, Dark = 0.08f, Wind = 0.35f },
        WeatherKind.Overcast => new Target { Clouds = 0.88f, Dark = 0.45f, Wind = 0.45f, Fog = 0.15f },
        WeatherKind.Rain => new Target { Clouds = 0.97f, Dark = 0.62f, Wind = 0.55f, Precip = 0.7f, Fog = 0.35f },
        WeatherKind.Storm => new Target { Clouds = 1f, Dark = 0.82f, Wind = 1f, Precip = 1f, Fog = 0.4f, Lightning = 1f },
        WeatherKind.Fog => new Target { Clouds = 0.6f, Dark = 0.3f, Wind = 0.06f, Fog = 1f },
        _ => new Target(),
    };

    public static string Label(WeatherKind k, bool snow) => k switch
    {
        WeatherKind.Clear => "Klart",
        WeatherKind.Cloudy => "Lettskyet",
        WeatherKind.Overcast => "Overskyet",
        WeatherKind.Rain => snow ? "Snøvær" : "Regn",
        WeatherKind.Storm => snow ? "Snøstorm" : "Storm",
        WeatherKind.Fog => "Tåke",
        _ => "",
    };

    public string Description => Label(Kind, PrecipIsSnow);

    // ------------------------------------------------------------ Årstider
    /// <summary>0..1 gjennom året, 0 = vårjevndøgn, 0.25 = sommersolverv, 0.5 = høst, 0.75 = vintersolverv.</summary>
    public float YearT => Mathf.PosMod((_dn.Day + _dn.TimeOfDay) / (4f * DaysPerSeason), 1f);
    public int Season => (int)(YearT * 4f) % 4;
    public string SeasonName => SeasonNames[Season];
    public int DayOfSeason => _dn.Day % DaysPerSeason + 1;

    /// <summary>Temperaturtillegg for årstiden (topp midt på sommeren, bunn midt på vinteren).</summary>
    public float SeasonTemp => 0.32f * Mathf.Sin(Mathf.Tau * (YearT - 0.125f));

    /// <summary>Temperaturen snøen ligger ned til nå (årstid + nysnø). 0 = bare permanent snø.</summary>
    public float SnowLine => Mathf.Max(0f, 0.1f - SeasonTemp * 1.6f) + SnowAccum;

    public override void _Ready()
    {
        Current = this;
        _dn = GetParent<Node>().GetNode<DayNight>("DayNight");
        _rng.Seed = (ulong)Main.Seed * 7919UL + 13UL;
        if (OS.GetEnvironment("HK_NOPRECIP") == "1") { Apply(0f, true); return; }   // testbryter
        _rain = MakeRain();
        _snow = MakeSnow();
        AddChild(_rain);
        AddChild(_snow);
        // Regn og snø stopper på bakken, trær og tak rundt kameraet
        _collider = new GpuParticlesCollisionHeightField3D
        {
            Name = "PrecipCollision",
            Size = new Vector3(64, 60, 64),
            Resolution = GpuParticlesCollisionHeightField3D.ResolutionEnum.Resolution256,
            UpdateMode = GpuParticlesCollisionHeightField3D.UpdateModeEnum.WhenMoved,
            FollowCameraEnabled = true,
        };
        AddChild(_collider);
        Apply(0f, true);
    }

    public override void _ExitTree() { if (Current == this) Current = null; }

    /// <summary>Setter vær direkte (debug/autotest), med umiddelbar overgang hvis snap.</summary>
    public void Set(WeatherKind k, bool snap = false)
    {
        Kind = k;
        _timer = _rng.RandfRange(180f, 420f);
        if (snap)
        {
            var t = For(k);
            Clouds = t.Clouds; Dark = t.Dark; Wind = t.Wind; Precip = t.Precip; Fog = t.Fog; Lightning = t.Lightning;
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (!Locked)
        {
            _timer -= dt;
            if (_timer <= 0f) Set(PickNext());
        }
        Apply(dt, false);
    }

    private WeatherKind PickNext()
    {
        int s = Season;
        // Vekter: klart, lettskyet, overskyet, nedbør, storm, tåke
        float[] w = s switch
        {
            0 => new[] { 3f, 4f, 3f, 2.5f, 0.4f, 1.2f },   // vår
            1 => new[] { 6f, 4f, 2f, 1.5f, 0.6f, 0.5f },   // sommer
            2 => new[] { 2f, 3f, 4f, 3.5f, 1.2f, 2f },     // høst
            _ => new[] { 3f, 2f, 4f, 3.5f, 1f, 1f },       // vinter
        };
        // Litt "hukommelse": samme vær igjen er mindre sannsynlig, nabotilstander mer
        w[(int)Kind] *= 0.4f;
        float sum = 0f;
        foreach (var x in w) sum += x;
        float r = _rng.Randf() * sum;
        for (int i = 0; i < w.Length; i++)
        {
            r -= w[i];
            if (r <= 0f) return (WeatherKind)i;
        }
        return WeatherKind.Clear;
    }

    private void Apply(float dt, bool snap)
    {
        var t = For(Kind);
        float k = snap ? 1f : 1f - Mathf.Exp(-dt / 25f);       // ~25 s tidskonstant
        Clouds = Mathf.Lerp(Clouds, t.Clouds, k);
        Dark = Mathf.Lerp(Dark, t.Dark, k);
        Wind = Mathf.Lerp(Wind, t.Wind, k);
        Fog = Mathf.Lerp(Fog, t.Fog, k);
        Lightning = Mathf.Lerp(Lightning, t.Lightning, k);
        // Nedbøren venter til skyene er tette nok
        float precipTarget = t.Precip * Biomes.SmoothStep(0.75f, 0.92f, Clouds);
        Precip = Mathf.Lerp(Precip, precipTarget, snap ? 1f : 1f - Mathf.Exp(-dt / 12f));

        // Vindretningen dreier sakte
        if (_rng.Randf() < dt * 0.01f) _windAngleTarget += _rng.RandfRange(-1.2f, 1.2f);
        _windAngle = Mathf.Lerp(_windAngle, _windAngleTarget, 1f - Mathf.Exp(-dt / 40f));
        WindDir = new Vector2(Mathf.Cos(_windAngle), Mathf.Sin(_windAngle));

        // Temperatur der spilleren er: avgjør regn eller snø
        var p = Game.Focus;
        float localTemp = Game.World != null ? Game.World.GetTemp(p.X, p.Z) : 0.5f;
        float eff = localTemp + SeasonTemp;
        PrecipIsSnow = localTemp < SnowLine + 0.03f;

        // Våthet og snø som legger seg
        float snowing = PrecipIsSnow ? Precip : 0f, raining = PrecipIsSnow ? 0f : Precip;
        Wetness = Mathf.Clamp(Wetness + (raining * 0.08f - (1f - raining) * 0.006f * (1f + (1f - Clouds) * 2f)) * dt, 0f, 1f);
        SnowAccum = Mathf.Clamp(SnowAccum + (snowing * 0.004f - (eff > 0.45f ? 0.003f : 0.0004f)) * dt, 0f, 0.25f);

        // Årstid -> globale parametre
        float y = YearT;
        float autumn = y > 0.5f ? Biomes.SmoothStep(0.56f, 0.68f, y) : 0f;
        float drop = y > 0.5f ? Biomes.SmoothStep(0.68f, 0.78f, y) : 1f - Biomes.SmoothStep(0.0f, 0.07f, y);
        // Snølinje = temperaturen snøen går ned til. Vinter: langt sørover; sommer: ingen ekstra snø.
        float snowLine = SnowLine;
        RenderingServer.GlobalShaderParameterSet("wind_dir", WindDir);
        RenderingServer.GlobalShaderParameterSet("wind_strength", Wind);
        RenderingServer.GlobalShaderParameterSet("season_leaf_tint", new Vector4(0.85f, 0.42f, 0.08f, autumn));
        RenderingServer.GlobalShaderParameterSet("season_leaf_drop", drop);
        RenderingServer.GlobalShaderParameterSet("season_conifer", Biomes.SmoothStep(0.05f, 0.3f, snowLine) * 0.6f);
        RenderingServer.GlobalShaderParameterSet("snow_line", snowLine);
        RenderingServer.GlobalShaderParameterSet("wetness", Wetness);

        // Lyn
        Flash = Mathf.Max(0f, Flash - dt * 4f);
        if (Lightning > 0.5f && Precip > 0.5f)
        {
            _lightningTimer -= dt;
            if (_lightningTimer <= 0f)
            {
                Flash = 1f;
                _lightningTimer = _rng.RandfRange(4f, 16f);
                // Dobbeltglimt av og til
                if (_rng.Randf() < 0.4f) GetTree().CreateTimer(0.18f).Timeout += () => Flash = 0.8f;
            }
        }

        UpdateParticles();
    }

    // ------------------------------------------------------------ Nedbør
    private void UpdateParticles()
    {
        var cam = GetViewport()?.GetCamera3D();
        if (cam == null || _rain == null) return;
        var cp = cam.GlobalPosition;
        // Partiklene simuleres i verdensrom; emitteren flyttes med kameraet
        _rain.GlobalPosition = cp + new Vector3(WindDir.X, 0, WindDir.Y) * Wind * 6f + Vector3.Up * 14f;
        _snow.GlobalPosition = cp + Vector3.Up * 10f;
        float rain = PrecipIsSnow ? 0f : Precip;
        float snow = PrecipIsSnow ? Precip : 0f;
        _rain.Emitting = rain > 0.03f;
        _snow.Emitting = snow > 0.03f;
        _rain.AmountRatio = Mathf.Clamp(rain, 0f, 1f);
        _snow.AmountRatio = Mathf.Clamp(snow, 0f, 1f);
        var wind = new Vector3(WindDir.X, 0, WindDir.Y) * Wind;
        ((ParticleProcessMaterial)_rain.ProcessMaterial).Gravity = new Vector3(wind.X * 18f, -32f, wind.Z * 18f);
        ((ParticleProcessMaterial)_snow.ProcessMaterial).Gravity = new Vector3(wind.X * 5f, -1.6f, wind.Z * 5f);
    }

    private static GpuParticles3D MakeRain()
    {
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(28, 2, 28),
            Direction = new Vector3(0, -1, 0),
            Spread = 2f,
            InitialVelocityMin = 14f,
            InitialVelocityMax = 18f,
            Gravity = new Vector3(0, -32, 0),
            CollisionMode = ParticleProcessMaterial.CollisionModeEnum.HideOnContact,
        };
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0.75f, 0.8f, 0.88f, 0.28f),
            VertexColorUseAsAlbedo = false,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            // Dråper helt inntil kameraet blir enorme striper; tone dem ut
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha,
            DistanceFadeMinDistance = 1.5f,
            DistanceFadeMaxDistance = 5f,
        };
        var mesh = new QuadMesh { Size = new Vector2(0.012f, 0.55f), Material = mat };
        return new GpuParticles3D
        {
            Name = "Rain",
            Amount = 9000,
            Lifetime = 1.1f,
            Preprocess = 1f,
            LocalCoords = false,
            Emitting = false,
            ProcessMaterial = pm,
            DrawPass1 = mesh,
            TransformAlign = GpuParticles3D.TransformAlignEnum.ZBillboardYToVelocity,
            VisibilityAabb = new Aabb(new Vector3(-40, -50, -40), new Vector3(80, 70, 80)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CollisionBaseSize = 0.05f,
        };
    }

    private static GpuParticles3D MakeSnow()
    {
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(26, 6, 26),
            Direction = new Vector3(0, -1, 0),
            Spread = 25f,
            InitialVelocityMin = 0.6f,
            InitialVelocityMax = 1.4f,
            Gravity = new Vector3(0, -1.6f, 0),
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 2.5f,
            TurbulenceNoiseScale = 6f,
            TurbulenceInfluenceMin = 0.05f,
            TurbulenceInfluenceMax = 0.15f,
            ScaleMin = 0.6f,
            ScaleMax = 1.3f,
            CollisionMode = ParticleProcessMaterial.CollisionModeEnum.HideOnContact,
        };
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0.95f, 0.96f, 1f, 0.85f),
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            AlbedoTexture = SoftDot(),
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha,
            DistanceFadeMinDistance = 0.3f,
            DistanceFadeMaxDistance = 1.2f,
        };
        var mesh = new QuadMesh { Size = new Vector2(0.07f, 0.07f), Material = mat };
        return new GpuParticles3D
        {
            Name = "Snow",
            Amount = 7000,
            Lifetime = 9f,
            Preprocess = 9f,
            LocalCoords = false,
            Emitting = false,
            ProcessMaterial = pm,
            DrawPass1 = mesh,
            VisibilityAabb = new Aabb(new Vector3(-40, -40, -40), new Vector3(80, 60, 80)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CollisionBaseSize = 0.03f,
        };
    }

    /// <summary>Liten myk, rund prikk (snøfnugg).</summary>
    public static Texture2D SoftDot(int size = 16)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
            float a = Mathf.Clamp(1f - d, 0f, 1f);
            img.SetPixel(x, y, new Color(1, 1, 1, a * a));
        }
        return ImageTexture.CreateFromImage(img);
    }
}
