using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace Heimskringla.Core;

/// <summary>Inventar: ressursnavn → antall.</summary>
public static class Inventory
{
    public static readonly Dictionary<string, int> Items = new();
    public static event Action Changed;
    public static event Action<string, int> ItemAdded;

    public static void Add(string item, int count = 1)
    {
        if (string.IsNullOrEmpty(item) || count == 0) return;
        Items.TryGetValue(item, out int cur);
        Items[item] = cur + count;
        Changed?.Invoke();
        if (count > 0) ItemAdded?.Invoke(item, count);
    }

    public static int Count(string item) => Items.TryGetValue(item, out int c) ? c : 0;

    public static void Clear()
    {
        Items.Clear();
        Changed?.Invoke();
    }
}

/// <summary>
/// Endringer i verden i forhold til det genererte utgangspunktet (diff).
/// Kun ID-ene til høstede objekter lagres, ikke hele verden.
/// </summary>
public static class WorldState
{
    public static readonly HashSet<long> Removed = new();
    /// <summary>Bål: x, y, z, ved igjen (s). Fylles ved lasting, leses ved lagring.</summary>
    public static readonly List<float[]> Fires = new();
    /// <summary>Ferdigheter (0..100), f.eks. "Hogging". Øker litt for hver gang de brukes.</summary>
    public static readonly Dictionary<string, float> Skills = new();

    public static float Skill(string name) => Skills.TryGetValue(name, out float v) ? v : 0f;

    /// <summary>Øker en ferdighet (raskt i starten, saktere mot 100). Returnerer nytt nivå.</summary>
    public static float Practice(string name, float amount)
    {
        float v = Skill(name);
        v = Math.Min(100f, v + amount * (1f - v / 110f));
        Skills[name] = v;
        return v;
    }

    private sealed class SaveData
    {
        public int Seed { get; set; }
        public float[] Player { get; set; }
        public float TimeOfDay { get; set; }
        public long[] Removed { get; set; }
        public Dictionary<string, int> Inventory { get; set; }
        public float[][] Fires { get; set; }
        public Dictionary<string, float> Skills { get; set; }
    }

    private static string PathFor(int seed) => ProjectSettings.GlobalizePath($"user://saves/world_{seed}.json");

    public static void Save(int seed, Vector3 player, float timeOfDay)
    {
        var data = new SaveData
        {
            Seed = seed,
            Player = new[] { player.X, player.Y, player.Z },
            TimeOfDay = timeOfDay,
            Removed = new List<long>(Removed).ToArray(),
            Inventory = new Dictionary<string, int>(Inventory.Items),
            Fires = Fires.ToArray(),
            Skills = new Dictionary<string, float>(Skills),
        };
        var path = PathFor(seed);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(data));
    }

    public static bool Exists(int seed) => System.IO.File.Exists(PathFor(seed));

    /// <summary>Laster lagret tilstand. Returnerer spillerposisjon og tid, eller null.</summary>
    public static (Vector3 pos, float time)? Load(int seed)
    {
        var path = PathFor(seed);
        if (!System.IO.File.Exists(path)) return null;
        var data = JsonSerializer.Deserialize<SaveData>(System.IO.File.ReadAllText(path));
        Removed.Clear();
        foreach (var id in data.Removed ?? Array.Empty<long>()) Removed.Add(id);
        Inventory.Items.Clear();
        if (data.Inventory != null)
            foreach (var kv in data.Inventory) Inventory.Items[kv.Key] = kv.Value;
        Inventory.Add("", 0);
        Fires.Clear();
        if (data.Fires != null) Fires.AddRange(data.Fires);
        Skills.Clear();
        if (data.Skills != null) foreach (var kv in data.Skills) Skills[kv.Key] = kv.Value;
        return (new Vector3(data.Player[0], data.Player[1], data.Player[2]), data.TimeOfDay);
    }

    public static void Reset()
    {
        Removed.Clear();
        Inventory.Items.Clear();
        Fires.Clear();
        Skills.Clear();
    }
}
