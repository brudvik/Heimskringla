using System.Text.Json;
using Godot;

namespace Heimskringla.Terrain;

/// <summary>
/// Kobler de ekte terrengteksturene (assets/terrain, fra scripts/fetch_terrain_textures.py) til
/// terrengmaterialet. Mangler de, tones teksturdelen av shaderen ut og terrenget ser ut som før.
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
            GD.Print("Terrengteksturer mangler (kjør scripts/fetch_terrain_textures.py) - bruker bare farger.");
            return;
        }
        mat.SetShaderParameter("albedo_arr", GD.Load<TextureLayered>(Dir + "terrain_albedo.png"));
        mat.SetShaderParameter("nrao_arr", GD.Load<TextureLayered>(Dir + "terrain_nrao.png"));
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
