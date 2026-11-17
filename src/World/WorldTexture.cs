using Godot;

namespace Heimskringla.World;

/// <summary>
/// Publishes the coarse map as a global shader texture (world_map), so shaders can look up
/// climate and water for any point:
///   r = temperature (0..1), g = terrain height / 100 m, b/a = flow direction*speed for rivers (m/s, xz).
/// Lookup: uv = (xz - world_rect.xy) * world_rect.zw
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
        // Texture pixel k sits at the center of texel k; grid point i is at x = -Half + i*Cell.
        // Shift by half a texel so bilinear sampling hits the grid points exactly.
        float size = n * WorldSettings.CellSize;
        float min = -WorldSettings.HalfSize - WorldSettings.CellSize * 0.5f;
        RenderingServer.GlobalShaderParameterSet("world_map", tex);

        // world_masks: r = gullies (0..1), g = curvature (0.5 = flat, >0.5 hollow, <0.5 ridge), b = rock mask
        var mdata = new byte[n * n * 4];
        for (int k = 0; k < n * n; k++)
        {
            mdata[k * 4] = (byte)(Mathf.Clamp(w.Gully != null ? w.Gully[k] : 0f, 0f, 1f) * 255f);
            mdata[k * 4 + 1] = (byte)(Mathf.Clamp(0.5f + (w.Curv != null ? w.Curv[k] : 0f) / 24f, 0f, 1f) * 255f);
            mdata[k * 4 + 2] = (byte)(Mathf.Clamp(w.Rock != null ? w.Rock[k] : 0f, 0f, 1f) * 255f);
            mdata[k * 4 + 3] = 255;
        }
        var mimg = Image.CreateFromData(n, n, false, Image.Format.Rgba8, mdata);
        RenderingServer.GlobalShaderParameterSet("world_masks", ImageTexture.CreateFromImage(mimg));
        RenderingServer.GlobalShaderParameterSet("world_rect", new Vector4(min, min, 1f / size, 1f / size));
    }
}
