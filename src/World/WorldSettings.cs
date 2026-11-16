namespace Heimskringla.World;

/// <summary>Globale konstanter for verdensgenereringen.</summary>
public static class WorldSettings
{
    /// <summary>Kantlengde på verden i meter (verden er sentrert i origo).</summary>
    public const float WorldSize = 10240f;
    public const float HalfSize = WorldSize * 0.5f;

    /// <summary>Antall celler i grovkartet per akse. 1024 gir 10 m per celle.</summary>
    public const int MapResolution = 1024;
    public const float CellSize = WorldSize / MapResolution;

    public const float SeaLevel = 0f;

    /// <summary>Hvor mange celler (10 m) med nedbørsfelt som trengs før det blir elv.</summary>
    public const float RiverThreshold = 450f;
}
