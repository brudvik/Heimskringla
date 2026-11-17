using System.Collections.Generic;
using System.Linq;
using Godot;
using Heimskringla.Core;
using Heimskringla.Vegetation;

namespace Heimskringla.UI;

/// <summary>
/// The game HUD: compass, hotbar with icons, stamina, inventory, target health,
/// pickup messages, system messages, help, debug, loading screen and map.
/// </summary>
public partial class Hud : CanvasLayer
{
    public static Hud Instance;

    private static readonly Color Text = new(1f, 0.96f, 0.88f);
    private static readonly Color Gold = new(0.86f, 0.70f, 0.40f);
    private static readonly Color PanelBg = new(0.07f, 0.06f, 0.05f, 0.62f);

    private Label _prompt, _debug, _loading, _help, _targetName;
    private ColorRect _loadingShade;
    private TextureRect _loadingBg;
    private TextureRect _logo;
    private VBoxContainer _toasts, _pickups;
    private Control _crosshair, _targetBox;
    private ProgressBar _stamina, _targetHp;
    private Compass _compass;
    private HBoxContainer _hotbar;
    private GridContainer _inventory;
    private PanelContainer _invPanel;
    private readonly List<PanelContainer> _slots = new();
    private ToolKind _tool = ToolKind.Axe;
    private IconRenderer _icons;
    private float _staminaShowTimer;

    public MapView Map { get; private set; }
    public bool MapOpen => Map.Visible;
    public bool DebugVisible => _debug.Visible;

    /// <summary>The mesh used as the icon for each resource/tool.</summary>
    private static Mesh IconMesh(string key) => key switch
    {
        "Tømmer" => PropLibrary.Defs?[(int)PropKind.Log].Variants[0].Mesh,
        "Grein" => PropLibrary.Defs?[(int)PropKind.Branch].Variants[0].Mesh,
        "Stein" => PropLibrary.Defs?[(int)PropKind.Stone].Variants[0].Mesh,
        "Sopp" => PropLibrary.Defs?[(int)PropKind.Mushroom].Variants[0].Mesh,
        "Øks" => Gameplay.CharacterModel.ToolMesh(ModelLibrary.LoadMesh("res://assets/survival/tool-axe.glb")),
        "Hakke" => Gameplay.CharacterModel.ToolMesh(ModelLibrary.LoadMesh("res://assets/survival/tool-pickaxe.glb")),
        _ => null,
    };

    private static Vector3 IconRotation(string key) => key switch
    {
        "Tømmer" => new Vector3(0, 35, 0),
        "Grein" => new Vector3(0, 30, 0),
        "Øks" or "Hakke" => new Vector3(0, 0, -35),
        _ => new Vector3(15, 30, 0),
    };

    public override void _Ready()
    {
        Instance = this;
        Layer = 10;
        _icons = new IconRenderer();
        AddChild(_icons);
        _icons.IconReady += _ => { RefreshInventory(); RefreshHotbar(_tool); };

        // Crosshair
        _crosshair = new ColorRect { Color = new Color(1, 1, 1, 0.8f), Size = new Vector2(4, 4), MouseFilter = Control.MouseFilterEnum.Ignore };
        _crosshair.SetAnchorsPreset(Control.LayoutPreset.Center);
        _crosshair.Position -= new Vector2(2, 2);
        AddChild(_crosshair);

        // Compass at the top
        _compass = new Compass { CustomMinimumSize = new Vector2(520, 34), MouseFilter = Control.MouseFilterEnum.Ignore };
        _compass.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _compass.OffsetLeft = -260; _compass.OffsetRight = 260; _compass.OffsetTop = 10; _compass.OffsetBottom = 44;
        AddChild(_compass);

        // Target: name + health below the crosshair
        var tb = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        tb.SetAnchorsPreset(Control.LayoutPreset.Center);
        tb.OffsetLeft = -110; tb.OffsetRight = 110; tb.OffsetTop = 26; tb.OffsetBottom = 70;
        _targetName = MakeLabel(15, HorizontalAlignment.Center);
        tb.AddChild(_targetName);
        _targetHp = MakeBar(new Color(0.85f, 0.55f, 0.25f), 8);
        tb.AddChild(_targetHp);
        _targetBox = tb;
        _targetBox.Visible = false;
        AddChild(tb);

        // Action hint
        _prompt = MakeLabel(19, HorizontalAlignment.Center);
        _prompt.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _prompt.OffsetLeft = -400; _prompt.OffsetRight = 400; _prompt.OffsetTop = -178; _prompt.OffsetBottom = -150;
        AddChild(_prompt);

        // Stamina + hotbar at the bottom
        var bottom = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
        bottom.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        bottom.OffsetLeft = -150; bottom.OffsetRight = 150; bottom.OffsetTop = -128; bottom.OffsetBottom = -14;
        bottom.AddThemeConstantOverride("separation", 6);
        _stamina = MakeBar(new Color(0.95f, 0.82f, 0.35f), 7);
        bottom.AddChild(_stamina);
        _hotbar = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        _hotbar.AddThemeConstantOverride("separation", 8);
        bottom.AddChild(_hotbar);
        AddChild(bottom);
        for (int i = 0; i < 3; i++)
        {
            var slot = MakePanel();
            slot.CustomMinimumSize = new Vector2(76, 76);
            _hotbar.AddChild(slot);
            _slots.Add(slot);
        }

        // Inventory in the bottom left
        _invPanel = MakePanel();
        _invPanel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _invPanel.OffsetLeft = 16; _invPanel.OffsetBottom = -16; _invPanel.OffsetTop = -16; _invPanel.OffsetRight = 16;
        _invPanel.GrowVertical = Control.GrowDirection.Begin;
        var invBox = new VBoxContainer();
        var invTitle = MakeLabel(14, HorizontalAlignment.Left);
        invTitle.Text = "INVENTAR";
        invTitle.AddThemeColorOverride("font_color", Gold);
        invBox.AddChild(invTitle);
        _inventory = new GridContainer { Columns = 4 };
        _inventory.AddThemeConstantOverride("h_separation", 6);
        _inventory.AddThemeConstantOverride("v_separation", 6);
        invBox.AddChild(_inventory);
        _invPanel.AddChild(invBox);
        AddChild(_invPanel);

        // Pickup messages (right) and system messages (left)
        _pickups = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
        _pickups.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
        _pickups.OffsetLeft = -260; _pickups.OffsetRight = -20; _pickups.OffsetTop = -40; _pickups.OffsetBottom = 220;
        AddChild(_pickups);
        _toasts = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _toasts.SetAnchorsPreset(Control.LayoutPreset.CenterLeft);
        _toasts.OffsetLeft = 20; _toasts.OffsetRight = 520; _toasts.OffsetTop = -60; _toasts.OffsetBottom = 160;
        AddChild(_toasts);

        _debug = MakeLabel(13, HorizontalAlignment.Left);
        _debug.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _debug.OffsetLeft = -390; _debug.OffsetRight = -12; _debug.OffsetTop = 56; _debug.OffsetBottom = 560;
        AddChild(_debug);

        var helpPanel = MakePanel();
        helpPanel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        helpPanel.OffsetLeft = 14; helpPanel.OffsetTop = 12;
        _help = MakeLabel(14, HorizontalAlignment.Left);
        _help.Text =
            "HEIMSKRINGLA  ·  spill-test        F1 skjul\n" +
            "WASD gå · Shift løp · Alt rolig · Space hopp\n" +
            "Mus se · Hjul zoom · Venstre mus: hugg/hakk\n" +
            "E plukk opp · 1 Øks · 2 Hakke · 3 Hender\n" +
            "B bygg bål (3 greiner + 5 stein)\n" +
            "M kart · F fly (Ctrl ned) · T spol tid\n" +
            "F3 debug · F5 lagre · F9 last · F10 ny verden\n" +
            "F6 skift vær · F7 neste årstid";
        helpPanel.AddChild(_help);
        AddChild(helpPanel);
        _help = MakeHelpToggle(helpPanel);

        // Loading screen: background image, dark tint (mostly behind the text) and the logo above the status text
        const string bgPath = "res://assets/logo/heimskringla-bg.png";
        if (ResourceLoader.Exists(bgPath))
        {
            _loadingBg = new TextureRect
            {
                Texture = GD.Load<Texture2D>(bgPath),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _loadingBg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_loadingBg);
        }
        _loadingShade = new ColorRect { Color = new Color(0.02f, 0.02f, 0.03f, _loadingBg != null ? 0.3f : 0.55f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _loadingShade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_loadingShade);
        const string logoPath = "res://assets/logo/heimskringla-logo.png";
        if (ResourceLoader.Exists(logoPath))
        {
            _logo = new TextureRect
            {
                Texture = GD.Load<Texture2D>(logoPath),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _logo.SetAnchorsPreset(Control.LayoutPreset.Center);
            _logo.OffsetLeft = -480; _logo.OffsetRight = 480; _logo.OffsetTop = -390; _logo.OffsetBottom = -70;
            AddChild(_logo);
        }

        _loading = MakeLabel(28, HorizontalAlignment.Center);
        _loading.SetAnchorsPreset(Control.LayoutPreset.Center);
        _loading.OffsetLeft = -500; _loading.OffsetRight = 500; _loading.OffsetTop = -40; _loading.OffsetBottom = 40;
        AddChild(_loading);

        Map = new MapView { Visible = false };
        Map.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(Map);

        Inventory.Changed += RefreshInventory;
        Inventory.ItemAdded += OnItemAdded;
        RefreshInventory();
        RefreshHotbar(ToolKind.Axe);
    }

    private Control _helpPanel;
    private Label MakeHelpToggle(Control panel)
    {
        _helpPanel = panel;
        return (Label)panel.GetChild(0);
    }

    /// <summary>Called when PropLibrary is ready, so the icons can be rendered.</summary>
    public void RequestIcons()
    {
        foreach (var key in new[] { "Tømmer", "Grein", "Stein", "Sopp", "Øks", "Hakke" })
            _icons.Request(key, IconMesh(key), IconRotation(key));
    }

    public override void _ExitTree()
    {
        Inventory.Changed -= RefreshInventory;
        Inventory.ItemAdded -= OnItemAdded;
        if (Instance == this) Instance = null;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        switch (k.PhysicalKeycode)
        {
            case Key.F1: _helpPanel.Visible = !_helpPanel.Visible; break;
            case Key.F3: _debug.Visible = !_debug.Visible; break;
            case Key.M: ToggleMap(); break;
        }
    }

    public override void _Process(double delta)
    {
        // The stamina bar is hidden when full
        _staminaShowTimer -= (float)delta;
        float a = _staminaShowTimer > 0f ? 1f : 0f;
        _stamina.Modulate = new Color(1, 1, 1, Mathf.MoveToward(_stamina.Modulate.A, a, (float)delta * 3f));
    }

    public void ToggleMap()
    {
        if (Map.Texture == null) return;
        Map.Visible = !Map.Visible;
        if (!Core.Game.AutoTest) Input.MouseMode = Map.Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
    }

    // ------------------------------------------------------------ Building blocks

    private static Label MakeLabel(int size, HorizontalAlignment align)
    {
        var l = new Label { HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", Text);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        l.AddThemeConstantOverride("outline_size", 5);
        return l;
    }

    private static StyleBoxFlat Box(Color bg, Color border, int bw = 1, int radius = 6) => new()
    {
        BgColor = bg,
        BorderColor = border,
        BorderWidthLeft = bw, BorderWidthRight = bw, BorderWidthTop = bw, BorderWidthBottom = bw,
        CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
        ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6,
    };

    private static PanelContainer MakePanel(bool selected = false)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        p.AddThemeStyleboxOverride("panel", Box(PanelBg, selected ? Gold : new Color(Gold, 0.35f), selected ? 2 : 1));
        return p;
    }

    private static ProgressBar MakeBar(Color fill, int height)
    {
        var bar = new ProgressBar { ShowPercentage = false, MaxValue = 1, Value = 1, CustomMinimumSize = new Vector2(0, height), MouseFilter = Control.MouseFilterEnum.Ignore };
        var bg = Box(new Color(0, 0, 0, 0.55f), new Color(0, 0, 0, 0.8f), 1, 3);
        bg.ContentMarginTop = bg.ContentMarginBottom = bg.ContentMarginLeft = bg.ContentMarginRight = 0;
        var fg = Box(fill, fill, 0, 3);
        fg.ContentMarginTop = fg.ContentMarginBottom = fg.ContentMarginLeft = fg.ContentMarginRight = 0;
        bar.AddThemeStyleboxOverride("background", bg);
        bar.AddThemeStyleboxOverride("fill", fg);
        return bar;
    }

    private Control ItemCell(string item, int count, float iconSize)
    {
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 0);
        if (_icons.Icons.TryGetValue(item, out var tex))
            box.AddChild(new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(iconSize, iconSize), MouseFilter = Control.MouseFilterEnum.Ignore });
        else
        {
            var ph = MakeLabel(12, HorizontalAlignment.Center);
            ph.Text = item;
            ph.CustomMinimumSize = new Vector2(iconSize, iconSize);
            ph.VerticalAlignment = VerticalAlignment.Center;
            box.AddChild(ph);
        }
        var lbl = MakeLabel(13, HorizontalAlignment.Center);
        lbl.Text = count >= 0 ? $"{item} {count}" : item;
        box.AddChild(lbl);
        return box;
    }

    // ------------------------------------------------------------ Updating

    public void SetPrompt(string text) => _prompt.Text = text ?? "";

    public void SetLoading(string text)
    {
        _loading.Text = text ?? "";
        _loading.Visible = text != null;
        _loadingShade.Visible = text != null;
        if (_loadingBg != null) _loadingBg.Visible = text != null;
        if (_hotbar != null) _hotbar.Visible = text == null;
        if (_helpPanel != null) _helpPanel.Visible = text == null;
        if (_logo != null) _logo.Visible = text != null;
        _crosshair.Visible = text == null;
        _compass.Visible = text == null;
    }

    public void SetDebug(string text) => _debug.Text = text;

    public void SetStamina(float fraction, bool exhausted)
    {
        _stamina.Value = fraction;
        if (fraction < 0.995f) _staminaShowTimer = 1.5f;
        var fg = (StyleBoxFlat)_stamina.GetThemeStylebox("fill");
        fg.BgColor = exhausted ? new Color(0.85f, 0.3f, 0.2f) : new Color(0.95f, 0.82f, 0.35f);
    }

    public void SetHeading(float radians) => _compass.Heading = radians;

    public void SetTargetHealth(string name, float fraction)
    {
        bool show = name != null && fraction < 0.999f;
        _targetBox.Visible = show;
        if (!show) return;
        _targetName.Text = name;
        _targetHp.Value = fraction;
    }

    public void RefreshHotbar(ToolKind t)
    {
        _tool = t;
        if (_slots.Count == 0) return;
        string[] names = { "Øks", "Hakke", "Hender" };
        ToolKind[] kinds = { ToolKind.Axe, ToolKind.Pickaxe, ToolKind.Hands };
        for (int i = 0; i < 3; i++)
        {
            var slot = _slots[i];
            foreach (var c in slot.GetChildren()) c.QueueFree();
            bool sel = kinds[i] == t;
            slot.AddThemeStyleboxOverride("panel", Box(sel ? new Color(0.18f, 0.14f, 0.08f, 0.8f) : PanelBg, sel ? Gold : new Color(Gold, 0.3f), sel ? 2 : 1));
            var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            box.AddThemeConstantOverride("separation", -4);
            var num = MakeLabel(11, HorizontalAlignment.Left);
            num.Text = (i + 1).ToString();
            num.AddThemeColorOverride("font_color", Gold);
            box.AddChild(num);
            if (_icons.Icons.TryGetValue(names[i], out var tex))
                box.AddChild(new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(48, 44), MouseFilter = Control.MouseFilterEnum.Ignore });
            else
            {
                var l = MakeLabel(13, HorizontalAlignment.Center);
                l.Text = names[i];
                l.CustomMinimumSize = new Vector2(48, 44);
                l.VerticalAlignment = VerticalAlignment.Center;
                box.AddChild(l);
            }
            slot.AddChild(box);
        }
    }

    private void RefreshInventory()
    {
        if (_inventory == null) return;
        foreach (var c in _inventory.GetChildren()) c.QueueFree();
        var items = Inventory.Items.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key).ToList();
        _invPanel.Visible = items.Count > 0;
        foreach (var kv in items) _inventory.AddChild(ItemCell(kv.Key, kv.Value, 44));
    }

    private void OnItemAdded(string item, int count)
    {
        var row = MakePanel();
        var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
        if (_icons.Icons.TryGetValue(item, out var tex))
            h.AddChild(new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(30, 30), MouseFilter = Control.MouseFilterEnum.Ignore });
        var l = MakeLabel(16, HorizontalAlignment.Right);
        l.Text = $"+{count} {item}";
        l.VerticalAlignment = VerticalAlignment.Center;
        h.AddChild(l);
        row.AddChild(h);
        _pickups.AddChild(row);
        while (_pickups.GetChildCount() > 6) _pickups.GetChild(0).Free();
        var tw = row.CreateTween();
        tw.TweenInterval(2.0);
        tw.TweenProperty(row, "modulate:a", 0f, 0.6);
        tw.TweenCallback(Callable.From(row.QueueFree));
    }

    public void Toast(string text)
    {
        var l = MakeLabel(17, HorizontalAlignment.Left);
        l.Text = text;
        _toasts.AddChild(l);
        while (_toasts.GetChildCount() > 5) _toasts.GetChild(0).Free();
        var tw = l.CreateTween();
        tw.TweenInterval(2.4);
        tw.TweenProperty(l, "modulate:a", 0f, 0.6);
        tw.TweenCallback(Callable.From(l.QueueFree));
    }
}

/// <summary>Compass strip: shows cardinal directions around the camera heading.</summary>
public partial class Compass : Control
{
    public float Heading;   // radians, 0 = north, clockwise

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var size = Size;
        var font = ThemeDB.FallbackFont;
        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.06f, 0.05f, 0.5f),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        };
        DrawStyleBox(bg, new Rect2(Vector2.Zero, size));
        float headingDeg = Mathf.RadToDeg(Heading);
        const float span = 150f;   // degrees visible
        string[] names = { "N", "NØ", "Ø", "SØ", "S", "SV", "V", "NV" };
        for (int d = -180; d <= 540; d += 15)
        {
            float rel = Mathf.Wrap(d - headingDeg, -180f, 180f);
            if (Mathf.Abs(rel) > span * 0.5f) continue;
            float x = size.X * 0.5f + rel / span * size.X;
            float fade = 1f - Mathf.Abs(rel) / (span * 0.5f);
            int dd = ((d % 360) + 360) % 360;
            if (dd % 45 == 0)
            {
                string s = names[dd / 45];
                var col = dd == 0 ? new Color(1f, 0.45f, 0.35f, fade) : new Color(1f, 0.96f, 0.88f, fade);
                var w = font.GetStringSize(s, HorizontalAlignment.Left, -1, 16).X;
                DrawString(font, new Vector2(x - w * 0.5f, size.Y * 0.68f), s, HorizontalAlignment.Left, -1, 16, col);
            }
            else
                DrawLine(new Vector2(x, size.Y * 0.28f), new Vector2(x, size.Y * 0.5f), new Color(1, 1, 1, 0.5f * fade), 1.5f);
        }
        DrawLine(new Vector2(size.X * 0.5f, 0), new Vector2(size.X * 0.5f, 6), new Color(0.86f, 0.70f, 0.40f), 2f);
    }
}
