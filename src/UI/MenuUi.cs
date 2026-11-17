using System;
using Godot;
using Heimskringla.Core;

namespace Heimskringla.UI;

/// <summary>Shared menu style: Cinzel font, golden text, dark semi-transparent panels.</summary>
public static class MenuUi
{
    public static readonly Color Gold = new(0.93f, 0.8f, 0.52f);
    public static readonly Color GoldBright = new(1f, 0.92f, 0.7f);
    public static readonly Color TextDim = new(0.82f, 0.8f, 0.74f);
    private static FontVariation _title, _body;

    /// <summary>Cinzel (variable weight). weight 400–900.</summary>
    public static Font Cinzel(int weight = 500)
    {
        var baseFont = GD.Load<FontFile>("res://assets/fonts/Cinzel.ttf");
        var f = new FontVariation { BaseFont = baseFont };
        f.VariationOpentype = new Godot.Collections.Dictionary { { TextServerManager.GetPrimaryInterface().NameToTag("wght"), weight } };
        return f;
    }

    public static Font Title => _title ??= (FontVariation)Cinzel(700);
    public static Font Body => _body ??= (FontVariation)Cinzel(500);

    public static Label Label(string text, int size, Color? color = null, Font font = null)
    {
        var l = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        l.AddThemeFontOverride("font", font ?? Body);
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color ?? TextDim);
        l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.75f));
        l.AddThemeConstantOverride("shadow_offset_x", 0);
        l.AddThemeConstantOverride("shadow_offset_y", 2);
        l.AddThemeConstantOverride("shadow_outline_size", 6);
        return l;
    }

    /// <summary>Menu button: plain text that lights up in gold with a thin underline on mouse hover.</summary>
    public static Button Button(string text, int size, Action onPress, bool left = false)
    {
        var b = new Button { Text = text, Flat = true, FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(360, size + 22) };
        if (left) b.Alignment = HorizontalAlignment.Left;
        b.AddThemeFontOverride("font", Body);
        b.AddThemeFontSizeOverride("font_size", size);
        b.AddThemeColorOverride("font_color", TextDim);
        b.AddThemeColorOverride("font_hover_color", GoldBright);
        b.AddThemeColorOverride("font_focus_color", GoldBright);
        b.AddThemeColorOverride("font_pressed_color", Gold);
        b.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.7f));
        b.AddThemeConstantOverride("outline_size", 4);
        StyleBox empty = new StyleBoxEmpty();
        StyleBox hover = new StyleBoxLine { Color = new Color(Gold, 0.7f), Thickness = 1, GrowBegin = -110, GrowEnd = -110, Vertical = false };
        if (left)
        {
            // Left-aligned menu: golden bar on the left and a faint glow behind the text
            empty = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), ContentMarginLeft = 22, BorderWidthLeft = 3, BorderColor = new Color(Gold, 0f) };
            hover = new StyleBoxFlat { BgColor = new Color(Gold, 0.09f), ContentMarginLeft = 22, BorderWidthLeft = 3, BorderColor = new Color(Gold, 0.9f) };
        }
        b.AddThemeStyleboxOverride("normal", empty);
        b.AddThemeStyleboxOverride("hover", hover);
        b.AddThemeStyleboxOverride("focus", hover);
        b.AddThemeStyleboxOverride("pressed", hover);
        b.Pressed += () => { MenuSound.Click(); onPress(); };
        b.MouseEntered += () => { MenuSound.Hover(); b.GrabFocus(); };
        return b;
    }

    public static StyleBoxFlat PanelBox() => new()
    {
        BgColor = new Color(0.04f, 0.04f, 0.05f, 0.78f),
        BorderColor = new Color(Gold, 0.45f),
        BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
        CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        ContentMarginLeft = 36, ContentMarginRight = 36, ContentMarginTop = 28, ContentMarginBottom = 28,
        ShadowColor = new Color(0, 0, 0, 0.5f), ShadowSize = 18,
    };

    public static void StyleInput(Control c)
    {
        c.AddThemeFontOverride("font", Body);
        c.AddThemeFontSizeOverride("font_size", 20);
        c.AddThemeColorOverride("font_color", GoldBright);
        var box = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.45f), BorderColor = new Color(Gold, 0.5f),
            BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        c.AddThemeStyleboxOverride("normal", box);
        c.AddThemeStyleboxOverride("focus", box);
    }
}

/// <summary>Small, soft click/hover sounds for the menu (synthesized, no files).</summary>
public static class MenuSound
{
    private static AudioStreamPlayer _p;
    private static AudioStreamWav _hover, _click;

    public static void Attach(Node n)
    {
        _p = new AudioStreamPlayer { VolumeDb = -14f, ProcessMode = Node.ProcessModeEnum.Always };
        n.AddChild(_p);
        _hover ??= Tone(880f, 0.05f, 0.25f);
        _click ??= Tone(440f, 0.12f, 0.5f);
    }

    public static void Hover() => Play(_hover);
    public static void Click() => Play(_click);

    private static void Play(AudioStreamWav s)
    {
        if (_p == null || !GodotObject.IsInstanceValid(_p) || s == null) return;
        _p.Stream = s;
        _p.Play();
    }

    private static AudioStreamWav Tone(float f, float dur, float amp)
    {
        const int Rate = 22050;
        int n = (int)(dur * Rate);
        var data = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float e = Mathf.Exp(-t / (dur * 0.3f)) * Mathf.Min(1f, t * 400f);
            float v = (Mathf.Sin(Mathf.Tau * f * t) * 0.7f + Mathf.Sin(Mathf.Tau * f * 1.5f * t) * 0.3f) * e * amp;
            short s = (short)(Mathf.Clamp(v, -1f, 1f) * 32000);
            data[i * 2] = (byte)(s & 0xff);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Data = data };
    }
}

/// <summary>Settings (language, audio, controls, display, graphics). Changes are applied and saved immediately.</summary>
public partial class SettingsPanel : PanelContainer
{
    public Action OnBack;
    private VBoxContainer _content;
    private string _lang;

    public override void _EnterTree() => Settings.Changed += OnSettingsChanged;
    public override void _ExitTree() => Settings.Changed -= OnSettingsChanged;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        AddThemeStyleboxOverride("panel", MenuUi.PanelBox());
        CustomMinimumSize = new Vector2(620, 0);
        Build();
    }

    /// <summary>The language changed: rebuild the panel (deferred, since it happens inside a control's signal).</summary>
    private void OnSettingsChanged()
    {
        if (_content == null || _lang == Loc.Current) return;
        Callable.From(() => Build(focusLanguage: true)).CallDeferred();
    }

    private void Build(bool focusLanguage = false)
    {
        if (_content != null)
        {
            RemoveChild(_content);
            _content.QueueFree();
        }
        _lang = Loc.Current;
        var v = new VBoxContainer();
        _content = v;
        v.AddThemeConstantOverride("separation", 14);
        AddChild(v);
        v.AddChild(MenuUi.Label(Loc.T("SETTINGS_TITLE"), 34, MenuUi.Gold, MenuUi.Title));
        v.AddChild(new HSeparator());

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 28);
        grid.AddThemeConstantOverride("v_separation", 12);
        v.AddChild(grid);

        // Language: "System" + each supported language (names shown in their own language)
        Row(grid, Loc.T("SETTINGS_LANGUAGE"));
        var lang = new OptionButton();
        lang.AddItem(Loc.T("SETTINGS_LANGUAGE_SYSTEM"));
        int selected = 0;
        for (int i = 0; i < Loc.Languages.Length; i++)
        {
            lang.AddItem(Loc.Languages[i].Name);
            if (Loc.Languages[i].Code == Settings.Language) selected = i + 1;
        }
        lang.Selected = selected;
        MenuUi.StyleInput(lang);
        lang.ItemSelected += i => { Settings.Language = i <= 0 ? "" : Loc.Languages[(int)i - 1].Code; Changed(); };
        grid.AddChild(lang);

        Slider(grid, Loc.T("SETTINGS_MASTER_VOLUME"), 0f, 1f, Settings.MasterVolume, x => Settings.MasterVolume = x, x => $"{x * 100:0} %");
        Slider(grid, Loc.T("SETTINGS_MUSIC_VOLUME"), 0f, 1f, Settings.MusicVolume, x => Settings.MusicVolume = x, x => $"{x * 100:0} %");
        Slider(grid, Loc.T("SETTINGS_MOUSE_SENSITIVITY"), 0.3f, 2.5f, Settings.MouseSensitivity, x => Settings.MouseSensitivity = x, x => $"{x:0.0}×");
        Slider(grid, Loc.T("SETTINGS_FOV"), 60f, 95f, Settings.Fov, x => Settings.Fov = x, x => $"{x:0}°");

        Row(grid, Loc.T("SETTINGS_GRAPHICS"));
        var q = new OptionButton();
        q.AddItem(Loc.T("SETTINGS_QUALITY_LOW")); q.AddItem(Loc.T("SETTINGS_QUALITY_MEDIUM")); q.AddItem(Loc.T("SETTINGS_QUALITY_HIGH"));
        q.Selected = Settings.Quality;
        MenuUi.StyleInput(q);
        q.ItemSelected += i => { Settings.Quality = (int)i; Changed(); };
        grid.AddChild(q);

        Row(grid, Loc.T("SETTINGS_FULLSCREEN"));
        var fs = new CheckButton { ButtonPressed = Settings.Fullscreen };
        fs.Toggled += on => { Settings.Fullscreen = on; Changed(); };
        grid.AddChild(fs);

        Row(grid, Loc.T("SETTINGS_VSYNC"));
        var vs = new CheckButton { ButtonPressed = Settings.VSync };
        vs.Toggled += on => { Settings.VSync = on; Changed(); };
        grid.AddChild(vs);

        v.AddChild(new HSeparator());
        var back = MenuUi.Button(Loc.T("SETTINGS_BACK"), 24, () => OnBack?.Invoke());
        back.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        v.AddChild(back);

        if (focusLanguage && IsVisibleInTree()) lang.GrabFocus();
    }

    private static void Changed()
    {
        Settings.Apply();
        Settings.Save();
    }

    private static void Row(GridContainer g, string text)
    {
        var l = MenuUi.Label(text, 20);
        l.HorizontalAlignment = HorizontalAlignment.Left;
        g.AddChild(l);
    }

    private static void Slider(GridContainer g, string text, float min, float max, float value, Action<float> set, Func<float, string> fmt)
    {
        Row(g, text);
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 12);
        var s = new HSlider { MinValue = min, MaxValue = max, Step = (max - min) / 100f, Value = value, CustomMinimumSize = new Vector2(220, 24), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var val = MenuUi.Label(fmt(value), 18, MenuUi.Gold);
        val.CustomMinimumSize = new Vector2(70, 0);
        s.ValueChanged += x => { set((float)x); val.Text = fmt((float)x); Changed(); };
        h.AddChild(s);
        h.AddChild(val);
        g.AddChild(h);
    }
}
