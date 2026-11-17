using Godot;
using Heimskringla.World;

namespace Heimskringla.Core;

/// <summary>Global references shared between systems (set up by Main).</summary>
public static class Game
{
    public static WorldGenerator World;
    public static JobQueue Jobs;
    public static Terrain.CollisionManager Collision;

    public static ShaderMaterial TerrainMaterial;
    public static ShaderMaterial WaterMaterial;
    public static StandardMaterial3D PropMaterial;

    /// <summary>The point the systems stream around (player, or camera in fly mode).</summary>
    public static Vector3 Focus;

    /// <summary>Autotest is running: don't capture the mouse or react to user input (the user is working in other windows).</summary>
    public static bool AutoTest;

    // Collision layers (bitmasks)
    public const uint LayerWorld = 1;        // terrain, trees, boulders
    public const uint LayerInteract = 2;     // anything that can be harvested/picked up
    public const uint LayerPlayer = 4;
}
