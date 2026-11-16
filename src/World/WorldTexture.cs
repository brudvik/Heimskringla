using Godot;

namespace Heimskringla.World;

/// <summary>
/// Legger grovkartet ut som en global shadertekstur (world_map), så shaderne kan slå opp
/// klima og vann for et hvilket som helst punkt:
///   r = temperatur (0..1), g = terrenghøyde / 100 m, b/a = strømretning*fart for elver (m/s, xz).
/// Oppslag: uv = (xz - world_rect.xy) * world_rect.zw
/// </summary>
public static class WorldTexture
{
    public static void Publish(WorldGenerator w)
    {
        int n = w.N;
        var data = new byte[n * n * 16];
        var f = new float[4];
        for (int k = 0; k < n * n; k++)
        {
            f[0] = w.Temp[k];
            f[1] = w.Height[k] / 100f;
            f[2] = w.FlowX != null ? w.FlowX[k] : 0f;
            f[3] = w.FlowZ != null ? w.FlowZ[k] : 0f;
            System.Buffer.BlockCopy(f, 0, data, k * 16, 16);
        }
        var img = Image.CreateFromData(n, n, false, Image.Format.Rgbaf, data);
        img.Convert(Image.Format.Rgbah);
        var tex = ImageTexture.CreateFromImage(img);
        // Teksturpiksel k ligger midt i texel k; gridpunkt i ligger på x = -Half + i*Cell.
        // Forskyv et halvt texel så bilineær sampling treffer gridpunktene nøyaktig.
        float size = n * WorldSettings.CellSize;
        float min = -WorldSettings.HalfSize - WorldSettings.CellSize * 0.5f;
        RenderingServer.GlobalShaderParameterSet("world_map", tex);
        RenderingServer.GlobalShaderParameterSet("world_rect", new Vector4(min, min, 1f / size, 1f / size));
    }
}
