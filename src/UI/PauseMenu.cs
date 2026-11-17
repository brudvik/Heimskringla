using System;
using Godot;
using Heimskringla.Core;

namespace Heimskringla.UI;

/// <summary>In-game Esc menu: Continue, Settings, Save, Main menu, Quit. Pauses the game.</summary>
public partial class PauseMenu : CanvasLayer
{
    public Action OnSave, OnMainMenu;
    private Control _root;
    private VBoxContainer _buttons;
    private SettingsPanel _settings;
    private Button _continue, _settingsButton, _save, _mainMenu, _quit;
    private string _lang;

    public bool IsOpen => _root.Visible;

    public override void _Ready()
    {
        Layer = 40;
        ProcessMode = ProcessModeEnum.Always;
        MenuSound.Attach(this);
        _root = new Control { Visible = false };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        var shade = new ColorRect { Color = new Color(0.01f, 0.01f, 0.02f, 0.62f) };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(shade);
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(center);
        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 4);
        center.AddChild(stack);
        _buttons = stack;
        stack.AddChild(MenuUi.Label("Heimskringla", 46, MenuUi.Gold, MenuUi.Title));
        stack.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });
        _lang = Loc.Current;
        stack.AddChild(_continue = MenuUi.Button(Loc.T("PAUSE_CONTINUE"), 28, Close));
        stack.AddChild(_settingsButton = MenuUi.Button(Loc.T("PAUSE_SETTINGS"), 28, () => { _buttons.Visible = false; _settings.Visible = true; }));
        stack.AddChild(_save = MenuUi.Button(Loc.T("PAUSE_SAVE"), 28, () => { OnSave?.Invoke(); Close(); }));
        stack.AddChild(_mainMenu = MenuUi.Button(Loc.T("PAUSE_MAIN_MENU"), 28, () => { Close(); OnMainMenu?.Invoke(); }));
        stack.AddChild(_quit = MenuUi.Button(Loc.T("PAUSE_QUIT"), 28, () => GetTree().Quit()));
        _settings = new SettingsPanel { Visible = false, OnBack = () => { _settings.Visible = false; _buttons.Visible = true; } };
        center.AddChild(_settings);
    }

    public override void _EnterTree() => Settings.Changed += OnSettingsChanged;
    public override void _ExitTree() => Settings.Changed -= OnSettingsChanged;

    /// <summary>The language changed in the settings: update the button texts.</summary>
    private void OnSettingsChanged()
    {
        if (_continue == null || _lang == Loc.Current) return;
        _lang = Loc.Current;
        _continue.Text = Loc.T("PAUSE_CONTINUE");
        _settingsButton.Text = Loc.T("PAUSE_SETTINGS");
        _save.Text = Loc.T("PAUSE_SAVE");
        _mainMenu.Text = Loc.T("PAUSE_MAIN_MENU");
        _quit.Text = Loc.T("PAUSE_QUIT");
    }

    public override void _Input(InputEvent e)
    {
        if (Game.AutoTest) return;
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Escape })
        {
            if (_settings.Visible) { _settings.Visible = false; _buttons.Visible = true; }
            else if (IsOpen) Close();
            else Open();
            GetViewport().SetInputAsHandled();
        }
    }

    public void Open()
    {
        _root.Visible = true;
        _buttons.Visible = true;
        _settings.Visible = false;
        GetTree().Paused = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public void Close()
    {
        _root.Visible = false;
        GetTree().Paused = false;
        if (!Game.AutoTest) Input.MouseMode = Input.MouseModeEnum.Captured;
    }
}
