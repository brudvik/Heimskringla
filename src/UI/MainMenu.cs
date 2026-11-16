using System;
using Godot;
using Heimskringla.Core;

namespace Heimskringla.UI;

/// <summary>
/// Intro og hovedmeny.
///
/// Intro (første gang spillet startes, kan hoppes over med tast/klikk):
///   svart → "Kjell Arne Brudvik presenterer" → bakgrunnsbildet toner fram med sakte zoom,
///   drivende tåke og lysstråler → logoen stiger fram → menyen.
/// Under ligger vind og en dyp, langsom klangflate (syntetisert, d-moll, svak hornrop).
///
/// Menyen: Fortsett (hvis lagring for valgt seed), Nytt spill, seed (tall, terning, standard),
/// Innstillinger, Avslutt. "Utviklet av Kjell Arne Brudvik" nederst.
/// </summary>
public partial class MainMenu : CanvasLayer
{
    /// <summary>Kalles når spilleren starter: (seed, last lagring).</summary>
    public Action<int, bool> OnStart;
    public bool SkipIntro;

    private Control _root;
    private TextureRect _bg, _logo;
    private Label _presents, _presentsName, _tagline, _credit;
    private VBoxContainer _menu;
    private ColorRect _black;
    private TextureRect _menuShade;
    private Label _version;
    private SettingsPanel _settings;
    private LineEdit _seedEdit;
    private Button _continue;
    private float _t;
    private bool _menuShown, _leaving;
    private IntroMusic _music;
    private AudioStreamPlayer _wind;

    // Tidslinje (sekunder)
    private const float PresentsIn = 0.8f, PresentsOut = 4.2f, BgIn = 3.6f, LogoIn = 6.2f, MenuIn = 8.6f;

    public override void _Ready()
    {
        Layer = 50;
        ProcessMode = ProcessModeEnum.Always;
        Settings.Load();
        MenuSound.Attach(this);

        _root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        var baseBg = new ColorRect { Color = new Color(0.01f, 0.01f, 0.015f) };
        baseBg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(baseBg);

        // Bakgrunnsbildet (sakte zoom/panorering)
        const string bgPath = "res://assets/logo/heimskringla-bg.png";
        if (ResourceLoader.Exists(bgPath))
        {
            _bg = new TextureRect
            {
                Texture = GD.Load<Texture2D>(bgPath),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                Modulate = new Color(1, 1, 1, 0),
            };
            _bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(_bg);
        }
        // Tåke, lysstråler og vignett
        var mist = new ColorRect { Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/menu_mist.gdshader") }, MouseFilter = Control.MouseFilterEnum.Ignore };
        mist.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(mist);
        // Snø/aske som daler sakte
        var snow = new CpuParticles2D
        {
            Amount = 140, Lifetime = 14f, Preprocess = 14f,
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
            Direction = new Vector2(0.25f, 1f), Spread = 12f,
            Gravity = new Vector2(0, 4f), InitialVelocityMin = 18f, InitialVelocityMax = 40f,
            ScaleAmountMin = 1.2f, ScaleAmountMax = 3.2f,
            Color = new Color(1f, 0.97f, 0.92f, 0.55f),
        };
        _root.AddChild(snow);
        _root.Resized += () =>
        {
            snow.Position = new Vector2(_root.Size.X * 0.5f, -20f);
            snow.EmissionRectExtents = new Vector2(_root.Size.X * 0.6f, 10f);
        };

        // Svart toning ligger under presenter-kortet (som står på svart), men over bildet
        _black = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        _black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_black);

        // "Presenterer"-kortet
        var pres = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        pres.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        pres.MouseFilter = Control.MouseFilterEnum.Ignore;
        _root.AddChild(pres);
        _presentsName = MenuUi.Label("Kjell Arne Brudvik", 44, MenuUi.GoldBright, MenuUi.Title);
        _presents = MenuUi.Label("presenterer", 22, MenuUi.TextDim);
        _presentsName.Modulate = _presents.Modulate = new Color(1, 1, 1, 0);
        pres.AddChild(_presentsName);
        pres.AddChild(_presents);

        // Logo og undertittel
        const string logoPath = "res://assets/logo/heimskringla-logo.png";
        var top = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin };
        top.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        top.MouseFilter = Control.MouseFilterEnum.Ignore;
        top.AddThemeConstantOverride("separation", 0);
        _root.AddChild(top);
        top.AddChild(new Control { CustomMinimumSize = new Vector2(0, 40) });
        if (ResourceLoader.Exists(logoPath))
        {
            _logo = new TextureRect
            {
                Texture = GD.Load<Texture2D>(logoPath),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(0, 300),
                Modulate = new Color(1, 1, 1, 0),
            };
            top.AddChild(_logo);
        }
        _tagline = MenuUi.Label("En saga fra Norden", 24, MenuUi.Gold);
        _tagline.Modulate = new Color(1, 1, 1, 0);
        top.AddChild(_tagline);

        // Myk, mørk skygge bak menyen (lesbarhet mot den lyse himmelen/fjorden)
        var shadeTex = new GradientTexture2D
        {
            Width = 256, Height = 256, Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f),
            Gradient = new Gradient { Offsets = new[] { 0f, 1f }, Colors = new[] { new Color(0, 0, 0, 0.78f), new Color(0, 0, 0, 0f) } },
        };
        _menuShade = new TextureRect { Texture = shadeTex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
        _menuShade.SetAnchorsPreset(Control.LayoutPreset.Center);
        _menuShade.OffsetLeft = -560; _menuShade.OffsetRight = 560; _menuShade.OffsetTop = 40; _menuShade.OffsetBottom = 460;
        _root.AddChild(_menuShade);

        // Menyen
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        center.OffsetTop = 330;
        _root.AddChild(center);
        _menu = new VBoxContainer { Modulate = new Color(1, 1, 1, 0), Visible = false };
        _menu.AddThemeConstantOverride("separation", 4);
        center.AddChild(_menu);
        BuildMenu();

        _settings = new SettingsPanel { Visible = false, OnBack = ShowMainButtons };
        center.AddChild(_settings);

        // Kreditering nederst
        _credit = MenuUi.Label("Utviklet av Kjell Arne Brudvik", 18, MenuUi.TextDim);
        _credit.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _credit.GrowHorizontal = Control.GrowDirection.Both;
        _credit.OffsetTop = -52; _credit.OffsetBottom = -22;
        _credit.Modulate = new Color(1, 1, 1, 0);
        _root.AddChild(_credit);
        var ver = MenuUi.Label("tidlig utviklingsversjon", 13, new Color(MenuUi.TextDim, 0.6f));
        _version = ver;
        ver.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        ver.GrowHorizontal = Control.GrowDirection.Begin;
        ver.GrowVertical = Control.GrowDirection.Begin;
        ver.OffsetRight = -18; ver.OffsetBottom = -14;
        _root.AddChild(ver);

        // Lyd: vind + klangflate
        _music = new IntroMusic();
        AddChild(_music);
        const string windPath = "res://assets/audio/wind/wind_open.ogg";
        if (ResourceLoader.Exists(windPath))
        {
            var ws = GD.Load<AudioStreamOggVorbis>(windPath);
            ws.Loop = true;
            _wind = new AudioStreamPlayer { Stream = ws, VolumeDb = -80f };
            AddChild(_wind);
            _wind.Play();
        }

        Input.MouseMode = Input.MouseModeEnum.Visible;
        if (SkipIntro) _t = MenuIn;
    }

    private void BuildMenu()
    {
        _continue = MenuUi.Button("Fortsett", 30, () => Start(true));
        _menu.AddChild(_continue);
        _menu.AddChild(MenuUi.Button("Nytt spill", 30, () => Start(false)));

        // Seed-rad
        var seedRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        seedRow.AddThemeConstantOverride("separation", 10);
        var sl = MenuUi.Label("Verden", 22, MenuUi.Gold);
        seedRow.AddChild(sl);
        _seedEdit = new LineEdit { Text = Settings.LastSeed.ToString(), CustomMinimumSize = new Vector2(140, 0), Alignment = HorizontalAlignment.Center, MaxLength = 9 };
        MenuUi.StyleInput(_seedEdit);
        _seedEdit.TextChanged += txt =>
        {
            string digits = "";
            foreach (char c in txt) if (char.IsDigit(c)) digits += c;
            if (digits != txt) { _seedEdit.Text = digits; _seedEdit.CaretColumn = digits.Length; }
            UpdateContinue();
        };
        seedRow.AddChild(_seedEdit);
        var dice = MenuUi.Button("Tilfeldig", 20, () => { _seedEdit.Text = ((int)(GD.Randi() % 1000000)).ToString(); UpdateContinue(); });
        dice.CustomMinimumSize = new Vector2(120, 34);
        seedRow.AddChild(dice);
        var std = MenuUi.Button("Standard", 20, () => { _seedEdit.Text = Settings.DefaultSeed.ToString(); UpdateContinue(); });
        std.CustomMinimumSize = new Vector2(120, 34);
        seedRow.AddChild(std);
        _menu.AddChild(seedRow);

        _menu.AddChild(MenuUi.Button("Innstillinger", 30, ShowSettings));
        _menu.AddChild(MenuUi.Button("Avslutt", 30, () => GetTree().Quit()));
        UpdateContinue();
    }

    private int CurrentSeed => int.TryParse(_seedEdit.Text, out int s) ? s : Settings.DefaultSeed;

    private void UpdateContinue()
    {
        bool can = WorldState.Exists(CurrentSeed);
        _continue.Visible = can;
    }

    private void ShowSettings()
    {
        _menu.Visible = false;
        _settings.Visible = true;
    }

    private void ShowMainButtons()
    {
        _settings.Visible = false;
        _menu.Visible = true;
    }

    private void Start(bool load)
    {
        if (_leaving) return;
        _leaving = true;
        Settings.LastSeed = CurrentSeed;
        Settings.Save();
        var tw = CreateTween();
        tw.TweenProperty(_black, "color:a", 1f, 1.1f);
        tw.TweenCallback(Callable.From(() =>
        {
            OnStart?.Invoke(CurrentSeed, load);
            QueueFree();
        }));
    }

    public override void _Input(InputEvent e)
    {
        // Hopp over introen
        if (!_menuShown && _t < MenuIn && (e is InputEventKey { Pressed: true } || e is InputEventMouseButton { Pressed: true }))
        {
            _t = MenuIn;
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        float A(float start, float dur) => Mathf.Clamp((_t - start) / dur, 0f, 1f);
        float Ease(float x) => x * x * (3f - 2f * x);

        // Presenterer: inn og ut (bare når introen spilles)
        float pin = Ease(A(PresentsIn, 1.4f)) * (1f - Ease(A(PresentsOut - 1.1f, 1.1f)));
        if (_t >= MenuIn) pin = 0f;
        _presentsName.Modulate = new Color(1, 1, 1, pin);
        _presents.Modulate = new Color(1, 1, 1, pin * Ease(A(PresentsIn + 0.6f, 1.2f)));

        // Svart → bilde
        _black.Color = new Color(0, 0, 0, _leaving ? _black.Color.A : 1f - Ease(A(BgIn, 3.2f)));
        if (_bg != null)
        {
            _bg.Modulate = new Color(1, 1, 1, 1);
            // Sakte "Ken Burns": fra nært inn mot hele bildet, og en svak drift
            float z = 1.12f - 0.1f * Ease(Mathf.Clamp((_t - BgIn) / 60f, 0f, 1f));
            // (bare zoom; sakte sideveis drift ble festet til hele piksler og så ut som risting)
            _bg.PivotOffset = _bg.Size * 0.5f;
            _bg.Scale = new Vector2(z, z);
        }
        if (_logo != null)
        {
            float l = Ease(A(LogoIn, 2.4f));
            _logo.Modulate = new Color(1, 1, 1, l);
            _logo.PivotOffset = _logo.Size * 0.5f;
            float s = 0.94f + 0.06f * l;
            _logo.Scale = new Vector2(s, s);
        }
        _tagline.Modulate = new Color(1, 1, 1, Ease(A(LogoIn + 1.4f, 1.8f)) * 0.9f);
        _credit.Modulate = new Color(1, 1, 1, Ease(A(MenuIn - 0.4f, 1.5f)) * 0.85f);
        _menuShade.Modulate = new Color(1, 1, 1, Ease(A(MenuIn - 0.6f, 1.6f)));
        _version.Modulate = _credit.Modulate;

        // Testbryter: HK_MENUSHOT=<mappe> tar bilder av introen og menyen, og avslutter
        var shotDir = OS.GetEnvironment("HK_MENUSHOT");
        if (!string.IsNullOrEmpty(shotDir))
        {
            foreach (var (at, name) in new[] { (2.6f, "menu_1_presents"), (7.4f, "menu_2_logo"), (11.5f, "menu_3_menu") })
                if (_t >= at && _t - dt < at)
                    GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(shotDir, name + ".png"));
            if (_t > 12f && !_leaving) { ShowSettings(); }
            if (_t > 13f) { GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(shotDir, "menu_4_settings.png")); GetTree().Quit(); }
        }

        if (_t >= MenuIn && !_menuShown)
        {
            _menuShown = true;
            _menu.Visible = true;
            var tw = CreateTween();
            tw.TweenProperty(_menu, "modulate:a", 1f, 1.2f);
            if (_continue.Visible) _continue.CallDeferred(Control.MethodName.GrabFocus);
        }

        // Lyd: vinden kommer først, klangflaten svulmer opp mot logoen; alt tones ut ved start
        float master = _leaving ? Mathf.Max(0f, 1f - _black.Color.A) : 1f;
        float mv = Settings.MusicVolume * master;
        if (_wind != null) _wind.VolumeDb = Mathf.LinearToDb(Mathf.Max(0.0001f, Ease(A(0.2f, 3f)) * 0.5f * mv));
        _music.Level = Ease(A(2.5f, 6f)) * mv;
    }
}

/// <summary>
/// Langsom, mørk klangflate til menyen: d-moll med kvint og oktav (detunede sagtenner, lavpass
/// som puster), en dyp grunntone, og innimellom et fjernt "lurhorn" (lav kvint som svulmer).
/// </summary>
public partial class IntroMusic : Node
{
    public float Level;
    private const int Rate = 22050;
    private AudioStreamGeneratorPlayback _pb;
    private double _t;
    private readonly double[] _ph = new double[12];
    private float _lp1, _lp2, _lvl, _hornT = -1f, _hornPh, _hornLp, _nextHorn = 9f;
    private int _chord;
    private float _chordT;
    private readonly Random _rng = new(7);

    // Akkorder (Hz, grunntone + tre stemmer): Dm, Bb, F, C — rolig nordisk sekvens
    private static readonly float[][] Chords =
    {
        new[] { 73.42f, 146.83f, 174.61f, 220.00f },
        new[] { 58.27f, 116.54f, 174.61f, 233.08f },
        new[] { 87.31f, 130.81f, 174.61f, 220.00f },
        new[] { 65.41f, 130.81f, 196.00f, 261.63f },
    };
    private readonly float[] _freq = new float[4];

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        var gen = new AudioStreamGenerator { MixRate = Rate, BufferLength = 0.3f };
        var p = new AudioStreamPlayer { Stream = gen, VolumeDb = -6f };
        AddChild(p);
        p.Play();
        _pb = (AudioStreamGeneratorPlayback)p.GetStreamPlayback();
        Array.Copy(Chords[0], _freq, 4);
    }

    public override void _Process(double delta)
    {
        if (_pb == null) return;
        int frames = _pb.GetFramesAvailable();
        if (frames <= 0) return;
        var buf = new Vector2[frames];
        float dt = 1f / Rate;
        for (int i = 0; i < frames; i++)
        {
            _t += dt;
            _lvl += (Level - _lvl) * 0.0002f;
            _chordT += dt;
            if (_chordT > 11f) { _chordT = 0f; _chord = (_chord + 1) % Chords.Length; }
            var target = Chords[_chord];
            for (int v = 0; v < 4; v++) _freq[v] += (target[v] - _freq[v]) * 0.00004f;   // langsom glid mellom akkordene

            float l = 0f, r = 0f;
            for (int v = 0; v < 4; v++)
            {
                for (int d = 0; d < 3; d++)
                {
                    int k = v * 3 + d;
                    float det = 1f + (d - 1) * 0.0035f;
                    _ph[k] += _freq[v] * det * dt;
                    if (_ph[k] > 1.0) _ph[k] -= 1.0;
                    float saw = (float)(_ph[k] * 2.0 - 1.0);
                    float amp = v == 0 ? 0.32f : 0.14f;
                    float pan = (d - 1) * 0.5f + (v - 1.5f) * 0.15f;
                    l += saw * amp * (1f - pan * 0.5f);
                    r += saw * amp * (1f + pan * 0.5f);
                }
            }
            // Lavpass som "puster" sakte (åpner og lukker seg over ~14 s)
            float cut = 380f + 260f * (0.5f + 0.5f * Mathf.Sin((float)_t * 0.45f));
            float a = 1f - Mathf.Exp(-Mathf.Tau * cut / Rate);
            _lp1 += (l - _lp1) * a;
            _lp2 += (r - _lp2) * a;
            float sl = _lp1, sr = _lp2;

            // Fjernt lurhorn: en lav tone som svulmer og dør ut, av og til
            _nextHorn -= dt;
            if (_nextHorn <= 0f && _hornT < 0f) { _hornT = 0f; _nextHorn = 18f + (float)_rng.NextDouble() * 14f; }
            if (_hornT >= 0f)
            {
                _hornT += dt;
                float env = Mathf.Sin(Mathf.Pi * Mathf.Clamp(_hornT / 5.5f, 0f, 1f));
                env *= env;
                _hornPh += 110f * (1f + 0.004f * Mathf.Sin(_hornT * 5f)) * dt;
                if (_hornPh > 1f) _hornPh -= 1f;
                float sq = _hornPh < 0.5f ? 1f : -1f;
                _hornLp += (sq - _hornLp) * 0.06f;
                float hs = _hornLp * env * 0.12f;
                sl += hs * 0.8f; sr += hs;
                if (_hornT > 5.5f) _hornT = -1f;
            }
            float g = _lvl * 0.35f;
            buf[i] = new Vector2(Mathf.Clamp(sl * g, -1f, 1f), Mathf.Clamp(sr * g, -1f, 1f));
        }
        _pb.PushBuffer(buf);
    }
}
