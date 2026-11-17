using System;
using Godot;

namespace Heimskringla.Core;

/// <summary>
/// User settings (stored in user://settings.cfg). Apply() sets language/audio/window immediately;
/// graphics and camera are read by DayNight and Player via Changed.
/// </summary>
public static class Settings
{
    public const int DefaultSeed = 4242;   // the world we have tested and fine-tuned with

    public static float MasterVolume = 0.8f;
    public static float MusicVolume = 0.6f;
    public static float MouseSensitivity = 1f;
    public static float Fov = 70f;
    public static bool Fullscreen;
    public static bool VSync = true;
    /// <summary>0 = low (no volumetric fog/SSAO, shorter shadows), 1 = medium, 2 = high.</summary>
    public static int Quality = 2;
    public static int LastSeed = DefaultSeed;
    /// <summary>UI language: "" = follow the system language, otherwise a code from Loc.Languages.</summary>
    public static string Language = "";

    public static event Action Changed;

    private const string Path = "user://settings.cfg";
    private static bool _loaded;
    private static string _appliedLanguage;

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return;
        MasterVolume = (float)Get(cfg, "audio", "master", "lyd", "hoved", MasterVolume);
        MusicVolume = (float)Get(cfg, "audio", "music", "lyd", "musikk", MusicVolume);
        MouseSensitivity = (float)Get(cfg, "controls", "mouse", "kontroll", "mus", MouseSensitivity);
        Fov = (float)Get(cfg, "controls", "fov", "kontroll", "synsfelt", Fov);
        Fullscreen = (bool)Get(cfg, "display", "fullscreen", "skjerm", "fullskjerm", Fullscreen);
        VSync = (bool)Get(cfg, "display", "vsync", "skjerm", "vsync", VSync);
        Quality = (int)Get(cfg, "graphics", "quality", "grafikk", "kvalitet", Quality);
        LastSeed = (int)Get(cfg, "game", "seed", "spill", "seed", LastSeed);
        Language = (string)cfg.GetValue("game", "language", Language);
    }

    /// <summary>Reads a value, falling back to the old (Norwegian) section/key from earlier versions.</summary>
    private static Variant Get(ConfigFile cfg, string section, string key, string oldSection, string oldKey, Variant def)
    {
        if (cfg.HasSectionKey(section, key)) return cfg.GetValue(section, key, def);
        return cfg.GetValue(oldSection, oldKey, def);
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("audio", "master", MasterVolume);
        cfg.SetValue("audio", "music", MusicVolume);
        cfg.SetValue("controls", "mouse", MouseSensitivity);
        cfg.SetValue("controls", "fov", Fov);
        cfg.SetValue("display", "fullscreen", Fullscreen);
        cfg.SetValue("display", "vsync", VSync);
        cfg.SetValue("graphics", "quality", Quality);
        cfg.SetValue("game", "seed", LastSeed);
        cfg.SetValue("game", "language", Language);
        cfg.Save(Path);
    }

    /// <summary>Applies the settings (audio, window) and notifies the rest of the game.</summary>
    public static void Apply()
    {
        // Only switch locale when the language actually changed (switching makes menus/HUD rebuild their texts)
        if (Language != _appliedLanguage)
        {
            _appliedLanguage = Language;
            Loc.Apply(Language);
        }
        AudioServer.SetBusVolumeDb(0, MasterVolume <= 0.001f ? -80f : Mathf.LinearToDb(MasterVolume));
        if (!Game.AutoTest)
        {
            var mode = DisplayServer.WindowGetMode();
            bool isFull = mode is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen;
            if (Fullscreen != isFull)
                DisplayServer.WindowSetMode(Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
            DisplayServer.WindowSetVsyncMode(VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        }
        Changed?.Invoke();
    }
}
