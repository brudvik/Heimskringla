namespace Heimskringla.World;

/// <summary>Global constants for world generation.</summary>
public static class WorldSettings
{
    /// <summary>Edge length of the world in meters (the world is centered at the origin).</summary>
    public const float WorldSize = 10240f;
    public const float HalfSize = WorldSize * 0.5f;

    /// <summary>Number of cells in the coarse map per axis. 1024 gives 10 m per cell.</summary>
    public const int MapResolution = 1024;
    public const float CellSize = WorldSize / MapResolution;

    public const float SeaLevel = 0f;

    /// <summary>How many cells (10 m) of catchment area are needed before a river forms.</summary>
    public const float RiverThreshold = 450f;
}
