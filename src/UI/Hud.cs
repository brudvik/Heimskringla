using System;
using System.Linq;
using Godot;
using Heimskringla.Core;
using Heimskringla.Vegetation;

namespace Heimskringla.UI;

public partial class Hud : CanvasLayer
{
    public static Hud Instance;

    private Label _prompt, _inventory, _hotbar, _debug, _loading, _help;
    private VBoxContainer _toasts;
    private Control _crosshair;
    public MapView Map { get; private set; }
    public bool MapOpen => Map.Visible;
    public bool DebugVisible => _debug.Visible;

    public override void _Ready()
    {
        Instance = this;
        Layer = 10;

        _crosshair = new ColorRect { Color = new Color(1, 1, 1, 0.85f), Size = new Vector2(4, 4), MouseFilter = Control.MouseFilterEnum.Ignore };
        _crosshair.SetAnchorsPreset(Control.LayoutPreset.Center);
        _crosshair.Position -= new Vector2(2, 2);
        AddChild(_crosshair);

        _prompt = MakeLabel(20, HorizontalAlignment.Center);
        _prompt.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _prompt.OffsetLeft = -400; _prompt.OffsetRight = 400; _prompt.OffsetTop = -190; _prompt.OffsetBottom = -160;
        AddChild(_prompt);

        _hotbar = MakeLabel(18, HorizontalAlignment.Center);
        _hotbar.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _hotbar.OffsetLeft = -400; _hotbar.OffsetRight = 400; _hotbar.OffsetTop = -60; _hotbar.OffsetBottom = -30;
        AddChild(_hotbar);
        RefreshHotbar(ToolKind.Axe);

        _inventory = MakeLabel(18, HorizontalAlignment.Left);
        _inventory.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _inventory.OffsetLeft = 20; _inventory.OffsetTop = -150; _inventory.OffsetRight = 300; _inventory.OffsetBottom = -20;
        _inventory.VerticalAlignment = VerticalAlignment.Bottom;
        AddChild(_inventory);
        Inventory.Changed += RefreshInventory;
        RefreshInventory();

        _toasts = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _toasts.SetAnchorsPreset(Control.LayoutPreset.CenterLeft);
        _toasts.OffsetLeft = 20; _toasts.OffsetRight = 520; _toasts.OffsetTop = -40; _toasts.OffsetBottom = 200;
        AddChild(_toasts);

        _debug = MakeLabel(14, HorizontalAlignment.Left);
        _debug.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _debug.OffsetLeft = -380; _debug.OffsetRight = -12; _debug.OffsetTop = 12; _debug.OffsetBottom = 500;
        AddChild(_debug);

        _help = MakeLabel(15, HorizontalAlignment.Left);
        _help.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _help.OffsetLeft = 16; _help.OffsetTop = 12; _help.OffsetRight = 520; _help.OffsetBottom = 400;
        _help.Text =
            "HEIMSKRINGLA - spill-test   (F1 skjul)\n" +
            "WASD gå · Shift løp · Space hopp · Mus se · Hjul zoom\n" +
            "Venstre mus: hugg/hakk · E: plukk opp\n" +
            "1 Øks · 2 Hakke · 3 Hender\n" +
            "M kart (klikk = teleport) · F flymodus (Ctrl ned)\n" +
            "T (hold) spol tid · F3 debug · F5 lagre · F9 last\n" +
            "F10 ny verden (tilfeldig seed) · Esc slipp mus";
        AddChild(_help);

        _loading = MakeLabel(28, HorizontalAlignment.Center);
        _loading.SetAnchorsPreset(Control.LayoutPreset.Center);
        _loading.OffsetLeft = -500; _loading.OffsetRight = 500; _loading.OffsetTop = -40; _loading.OffsetBottom = 40;
        AddChild(_loading);

        Map = new MapView { Visible = false };
        Map.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(Map);
    }

    public override void _ExitTree()
    {
        Inventory.Changed -= RefreshInventory;
        if (Instance == this) Instance = null;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        switch (k.PhysicalKeycode)
        {
            case Key.F1: _help.Visible = !_help.Visible; break;
            case Key.F3: _debug.Visible = !_debug.Visible; break;
            case Key.M: ToggleMap(); break;
        }
    }

    public void ToggleMap()
    {
        if (Map.Texture == null) return;
        Map.Visible = !Map.Visible;
        Input.MouseMode = Map.Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
    }

    private static Label MakeLabel(int size, HorizontalAlignment align)
    {
        var l = new Label { HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", new Color(1f, 0.97f, 0.9f));
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        l.AddThemeConstantOverride("outline_size", 5);
        return l;
    }

    public void SetPrompt(string text) => _prompt.Text = text ?? "";

    public void SetLoading(string text)
    {
        _loading.Text = text ?? "";
        _loading.Visible = text != null;
        _crosshair.Visible = text == null;
    }

    public void SetDebug(string text) => _debug.Text = text;

    public void RefreshHotbar(ToolKind t)
    {
        string Slot(int n, string name, ToolKind k) => t == k ? $"[ {n} {name} ]" : $"  {n} {name}  ";
        _hotbar.Text = Slot(1, "Øks", ToolKind.Axe) + "   " + Slot(2, "Hakke", ToolKind.Pickaxe) + "   " + Slot(3, "Hender", ToolKind.Hands);
    }

    private void RefreshInventory()
    {
        if (_inventory == null) return;
        var items = Inventory.Items.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key).ToList();
        _inventory.Text = items.Count == 0
            ? "Inventar: tomt"
            : "Inventar\n" + string.Join("\n", items.Select(kv => $"  {kv.Key}: {kv.Value}"));
    }

    public void Toast(string text)
    {
        var l = MakeLabel(18, HorizontalAlignment.Left);
        l.Text = text;
        _toasts.AddChild(l);
        while (_toasts.GetChildCount() > 6) _toasts.GetChild(0).Free();
        var tw = l.CreateTween();
        tw.TweenInterval(2.2);
        tw.TweenProperty(l, "modulate:a", 0f, 0.6);
        tw.TweenCallback(Callable.From(l.QueueFree));
    }
}

/// <summary>Verdenskart generert fra grovkartet. Klikk for å teleportere.</summary>
public partial class MapView : Control
{
    public Texture2D Texture;
    public Func<Vector3> PlayerPosition;
    public Func<float> PlayerYaw;
    public Action<float, float> OnTeleport;
    public int Seed;

    private Rect2 MapRect
    {
        get
        {
            float s = Mathf.Min(Size.X, Size.Y) * 0.9f;
            return new Rect2((Size - new Vector2(s, s)) * 0.5f, new Vector2(s, s));
        }
    }

    public override void _Process(double delta)
    {
        if (Visible) QueueRedraw();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.55f));
        if (Texture == null) return;
        var r = MapRect;
        DrawTextureRect(Texture, r, false);
        DrawRect(r, new Color(0.1f, 0.08f, 0.05f), false, 3f);
        var font = ThemeDB.FallbackFont;
        DrawString(font, r.Position + new Vector2(0, -10), $"Verdenskart · seed {Seed} · 10,24 x 10,24 km · klikk for å teleportere · M lukker",
            HorizontalAlignment.Left, -1, 18, new Color(1, 0.97f, 0.9f));
        DrawString(font, r.Position + new Vector2(r.Size.X * 0.5f - 6, 22), "N", HorizontalAlignment.Left, -1, 22, Colors.White);

        if (PlayerPosition == null) return;
        var p = PlayerPosition();
        var mp = WorldToMap(p.X, p.Z, r);
        float yaw = PlayerYaw?.Invoke() ?? 0f;
        var fwd = new Vector2(-Mathf.Sin(yaw), -Mathf.Cos(yaw));
        var side = new Vector2(-fwd.Y, fwd.X);
        var pts = new[] { mp + fwd * 12f, mp - fwd * 7f + side * 7f, mp - fwd * 7f - side * 7f };
        DrawColoredPolygon(pts, new Color(0.9f, 0.1f, 0.1f));
        DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[0] }, Colors.White, 1.5f);
    }

    private static Vector2 WorldToMap(float x, float z, Rect2 r)
    {
        float u = (x + World.WorldSettings.HalfSize) / World.WorldSettings.WorldSize;
        float v = (z + World.WorldSettings.HalfSize) / World.WorldSettings.WorldSize;
        return r.Position + new Vector2(u, v) * r.Size;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
        {
            var r = MapRect;
            if (!r.HasPoint(mb.Position)) return;
            var uv = (mb.Position - r.Position) / r.Size;
            float x = -World.WorldSettings.HalfSize + uv.X * World.WorldSettings.WorldSize;
            float z = -World.WorldSettings.HalfSize + uv.Y * World.WorldSettings.WorldSize;
            OnTeleport?.Invoke(x, z);
            Hud.Instance?.ToggleMap();
            AcceptEvent();
        }
    }
}
