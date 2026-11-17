using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace Heimskringla.Core;

/// <summary>Inventory: resource name → count.</summary>
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
/// Changes to the world relative to the generated baseline (diff).
/// Only the IDs of harvested objects are stored, not the whole world.
/// </summary>
public static class WorldState
{
    public static readonly HashSet<long> Removed = new();
    /// <summary>Campfires: x, y, z, wood remaining (s). Filled on load, read on save.</summary>
    public static readonly List<float[]> Fires = new();
    /// <summary>Skills (0..100), e.g. "woodcutting". Increase a little each time they are used.</summary>
    public static readonly Dictionary<string, float> Skills = new();

    public static float Skill(string name) => Skills.TryGetValue(name, out float v) ? v : 0f;

    /// <summary>Increases a skill (fast at first, slower towards 100). Returns the new level.</summary>
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

    /// <summary>Loads saved state. Returns player position and time, or null.</summary>
    public static (Vector3 pos, float time)? Load(int seed)
    {
        var path = PathFor(seed);
        if (!System.IO.File.Exists(path)) return null;
        var data = JsonSerializer.Deserialize<SaveData>(System.IO.File.ReadAllText(path));
        Removed.Clear();
        foreach (var id in data.Removed ?? Array.Empty<long>()) Removed.Add(id);
        Inventory.Items.Clear();
        if (data.Inventory != null)
            foreach (var kv in data.Inventory)
            {
                string id = MigrateKey(kv.Key);
                Inventory.Items.TryGetValue(id, out int cur);
                Inventory.Items[id] = cur + kv.Value;
            }
        Inventory.Add("", 0);
        Fires.Clear();
        if (data.Fires != null) Fires.AddRange(data.Fires);
        Skills.Clear();
        if (data.Skills != null)
            foreach (var kv in data.Skills)
            {
                string id = MigrateKey(kv.Key);
                Skills[id] = Skills.TryGetValue(id, out float cur) ? Math.Max(cur, kv.Value) : kv.Value;
            }
        return (new Vector3(data.Player[0], data.Player[1], data.Player[2]), data.TimeOfDay);
    }

    /// <summary>Maps item/skill keys from older saves (Norwegian names) to the current English ids.</summary>
    private static string MigrateKey(string key) => key switch
    {
        "Tømmer" => "wood",
        "Grein" => "branch",
        "Stein" => "stone",
        "Sopp" => "mushroom",
        "Hogging" => "woodcutting",
        _ => key,
    };

    public static void Reset()
    {
        Removed.Clear();
        Inventory.Items.Clear();
        Fires.Clear();
        Skills.Clear();
    }
}
