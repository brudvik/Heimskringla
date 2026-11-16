using Godot;
using Heimskringla.World;

namespace Heimskringla.Core;

/// <summary>Globale referanser som deles mellom systemene (satt opp av Main).</summary>
public static class Game
{
    public static WorldGenerator World;
    public static JobQueue Jobs;
    public static Terrain.CollisionManager Collision;

    public static ShaderMaterial TerrainMaterial;
    public static ShaderMaterial WaterMaterial;
    public static StandardMaterial3D PropMaterial;

    /// <summary>Punktet systemene strømmer rundt (spiller eller kamera i flymodus).</summary>
    public static Vector3 Focus;

    // Kollisjonslag (bitmasker)
    public const uint LayerWorld = 1;        // terreng, trær, steinblokker
    public const uint LayerInteract = 2;     // alt som kan høstes/plukkes
    public const uint LayerPlayer = 4;
}
