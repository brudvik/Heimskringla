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

    public static void Add(string item, int count = 1)
    {
        if (string.IsNullOrEmpty(item) || count == 0) return;
        Items.TryGetValue(item, out int cur);
        Items[item] = cur + count;
        Changed?.Invoke();
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

    private sealed class SaveData
    {
        public int Seed { get; set; }
        public float[] Player { get; set; }
        public float TimeOfDay { get; set; }
        public long[] Removed { get; set; }
        public Dictionary<string, int> Inventory { get; set; }
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
        return (new Vector3(data.Player[0], data.Player[1], data.Player[2]), data.TimeOfDay);
    }

    public static void Reset()
    {
        Removed.Clear();
        Inventory.Items.Clear();
    }
}
