using System.Text.Json;
using Godot;

namespace Heimskringla.Terrain;

/// <summary>
/// Hooks the real terrain textures (assets/terrain, from scripts/fetch_terrain_textures.py) up to
/// the terrain material. If they are missing, the texture part of the shader fades out and the terrain looks as before.
/// </summary>
public static class TerrainTextures
{
    private const string Dir = "res://assets/terrain/";

    public static void Apply(ShaderMaterial mat)
    {
        if (!ResourceLoader.Exists(Dir + "terrain_albedo.png") || !FileAccess.FileExists(Dir + "terrain_layers.json"))
        {
            mat.SetShaderParameter("tex_near", 0f);
            mat.SetShaderParameter("tex_far", 0.001f);
            GD.Print("Terrain textures missing (run scripts/fetch_terrain_textures.py) - using colors only.");
            return;
        }
        var alb = GD.Load<TextureLayered>(Dir + "terrain_albedo.png");
        var nrao = GD.Load<TextureLayered>(Dir + "terrain_nrao.png");
        mat.SetShaderParameter("albedo_arr", alb);
        mat.SetShaderParameter("nrao_arr", nrao);
        // Also global, so rocks (rock_formation) can use the same rock texture as the mountains
        RenderingServer.GlobalShaderParameterSet("rock_albedo_arr", alb);
        RenderingServer.GlobalShaderParameterSet("rock_nrao_arr", nrao);
        if (ResourceLoader.Exists(Dir + "detail_albedo.png"))
        {
            RenderingServer.GlobalShaderParameterSet("detail_albedo", GD.Load<TextureLayered>(Dir + "detail_albedo.png"));
            RenderingServer.GlobalShaderParameterSet("detail_nrao", GD.Load<TextureLayered>(Dir + "detail_nrao.png"));
        }
        using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(Dir + "terrain_layers.json"));
        var means = new Vector3[8];
        int i = 0;
        foreach (var layer in doc.RootElement.EnumerateArray())
        {
            if (i >= means.Length) break;
            var m = layer.GetProperty("mean_linear");
            means[i++] = new Vector3(m[0].GetSingle(), m[1].GetSingle(), m[2].GetSingle());
        }
        mat.SetShaderParameter("layer_mean", means);
    }
}
