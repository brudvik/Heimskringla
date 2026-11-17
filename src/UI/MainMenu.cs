using System;
using Godot;
using Heimskringla.Core;

namespace Heimskringla.UI;

/// <summary>
/// Intro and main menu.
///
/// Intro (first time the game starts, key/click skips):
///   black → "Kjell Arne Brudvik presents" → the painted image fades in with a smooth, slow zoom,
///   mist and light rays → the logo is revealed large in the middle of the screen (glow and light sweep) → when
///   the world has been generated, the image crossfades into a cinematic flyover of the live world
///   (Main drives the camera) → the logo slides up to the left and the menu appears below it.
///
/// Menu: Continue (if a save exists), New game, world/seed (random, default 4242),
/// Settings, Quit. "Developed by Kjell Arne Brudvik" at the bottom.
/// </summary>
public partial class MainMenu : CanvasLayer
{
    /// <summary>Called when the player starts: (seed, load save).</summary>
    public Action<int, bool> OnStart;
    public bool SkipIntro;
    /// <summary>Set by Main when the live world is ready to be shown behind the menu.</summary>
    public bool WorldReady;

    private Control _root;
    private ColorRect _cover, _bg, _mist, _black;
    private ShaderMaterial _bgMat, _mistMat, _logoMat;
    private TextureRect _logo, _leftShade;
    private Label _presents, _presentsName, _tagline, _credit, _version;
    private VBoxContainer _menu;
    private SettingsPanel _settings;
    private LineEdit _seedEdit;
    private Button _continue, _newGame, _random, _default, _settingsButton, _quit;
    private Label _worldLabel;
    private string _lang;
    private CpuParticles2D _snow;
    private float _t, _live, _sweepT = -1f, _nextSweep;
    private bool _menuShown, _leaving;
    private IntroMusic _music;
    private AudioStreamPlayer _wind;

    // Timeline (seconds)
    private const float PresentsIn = 0.8f, PresentsOut = 4.8f, BgIn = 4.2f, LogoIn = 6.4f, MenuIn = 10.0f;
    private const float LogoAspect = 2172f / 724f;

    public override void _Ready()
    {
        Layer = 50;
        ProcessMode = ProcessModeEnum.Always;
        Settings.Load();
        Loc.Apply(Settings.Language);
        _lang = Loc.Current;
        MenuSound.Attach(this);

        _root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        // Black cover behind the painted image; both fade out when the live world is shown
        _cover = Full(new ColorRect { Color = new Color(0.01f, 0.01f, 0.015f) });
        const string bgPath = "res://assets/logo/heimskringla-bg.png";
        _bgMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/menu_bg.gdshader") };
        if (ResourceLoader.Exists(bgPath)) _bgMat.SetShaderParameter("tex", GD.Load<Texture2D>(bgPath));
        _bg = Full(new ColorRect { Material = _bgMat });

        // Mist, light rays and vignette
        _mistMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/menu_mist.gdshader") };
        _mist = Full(new ColorRect { Material = _mistMat });

        // Slowly falling snow/ash
        _snow = new CpuParticles2D
        {
            Amount = 120, Lifetime = 14f, Preprocess = 14f,
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
            Direction = new Vector2(0.25f, 1f), Spread = 12f,
            Gravity = new Vector2(0, 4f), InitialVelocityMin = 18f, InitialVelocityMax = 40f,
            ScaleAmountMin = 1.2f, ScaleAmountMax = 3.0f,
            Color = new Color(1f, 0.97f, 0.92f, 0.5f),
        };
        _root.AddChild(_snow);

        // Dark gradient on the left behind the menu
        var lg = new GradientTexture2D
        {
            Width = 256, Height = 4, FillFrom = new Vector2(0, 0.5f), FillTo = new Vector2(1, 0.5f),
            Gradient = new Gradient { Offsets = new[] { 0f, 0.55f, 1f }, Colors = new[] { new Color(0, 0, 0, 0.7f), new Color(0, 0, 0, 0.3f), new Color(0, 0, 0, 0f) } },
        };
        _leftShade = new TextureRect { Texture = lg, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
        _leftShade.AnchorBottom = 1f;
        _leftShade.OffsetRight = 980;
        _root.AddChild(_leftShade);

        // Black fade (below the presents card, above the image)
        _black = Full(new ColorRect { Color = Colors.Black });

        // The "presents" card
        var pres = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        pres.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        pres.AddThemeConstantOverride("separation", 6);
        _root.AddChild(pres);
        _presentsName = MenuUi.Label("Kjell Arne Brudvik", 46, MenuUi.GoldBright, MenuUi.Title);
        _presents = MenuUi.Label(Loc.T("INTRO_PRESENTS"), 22, MenuUi.TextDim);
        _presentsName.Modulate = _presents.Modulate = new Color(1, 1, 1, 0);
        pres.AddChild(_presentsName);
        pres.AddChild(_presents);

        // Logo (positioned manually: centered during the reveal, then up to the left)
        const string logoPath = "res://assets/logo/heimskringla-logo.png";
        _logoMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/menu_logo.gdshader") };
        if (ResourceLoader.Exists(logoPath)) _logoMat.SetShaderParameter("tex", GD.Load<Texture2D>(logoPath));
        _logo = new TextureRect
        {
            Texture = ResourceLoader.Exists(logoPath) ? GD.Load<Texture2D>(logoPath) : null,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Material = _logoMat,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _root.AddChild(_logo);
        _tagline = MenuUi.Label(Loc.T("MENU_TAGLINE"), 24, MenuUi.Gold);
        _tagline.Modulate = new Color(1, 1, 1, 0);
        _root.AddChild(_tagline);

        // The menu (left column)
        _menu = new VBoxContainer { Modulate = new Color(1, 1, 1, 0), Visible = false };
        _menu.AddThemeConstantOverride("separation", 2);
        _root.AddChild(_menu);
        BuildMenu();
        _settings = new SettingsPanel { Visible = false, OnBack = ShowMainButtons };
        _root.AddChild(_settings);

        _credit = MenuUi.Label(Loc.T("MENU_CREDIT"), 18, MenuUi.TextDim);
        _credit.HorizontalAlignment = HorizontalAlignment.Left;
        _credit.Modulate = new Color(1, 1, 1, 0);
        _root.AddChild(_credit);
        _version = MenuUi.Label(Loc.T("MENU_VERSION"), 13, new Color(MenuUi.TextDim, 0.6f));
        _version.Modulate = new Color(1, 1, 1, 0);
        _root.AddChild(_version);

        // Audio: wind + pad
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
        _nextSweep = LogoIn + 1.2f;
    }

    private ColorRect Full(ColorRect r)
    {
        r.MouseFilter = Control.MouseFilterEnum.Ignore;
        r.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(r);
        return r;
    }

    private void BuildMenu()
    {
        _continue = MenuUi.Button(Loc.T("MENU_CONTINUE"), 32, () => Start(true), left: true);
        _menu.AddChild(_continue);
        _newGame = MenuUi.Button(Loc.T("MENU_NEW_GAME"), 32, () => Start(false), left: true);
        _menu.AddChild(_newGame);

        // World/seed
        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 10);
        seedRow.AddChild(new Control { CustomMinimumSize = new Vector2(22, 0) });
        _worldLabel = MenuUi.Label(Loc.T("MENU_WORLD"), 20, MenuUi.Gold);
        seedRow.AddChild(_worldLabel);
        _seedEdit = new LineEdit { Text = Settings.LastSeed.ToString(), CustomMinimumSize = new Vector2(130, 0), Alignment = HorizontalAlignment.Center, MaxLength = 9 };
        MenuUi.StyleInput(_seedEdit);
        _seedEdit.TextChanged += txt =>
        {
            string digits = "";
            foreach (char c in txt) if (char.IsDigit(c)) digits += c;
            if (digits != txt) { _seedEdit.Text = digits; _seedEdit.CaretColumn = digits.Length; }
            UpdateContinue();
        };
        seedRow.AddChild(_seedEdit);
        var dice = _random = MenuUi.Button(Loc.T("MENU_RANDOM"), 18, () => { _seedEdit.Text = ((int)(GD.Randi() % 1000000)).ToString(); UpdateContinue(); });
        dice.CustomMinimumSize = new Vector2(110, 32);
        seedRow.AddChild(dice);
        var std = _default = MenuUi.Button(Loc.T("MENU_DEFAULT"), 18, () => { _seedEdit.Text = Settings.DefaultSeed.ToString(); UpdateContinue(); });
        std.CustomMinimumSize = new Vector2(110, 32);
        seedRow.AddChild(std);
        _menu.AddChild(seedRow);
        _menu.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });

        _settingsButton = MenuUi.Button(Loc.T("MENU_SETTINGS"), 32, ShowSettings, left: true);
        _menu.AddChild(_settingsButton);
        _quit = MenuUi.Button(Loc.T("MENU_QUIT"), 32, () => GetTree().Quit(), left: true);
        _menu.AddChild(_quit);
        UpdateContinue();
    }

    public override void _EnterTree() => Settings.Changed += OnSettingsChanged;
    public override void _ExitTree() => Settings.Changed -= OnSettingsChanged;

    /// <summary>The language changed in the settings: update the menu texts.</summary>
    private void OnSettingsChanged()
    {
        if (_menu == null || _lang == Loc.Current) return;
        _lang = Loc.Current;
        _presents.Text = Loc.T("INTRO_PRESENTS");
        _tagline.Text = Loc.T("MENU_TAGLINE");
        _credit.Text = Loc.T("MENU_CREDIT");
        _version.Text = Loc.T("MENU_VERSION");
        _continue.Text = Loc.T("MENU_CONTINUE");
        _newGame.Text = Loc.T("MENU_NEW_GAME");
        _worldLabel.Text = Loc.T("MENU_WORLD");
        _random.Text = Loc.T("MENU_RANDOM");
        _default.Text = Loc.T("MENU_DEFAULT");
        _settingsButton.Text = Loc.T("MENU_SETTINGS");
        _quit.Text = Loc.T("MENU_QUIT");
    }

    private int CurrentSeed => int.TryParse(_seedEdit.Text, out int s) ? s : Settings.DefaultSeed;

    private void UpdateContinue() => _continue.Visible = WorldState.Exists(CurrentSeed);

    private void ShowSettings() { _menu.Visible = false; _settings.Visible = true; }
    private void ShowMainButtons() { _settings.Visible = false; _menu.Visible = true; }

    private void Start(bool load)
    {
        if (_leaving) return;
        _leaving = true;
        Settings.LastSeed = CurrentSeed;
        Settings.Save();
        var tw = CreateTween();
        tw.TweenProperty(_black, "color:a", 1f, 1.2f);
        tw.TweenCallback(Callable.From(() =>
        {
            OnStart?.Invoke(CurrentSeed, load);
            QueueFree();
        }));
    }

    public override void _Input(InputEvent e)
    {
        if (!_menuShown && _t < MenuIn && (e is InputEventKey { Pressed: true } || e is InputEventMouseButton { Pressed: true }))
        {
            _t = MenuIn;
            GetViewport().SetInputAsHandled();
        }
    }

    private static float Ease(float x) => x * x * (3f - 2f * x);
    private static float EaseOut(float x) => 1f - (1f - x) * (1f - x) * (1f - x);

    public override void _Process(double delta)
    {
        float dt = Mathf.Min((float)delta, 0.05f);
        _t += dt;
        float A(float start, float dur) => Mathf.Clamp((_t - start) / dur, 0f, 1f);
        var vs = _root.Size;

        // Presents
        float pin = Ease(A(PresentsIn, 1.4f)) * (1f - Ease(A(PresentsOut - 1.2f, 1.2f)));
        if (_t >= MenuIn) pin = 0f;
        _presentsName.Modulate = new Color(1, 1, 1, pin);
        _presents.Modulate = new Color(1, 1, 1, pin * Ease(A(PresentsIn + 0.6f, 1.2f)));

        // Black → image → live world
        if (!_leaving) _black.Color = new Color(0, 0, 0, 1f - Ease(A(BgIn, 3.4f)));
        if (WorldReady && _t > LogoIn + 1.5f) _live = Mathf.Min(1f, _live + dt / 4f);
        float live = Ease(_live);
        _cover.Color = new Color(_cover.Color, 1f - live);
        _bgMat.SetShaderParameter("alpha", 1f - live);
        // Smooth, slow zoom (in the shader, with mipmaps: no stutter or shimmer)
        float z = 1.12f - 0.1f * Ease(Mathf.Clamp((_t - BgIn) / 70f, 0f, 1f));
        _bgMat.SetShaderParameter("zoom", z);
        _mistMat.SetShaderParameter("strength", Mathf.Lerp(1f, 0.32f, live));
        _snow.Modulate = new Color(1, 1, 1, 1f - live * 0.6f);
        _snow.Position = new Vector2(vs.X * 0.5f, -20f);
        _snow.EmissionRectExtents = new Vector2(vs.X * 0.6f, 10f);

        // Logo: revealed large in the center, slides up to the left when the menu appears
        float reveal = EaseOut(A(LogoIn, 3.0f));
        _logoMat.SetShaderParameter("reveal", reveal);
        float g = Ease(A(MenuIn - 0.6f, 2.0f));
        float wBig = Mathf.Min(vs.X * 0.62f, 1150f), wSmall = Mathf.Min(vs.X * 0.34f, 600f);
        float w = Mathf.Lerp(wBig, wSmall, g) * (0.96f + 0.04f * reveal);
        float h = w / LogoAspect;
        var bigPos = new Vector2((vs.X - w) * 0.5f, vs.Y * 0.36f - h * 0.5f);
        var smallPos = new Vector2(70f, vs.Y * 0.08f);
        var lp = bigPos.Lerp(smallPos, g);
        _logo.Position = lp;
        _logo.Size = new Vector2(w, h);
        // Light sweep: at the reveal and then every ~14 seconds
        if (_t >= _nextSweep && _sweepT < 0f) { _sweepT = 0f; _nextSweep = _t + 14f; }
        if (_sweepT >= 0f)
        {
            _sweepT += dt;
            _logoMat.SetShaderParameter("sweep", -0.45f + _sweepT * 0.75f);
            if (_sweepT > 2.6f) { _sweepT = -1f; _logoMat.SetShaderParameter("sweep", -1f); }
        }
        _tagline.Modulate = new Color(1, 1, 1, Ease(A(LogoIn + 1.6f, 1.8f)) * 0.9f);
        _tagline.Size = new Vector2(w, 30);
        _tagline.Position = new Vector2(lp.X, lp.Y + h - 6f);

        // Menu, gradient and credits
        float m = Ease(A(MenuIn + 0.4f, 1.4f));
        _leftShade.Modulate = new Color(1, 1, 1, Ease(A(MenuIn - 0.4f, 2f)));
        var menuPos = new Vector2(78f, smallPos.Y + wSmall / LogoAspect + 64f);
        _menu.Position = menuPos;
        _settings.Position = menuPos;
        _credit.Modulate = new Color(1, 1, 1, Ease(A(MenuIn, 1.6f)) * 0.85f);
        _credit.Position = new Vector2(100f, vs.Y - 52f);
        _credit.Size = new Vector2(600, 30);
        _version.Modulate = _credit.Modulate;
        _version.Position = new Vector2(vs.X - 240f, vs.Y - 34f);
        _version.Size = new Vector2(220, 20);
        if (_t >= MenuIn && !_menuShown)
        {
            _menuShown = true;
            _menu.Visible = true;
            if (_continue.Visible) _continue.CallDeferred(Control.MethodName.GrabFocus);
        }
        if (_menuShown) _menu.Modulate = new Color(1, 1, 1, m);

        // Audio
        float master = _leaving ? Mathf.Max(0f, 1f - _black.Color.A) : 1f;
        float mv = Settings.MusicVolume * master;
        if (_wind != null) _wind.VolumeDb = Mathf.LinearToDb(Mathf.Max(0.0001f, Ease(A(0.2f, 3f)) * 0.45f * mv * (1f - live * 0.4f)));
        _music.Level = Ease(A(2.5f, 6f)) * mv;

        // Test switch: HK_MENUSHOT=<dir> takes screenshots of the intro and menu, then quits
        var shotDir = OS.GetEnvironment("HK_MENUSHOT");
        if (!string.IsNullOrEmpty(shotDir))
        {
            foreach (var (at, name) in new[] { (2.6f, "menu_1_presents"), (8.0f, "menu_2_logo"), (13.5f, "menu_3_menu"), (24f, "menu_4_live") })
                if (_t >= at && _t - dt < at)
                    GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(shotDir, name + ".png"));
            if (_t > 25f && !_leaving) ShowSettings();
            if (_t > 26f) { GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(shotDir, "menu_5_settings.png")); GetTree().Quit(); }
        }
    }
}
