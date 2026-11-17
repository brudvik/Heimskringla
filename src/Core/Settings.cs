using System;
using Godot;

namespace Heimskringla.Core;

/// <summary>
/// User settings (stored in user://settings.cfg). Apply() sets audio/window immediately;
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

    public static event Action Changed;

    private const string Path = "user://settings.cfg";
    private static bool _loaded;

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return;
        MasterVolume = (float)cfg.GetValue("lyd", "hoved", MasterVolume);
        MusicVolume = (float)cfg.GetValue("lyd", "musikk", MusicVolume);
        MouseSensitivity = (float)cfg.GetValue("kontroll", "mus", MouseSensitivity);
        Fov = (float)cfg.GetValue("kontroll", "synsfelt", Fov);
        Fullscreen = (bool)cfg.GetValue("skjerm", "fullskjerm", Fullscreen);
        VSync = (bool)cfg.GetValue("skjerm", "vsync", VSync);
        Quality = (int)cfg.GetValue("grafikk", "kvalitet", Quality);
        LastSeed = (int)cfg.GetValue("spill", "seed", LastSeed);
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("lyd", "hoved", MasterVolume);
        cfg.SetValue("lyd", "musikk", MusicVolume);
        cfg.SetValue("kontroll", "mus", MouseSensitivity);
        cfg.SetValue("kontroll", "synsfelt", Fov);
        cfg.SetValue("skjerm", "fullskjerm", Fullscreen);
        cfg.SetValue("skjerm", "vsync", VSync);
        cfg.SetValue("grafikk", "kvalitet", Quality);
        cfg.SetValue("spill", "seed", LastSeed);
        cfg.Save(Path);
    }

    /// <summary>Applies the settings (audio, window) and notifies the rest of the game.</summary>
    public static void Apply()
    {
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
