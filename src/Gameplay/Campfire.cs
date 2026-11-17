using System.Collections.Generic;
using Godot;
using Heimskringla.Core;
using Heimskringla.Environment;
using Heimskringla.UI;

namespace Heimskringla.Gameplay;

/// <summary>
/// Campfire: stone ring with firewood, flames, sparks, smoke drifting with the wind and a flickering light.
/// Burns as long as it has fuel; [E] adds more (timber or branch). Rain makes it burn down faster.
/// </summary>
public partial class Campfire : StaticBody3D, IInteractable
{
    public const int CostBranches = 3, CostStones = 5;
    public static readonly List<Campfire> All = new();

    public float Fuel = 150f;        // seconds left
    private const float MaxFuel = 900f;

    private OmniLight3D _light;
    private GpuParticles3D _flames, _sparks, _smoke, _embers;
    private float _t, _flick;
    private readonly Godot.FastNoiseLite _noise = new() { Frequency = 3.5f };

    public bool Burning => Fuel > 0f;

    public string Prompt
    {
        get
        {
            string what = Inventory.Count("wood") > 0 ? Loc.T("ITEM_WOOD").ToLowerInvariant()
                : Inventory.Count("branch") > 0 ? Loc.T("ITEM_BRANCH").ToLowerInvariant() : null;
            string minutes = (Fuel / 60f).ToString("F0");
            if (what == null) return Burning ? Loc.F("PROMPT_CAMPFIRE_BURNING_NO_FUEL", minutes) : Loc.T("PROMPT_CAMPFIRE_OUT_NO_FUEL");
            return Burning ? Loc.F("PROMPT_CAMPFIRE_ADD", what, minutes) : Loc.F("PROMPT_CAMPFIRE_LIGHT", what);
        }
    }

    public void Interact(Player p)
    {
        if (Inventory.Count("wood") > 0) { Inventory.Add("wood", -1); Fuel = Mathf.Min(Fuel + 300f, MaxFuel); }
        else if (Inventory.Count("branch") > 0) { Inventory.Add("branch", -1); Fuel = Mathf.Min(Fuel + 90f, MaxFuel); }
        else { Hud.Instance?.Toast(Loc.T("TOAST_NO_FIREWOOD")); return; }
        Hud.Instance?.Toast(Burning ? Loc.T("TOAST_FIRE_FLARES") : Loc.T("TOAST_FIRE_LIT"));
    }

    public override void _Ready()
    {
        All.Add(this);
        CollisionLayer = Game.LayerWorld | Game.LayerInteract;
        AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.7f, Height = 0.4f }, Position = new Vector3(0, 0.2f, 0) });

        if (ResourceLoader.Exists("res://assets/survival/campfire-pit.glb"))
        {
            var pit = GD.Load<PackedScene>("res://assets/survival/campfire-pit.glb").Instantiate<Node3D>();
            pit.Scale = Vector3.One * 5f;
            AddChild(pit);
        }

        _light = new OmniLight3D
        {
            LightColor = new Color(1f, 0.55f, 0.22f),
            OmniRange = 11f,
            OmniAttenuation = 1.4f,
            LightEnergy = 2.5f,
            ShadowEnabled = true,
            ShadowBias = 0.15f,
            Position = new Vector3(0, 0.9f, 0),
        };
        AddChild(_light);

        _flames = MakeFlames();
        _sparks = MakeSparks();
        _smoke = MakeSmoke();
        _embers = MakeEmbers();
        // Crackling (procedural sound, 3D)
        var gen = new AudioStreamGenerator { MixRate = 22050, BufferLength = 0.2f };
        _sound = new AudioStreamPlayer3D { Stream = gen, UnitSize = 3f, MaxDistance = 40f, Position = new Vector3(0, 0.5f, 0), VolumeDb = -2f };
        AddChild(_sound);
        _sound.Play();
        _pb = (AudioStreamGeneratorPlayback)_sound.GetStreamPlayback();
        AddChild(_embers);
        AddChild(_flames);
        AddChild(_sparks);
        AddChild(_smoke);
    }

    public override void _ExitTree() => All.Remove(this);

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        var wx = Weather.Current;
        // Rain and wind make the fire burn faster
        float burn = 1f + (wx != null && !wx.PrecipIsSnow ? wx.Precip * 1.5f : 0f) + (wx?.Wind ?? 0f) * 0.5f;
        if (Burning) Fuel = Mathf.Max(0f, Fuel - dt * burn);

        // Intensity: flares up when there is plenty of fuel, dies down slowly over the last 30 seconds
        float strength = Mathf.Clamp(Fuel / 30f, 0f, 1f) * Mathf.Lerp(0.85f, 1.15f, Mathf.Clamp(Fuel / MaxFuel, 0f, 1f));
        _flick = _noise.GetNoise1D(_t * 10f) * 0.5f + _noise.GetNoise1D(_t * 37f + 100f) * 0.25f;
        _light.LightEnergy = strength * (2.4f + _flick * 1.1f) + (1f - strength) * 0.15f * (Fuel > 0 ? 1 : 0);
        _light.Visible = Fuel > 0f || _embersGlow > 0.01f;
        _light.Position = new Vector3(_noise.GetNoise1D(_t * 6f + 50f) * 0.08f, 0.9f + _flick * 0.1f, _noise.GetNoise1D(_t * 6f + 90f) * 0.08f);
        _flames.Emitting = strength > 0.05f;
        _flames.AmountRatio = Mathf.Clamp(strength, 0.1f, 1f);
        _flames.SpeedScale = 0.9f + strength * 0.3f;
        _sparks.Emitting = strength > 0.3f;
        _smoke.Emitting = Fuel > 0f || _embersGlow > 0.2f;
        _embersGlow = Burning ? 1f : Mathf.Max(0f, _embersGlow - dt / 60f);
        _embers.Emitting = _embersGlow > 0.05f;
        FillSound(strength);

        if (wx != null)
        {
            var wind = new Vector3(wx.WindDir.X, 0, wx.WindDir.Y) * wx.Wind;
            ((ParticleProcessMaterial)_smoke.ProcessMaterial).Gravity = new Vector3(wind.X * 2.5f, 0.7f, wind.Z * 2.5f);
            ((ParticleProcessMaterial)_flames.ProcessMaterial).Gravity = new Vector3(wind.X * 1.5f, 1.5f, wind.Z * 1.5f);
            ((ParticleProcessMaterial)_sparks.ProcessMaterial).Gravity = new Vector3(wind.X * 2.5f, 0.6f, wind.Z * 2.5f);
        }
    }

    private float _embersGlow = 1f;
    private AudioStreamPlayer3D _sound;
    private AudioStreamGeneratorPlayback _pb;
    private readonly System.Random _arng = new();
    private float _hiss, _popEnv, _popPhase, _popFreq, _roar;

    private void FillSound(float strength)
    {
        if (_pb == null) return;
        int n = _pb.GetFramesAvailable();
        if (n <= 0) return;
        var buf = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float w = (float)_arng.NextDouble() * 2f - 1f;
            _hiss += (w - _hiss) * 0.08f;                       // low roar from the flames
            _roar = Mathf.Clamp(_roar + w * 0.01f, -1f, 1f) * 0.995f;
            // Pops: random short cracks with a fast decay
            if (_arng.NextDouble() < 0.0009 * strength)
            {
                _popEnv = 0.5f + (float)_arng.NextDouble() * 0.5f;
                _popFreq = 800f + (float)_arng.NextDouble() * 2500f;
            }
            _popEnv *= 0.993f;
            _popPhase += _popFreq / 22050f;
            float pop = (Mathf.Sin(_popPhase * Mathf.Tau) * 0.4f + w * 0.6f) * _popEnv * _popEnv;
            float s = (_hiss * 0.25f + _roar * 0.35f) * strength + pop * 0.6f * Mathf.Min(strength * 2f, 1f);
            buf[i] = new Vector2(s, s);
        }
        _pb.PushBuffer(buf);
    }

    // ------------------------------------------------------------ Particles
    private static GradientTexture1D Grad(params (float t, Color c)[] stops)
    {
        var offs = new float[stops.Length];
        var cols = new Color[stops.Length];
        for (int i = 0; i < stops.Length; i++) { offs[i] = stops[i].t; cols[i] = stops[i].c; }
        var g = new Gradient { Offsets = offs, Colors = cols };
        return new GradientTexture1D { Gradient = g };
    }

    private static StandardMaterial3D Additive(Texture2D tex) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
        VertexColorUseAsAlbedo = true,
        AlbedoTexture = tex,
        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
    };

    private static GpuParticles3D MakeFlames()
    {
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.28f,
            Direction = Vector3.Up,
            Spread = 12f,
            InitialVelocityMin = 0.6f,
            InitialVelocityMax = 1.2f,
            Gravity = new Vector3(0, 1.5f, 0),
            DampingMin = 0.5f, DampingMax = 1.2f,
            ScaleMin = 0.6f, ScaleMax = 1.1f,
            ScaleCurve = new CurveTexture { Curve = Shrink() },
            ColorRamp = Grad((0f, new Color(1f, 0.75f, 0.35f, 0.55f)), (0.35f, new Color(1f, 0.4f, 0.08f, 0.5f)),
                             (0.75f, new Color(0.7f, 0.12f, 0.02f, 0.25f)), (1f, new Color(0.2f, 0.02f, 0f, 0f))),
            HueVariationMin = -0.02f, HueVariationMax = 0.02f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 0.6f,
            TurbulenceNoiseScale = 2f,
            TurbulenceInfluenceMin = 0.05f, TurbulenceInfluenceMax = 0.12f,
        };
        var mat = Additive(Weather.SoftDot(32));
        // HDR brightness so glow picks up the flames
        mat.AlbedoColor = new Color(1.1f, 0.85f, 0.65f);
        return new GpuParticles3D
        {
            Name = "Flames",
            Amount = 45,
            Lifetime = 0.9f,
            Preprocess = 1f,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = new Vector2(0.55f, 0.75f), Material = mat },
            Position = new Vector3(0, 0.25f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-2, -1, -2), new Vector3(4, 5, 4)),
        };
    }

    private static GpuParticles3D MakeEmbers()
    {
        // The glow down in the fire (low, calm)
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.3f,
            Gravity = Vector3.Zero,
            ScaleMin = 0.8f, ScaleMax = 1.2f,
            ColorRamp = Grad((0f, new Color(1f, 0.3f, 0.05f, 0f)), (0.3f, new Color(1f, 0.35f, 0.05f, 0.6f)), (1f, new Color(0.6f, 0.1f, 0f, 0f))),
        };
        var mat = Additive(Weather.SoftDot(32));
        mat.AlbedoColor = new Color(2f, 1.5f, 1.2f);
        return new GpuParticles3D
        {
            Name = "Embers",
            Amount = 12,
            Lifetime = 1.6f,
            Preprocess = 1.6f,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = new Vector2(0.45f, 0.3f), Material = mat },
            Position = new Vector3(0, 0.12f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    private static GpuParticles3D MakeSparks()
    {
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.25f,
            Direction = Vector3.Up,
            Spread = 25f,
            InitialVelocityMin = 1.5f,
            InitialVelocityMax = 3.5f,
            Gravity = new Vector3(0, 0.6f, 0),
            DampingMin = 0.3f, DampingMax = 0.8f,
            ScaleMin = 0.5f, ScaleMax = 1f,
            ColorRamp = Grad((0f, new Color(1f, 0.8f, 0.4f, 1f)), (0.6f, new Color(1f, 0.4f, 0.1f, 0.8f)), (1f, new Color(0.6f, 0.1f, 0f, 0f))),
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 3f,
            TurbulenceNoiseScale = 1.5f,
            TurbulenceInfluenceMin = 0.1f, TurbulenceInfluenceMax = 0.3f,
        };
        var mat = Additive(Weather.SoftDot(16));
        mat.AlbedoColor = new Color(3f, 2.2f, 1.5f);
        return new GpuParticles3D
        {
            Name = "Sparks",
            Amount = 24,
            Lifetime = 2.2f,
            Randomness = 0.8f,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = new Vector2(0.04f, 0.04f), Material = mat },
            Position = new Vector3(0, 0.4f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-4, -1, -4), new Vector3(8, 10, 8)),
        };
    }

    private static GpuParticles3D MakeSmoke()
    {
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.2f,
            Direction = Vector3.Up,
            Spread = 10f,
            InitialVelocityMin = 0.6f,
            InitialVelocityMax = 1.0f,
            Gravity = new Vector3(0, 0.7f, 0),
            DampingMin = 0.2f, DampingMax = 0.4f,
            AngleMin = -180f, AngleMax = 180f,
            AngularVelocityMin = -20f, AngularVelocityMax = 20f,
            ScaleMin = 0.8f, ScaleMax = 1.3f,
            ScaleCurve = new CurveTexture { Curve = Waterfalls.Ramp(0.35f, 2.6f) },
            ColorRamp = Grad((0f, new Color(0.35f, 0.33f, 0.32f, 0f)), (0.15f, new Color(0.35f, 0.33f, 0.32f, 0.4f)),
                             (1f, new Color(0.55f, 0.55f, 0.56f, 0f))),
        };
        var mat = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true,
            AlbedoTexture = Weather.SoftDot(32),
            ProximityFadeEnabled = true,
            ProximityFadeDistance = 0.6f,
        };
        return new GpuParticles3D
        {
            Name = "Smoke",
            Amount = 40,
            Lifetime = 6f,
            Preprocess = 4f,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = new Vector2(1f, 1f), Material = mat },
            Position = new Vector3(0, 1.0f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-12, -1, -12), new Vector3(24, 16, 24)),
        };
    }

    private static Curve Shrink()
    {
        var c = new Curve();
        c.AddPoint(new Vector2(0, 0.6f));
        c.AddPoint(new Vector2(0.25f, 1f));
        c.AddPoint(new Vector2(1, 0.15f));
        return c;
    }
}
