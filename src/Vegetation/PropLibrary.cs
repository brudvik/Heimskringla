using System;
using System.Collections.Generic;
using Godot;
using Heimskringla.Core;

namespace Heimskringla.Vegetation;

public enum PropKind { Spruce, Pine, Birch, Oak, DeadTree, Bush, Fern, Flower, Mushroom, Boulder, Stone, Branch, Cliff, Driftwood, Log }

public enum ToolKind { Hands, Axe, Pickaxe }

public enum PropRole { Decor, Tree, Rock, Pickup, Obstacle }

public sealed class PropDef
{
    public PropKind Kind;
    public string Name;
    /// <summary>Translated name for display (Name is the translation key).</summary>
    public string DisplayName => Loc.T(Name);
    public PropRole Role;
    public ModelVariant[] Variants;
    /// <summary>Index where the snow/cold variants start (-1 = none). Chosen in cold zones.</summary>
    public int ColdStart = -1;
    public float MaxDistance = 950f;   // not shown farther away than this
    public float MinScale = 0.85f, MaxScale = 1.2f;
    public float Sink;                  // how far it is sunk into the ground (m, before scale)
    public bool CastShadow = true;
    // Physical shape (before scaling). Trees: trunk radius. Height is taken from the model.
    public float TrunkRadius = 0.3f;
    // Harvesting
    public ToolKind Tool = ToolKind.Hands;
    public int Hp = 1;
    public string Item;
    public int ItemsOnHit;
    public int ItemsOnBreak;
    public int LogsOnFell;
    public int BranchesOnFell;

    public ModelVariant Variant(int v) => Variants[Math.Clamp(v, 0, Variants.Length - 1)];
}

/// <summary>
/// Registry of all props. Uses CC0 models from res://assets when they exist, otherwise
/// procedurally generated low-poly meshes (so the game always works).
/// </summary>
public static class PropLibrary
{
    public const string Nature = "res://assets/nature/";
    public const string LowPoly = "res://assets/nature_lowpoly/";
    public const string KenneyNature = "res://assets/kenney_nature/";
    public static Mesh StumpMesh, StumpSnowMesh;
    public static PropDef[] Defs;
    public static Mesh GrassMesh;
    public static int MaxVariants;
    public static bool UsingAssets;

    public static PropDef Get(PropKind k) => Defs[(int)k];

    public static void Init()
    {
        Defs = new PropDef[Enum.GetValues<PropKind>().Length];
        UsingAssets = ModelLibrary.Exists(Nature + "Pine_1.gltf");

        var proc = new Procedural(Game.PropMaterial, BarkMaterial());

        // ---------- Trees
        // Spruce: our own procedurally generated Norway spruce with needle cards (SpruceBuilder); the low-poly spruces from
        // the nature pack had flat, angular branch plates that looked like cardboard up close.
        var spruce = OS.GetEnvironment("HK_OLDSPRUCE") == "1" ? ModelLibrary.Series(Nature + "Pine_{0}.gltf", 1, 3) : SpruceBuilder.Variants();
        Def(PropKind.Spruce, "PROP_SPRUCE", PropRole.Tree, spruce, proc.Spruce, d =>
        {
            d.MinScale = 0.9f; d.MaxScale = 1.45f; d.Sink = 0.15f; d.TrunkRadius = 0.3f;
            d.Tool = ToolKind.Axe; d.Hp = 5; d.LogsOnFell = 3; d.BranchesOnFell = 1;
        });
        var pine = ModelLibrary.Series(Nature + "Pine_{0}.gltf", 4, 5, 1.3f);
        Def(PropKind.Pine, "PROP_PINE", PropRole.Tree, pine, proc.Pine, d =>
        {
            d.MinScale = 0.85f; d.MaxScale = 1.2f; d.Sink = 0.2f; d.TrunkRadius = 0.3f;
            d.Tool = ToolKind.Axe; d.Hp = 6; d.LogsOnFell = 4; d.BranchesOnFell = 1;
        });
        // Birch and oak: procedurally generated deciduous trees with leaf cards (LeafTreeBuilder); the round
        // "balloon" crowns from the nature pack are only used with HK_OLDTREES=1
        bool oldTrees = OS.GetEnvironment("HK_OLDTREES") == "1";
        var birchBark = BirchMaterial();
        var birch = oldTrees ? OldBirch(birchBark) : LeafTreeBuilder.Variants(LeafTreeBuilder.Species.Birch, birchBark);
        Def(PropKind.Birch, "PROP_BIRCH", PropRole.Tree, birch, proc.Birch, d =>
        {
            d.MinScale = 0.75f; d.MaxScale = 1.2f; d.Sink = 0.15f; d.TrunkRadius = 0.25f;
            d.Tool = ToolKind.Axe; d.Hp = 4; d.LogsOnFell = 2; d.BranchesOnFell = 2;
        });
        var oak = oldTrees ? OldOak() : LeafTreeBuilder.Variants(LeafTreeBuilder.Species.Oak,
            new StandardMaterial3D { ResourceName = "Bark", AlbedoColor = new Color(0.36f, 0.31f, 0.26f), Roughness = 0.95f });
        Def(PropKind.Oak, "PROP_OAK", PropRole.Tree, oak, proc.Oak, d =>
        {
            d.MinScale = 0.85f; d.MaxScale = 1.3f; d.Sink = 0.15f; d.TrunkRadius = 0.4f;
            d.Tool = ToolKind.Axe; d.Hp = 8; d.LogsOnFell = 5; d.BranchesOnFell = 2;
        });
        var dead = ModelLibrary.Series(Nature + "DeadTree_{0}.gltf", 1, 5, 0.55f);
        Def(PropKind.DeadTree, "PROP_DEAD_PINE", PropRole.Tree, dead, proc.Pine, d =>
        {
            d.MinScale = 0.8f; d.MaxScale = 1.2f; d.Sink = 0.25f; d.TrunkRadius = 0.45f;
            d.Tool = ToolKind.Axe; d.Hp = 3; d.LogsOnFell = 2; d.BranchesOnFell = 3;
        });

        // ---------- Small vegetation (decoration)
        var bush = ModelLibrary.Series(Nature + "Bush_Common.gltf", 0, 0, 0.8f);
        // Bush_Common uses autumn-red foliage; give it the green foliage material from regular trees
        var greenLeaves = ModelLibrary.LoadMesh(Nature + "CommonTree_1.gltf")?.SurfaceGetMaterial(1);
        if (greenLeaves != null)
            foreach (var b in bush) b.Mesh = ModelLibrary.WithMaterial(b.Mesh, 0, greenLeaves);
        // Variation: bush with flowers (pink/white, spring/summer feel) and a low, wide variant
        var flowerBush = ModelLibrary.Variant(Nature + "Bush_Common_Flowers.gltf", 0.8f);
        if (flowerBush != null) bush.Add(flowerBush);
        if (bush.Count > 0)
            bush.Add(new ModelVariant { Mesh = bush[0].Mesh, BaseScale = 1.15f });
        Def(PropKind.Bush, "PROP_BUSH", PropRole.Decor, bush, proc.Bush, d =>
        {
            d.MaxDistance = 400f; d.MinScale = 0.7f; d.MaxScale = 1.3f; d.Sink = 0.1f;
        });
        var fern = ModelLibrary.Series(Nature + "Fern_1.gltf", 0, 0, 0.2f);
        fern.AddRange(ModelLibrary.Series(Nature + "Plant_1.gltf", 0, 0, 0.9f));
        fern.AddRange(ModelLibrary.Series(Nature + "Plant_1_Big.gltf", 0, 0, 0.4f));
        Def(PropKind.Fern, "PROP_FERN", PropRole.Decor, fern, proc.Bush, d =>
        {
            d.MaxDistance = 160f; d.MinScale = 0.8f; d.MaxScale = 1.3f; d.CastShadow = false;
        });
        var flower = ModelLibrary.Series(Nature + "Flower_{0}_Group.gltf", 3, 4, 0.3f);
        flower.AddRange(ModelLibrary.Series(Nature + "Clover_{0}.gltf", 1, 2, 0.4f));
        Def(PropKind.Flower, "PROP_FLOWERS", PropRole.Decor, flower, proc.Bush, d =>
        {
            d.MaxDistance = 120f; d.MinScale = 0.8f; d.MaxScale = 1.3f; d.CastShadow = false;
        });
        var mush = ModelLibrary.Series(Nature + "Mushroom_Common.gltf", 0, 0, 0.45f);
        Def(PropKind.Mushroom, "PROP_MUSHROOM", PropRole.Pickup, mush, proc.Stone, d =>
        {
            d.MaxDistance = 100f; d.MinScale = 0.8f; d.MaxScale = 1.3f; d.Item = "mushroom"; d.CastShadow = false;
        });

        // ---------- Rock
        var rockMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/rock_formation.gdshader") };
        var boulder = ModelLibrary.Series(Nature + "Rock_Medium_{0}.gltf", 1, 3, 0.75f);
        foreach (var v in boulder) v.Mesh = ModelLibrary.WithMaterialAll(v.Mesh, rockMat);
        Def(PropKind.Boulder, "PROP_BOULDER", PropRole.Rock, boulder, proc.Boulder, d =>
        {
            // Sunk well in (boulders are partly buried, not sitting on top of the ground)
            d.MaxDistance = 550f; d.MinScale = 0.6f; d.MaxScale = 1.6f; d.Sink = 0.55f;
            d.Tool = ToolKind.Pickaxe; d.Hp = 6; d.Item = "stone"; d.ItemsOnHit = 1; d.ItemsOnBreak = 3;
        });
        var stone = ModelLibrary.Series(Nature + "Pebble_Round_{0}.gltf", 1, 5, 0.8f);
        stone.AddRange(ModelLibrary.Series(Nature + "Pebble_Square_{0}.gltf", 1, 6, 0.8f));
        Def(PropKind.Stone, "PROP_STONE", PropRole.Pickup, stone, proc.Stone, d =>
        {
            d.MaxDistance = 120f; d.MinScale = 0.8f; d.MaxScale = 1.3f; d.Item = "stone"; d.CastShadow = false;
        });

        // ---------- Cliff formations: Kenney shapes + large MegaKit rocks, shared rock shader
        // Mountainsides now have their own rock layers/cliffs in the terrain; the Kenney pillars looked like gray prisms
        // on top of the mountain. Only rounded rock outcrops, sunk well in.
        var cliffs = new List<ModelVariant>();
        cliffs.AddRange(ModelLibrary.Series(Nature + "Rock_Medium_{0}.gltf", 1, 3, 3.2f));
        // Scanned cliffs from Poly Haven (scripts/fetch_polyhaven_models.py): give real rock shapes
        // with edges and overhangs that the terrain mesh cannot make. Godot generates LODs on import.
        const string PH = "res://assets/polyhaven/";
        if (OS.GetEnvironment("HK_NOSCANS") != "1")   // test switch
        foreach (var (id, scale) in new[] { ("rock_face_01", 6f), ("rock_face_02", 9f), ("mountainside", 3f), ("boulder_01", 6f) })
        {
            var v = ModelLibrary.Variant(PH + $"{id}/{id}.gltf", scale);
            if (v == null) continue;
            var face = ModelVariant.AverageNormal(v.Mesh);
            bool shell = new Vector2(face.X, face.Z).Length() > 0.4f;   // open shell (only the front side scanned)
            v.FaceDir = shell ? new Vector3(face.X, 0, face.Z).Normalized() : null;
            v.SinkFrac = shell ? 0.25f : 0.35f;
            GD.Print($"Cliff {id}: front {face}, shell={shell}");
            cliffs.Add(v);
            cliffs.Add(new ModelVariant { Mesh = v.Mesh, BaseScale = scale * 1.4f, FaceDir = v.FaceDir, SinkFrac = v.SinkFrac });
        }
        cliffs.RemoveAll(v => v == null);
        foreach (var v in cliffs) v.Mesh = ModelLibrary.WithMaterialAll(v.Mesh, rockMat);
        Def(PropKind.Cliff, "PROP_CLIFF", PropRole.Obstacle, cliffs, proc.Boulder, d =>
        {
            d.MaxDistance = 1000f; d.MinScale = 0.8f; d.MaxScale = 1.6f; d.Sink = 0.9f;
        });

        // ---------- Driftwood on the beaches (gives timber)
        // Uses the same bark-textured log as the timber logs, just larger and bleached
        Def(PropKind.Driftwood, "PROP_DRIFTWOOD", PropRole.Pickup, null, proc.Log, d =>
        {
            d.MaxDistance = 150f; d.MinScale = 1.2f; d.MaxScale = 2.0f; d.Item = "wood"; d.Sink = 0.1f;
        });

        // ---------- Wood resources (always procedurally generated, with bark texture)
        Def(PropKind.Branch, "PROP_BRANCH", PropRole.Pickup, null, proc.Branch, d =>
        {
            d.MaxDistance = 120f; d.MinScale = 0.8f; d.MaxScale = 1.3f; d.Item = "branch"; d.CastShadow = false;
        });
        Def(PropKind.Log, "PROP_LOG", PropRole.Pickup, null, proc.Log, d => { d.Item = "wood"; });

        // ---------- Snow-covered variants (Ultimate Nature Pack, low-poly) for cold zones
        // Spruce/pine/birch use the regular models in cold zones too; the snow is applied in
        // the foliage shader (the low-poly snow trees were angular and did not fit with the rest).
        AddCold(PropKind.DeadTree, ModelLibrary.Series(LowPoly + "BirchTree_Dead_{0}.glb", 1, 5, 1.5f));
        
        var snowRocks = ModelLibrary.Series(LowPoly + "Rock_Snow_{0}.glb", 1, 7, 2.6f);
        foreach (var v in snowRocks)
        {
            // The whole rock gets the rock shader; the snow is applied in the shader (cold zones always have snow)
            v.Mesh = ModelLibrary.WithMaterialAll(v.Mesh, rockMat);
        }
        AddCold(PropKind.Boulder, snowRocks);
        StumpMesh = ModelLibrary.LoadMesh(LowPoly + "TreeStump.glb");
        StumpSnowMesh = BrightenSnow(ModelLibrary.LoadMesh(LowPoly + "TreeStump_Snow.glb")) ?? StumpMesh;

        // ---------- Wind (and seasonal colors) on everything that grows
        //                                  bend  flutter  deciduous
        ApplyWind(PropKind.Spruce,          0.55f, 0.35f, false);

        ApplyWind(PropKind.Pine,            0.65f, 0.45f, false);
        ApplyWind(PropKind.Birch,           1.35f, 1.4f,  true);
        ApplyWind(PropKind.Oak,             0.8f,  1.0f,  true);
        ApplyWind(PropKind.DeadTree,        0.35f, 0.15f, false);
        ApplyWind(PropKind.Bush,            22f,   1.2f,  true);
        ApplyWind(PropKind.Fern,            45f,   1.6f,  true);
        ApplyWind(PropKind.Flower,          60f,   1.8f,  false);
        // Needle and leaf cards are flat: fade out cards seen edge-on (foliage.gdshader: card_fade)
        foreach (var kind in new[] { PropKind.Spruce, PropKind.Birch, PropKind.Oak })
            foreach (var v in Defs[(int)kind].Variants)
                for (int s = 0; s < v.Mesh.GetSurfaceCount(); s++)
                    if (v.Mesh.SurfaceGetMaterial(s) is ShaderMaterial sm && (sm.ResourceName == "Needles" || sm.ResourceName == "Leaves"))
                        sm.SetShaderParameter("card_fade", 1f);

        MaxVariants = 1;
        foreach (var d in Defs) MaxVariants = Math.Max(MaxVariants, d.Variants.Length);
        GrassMesh = Procedural.BuildGrassMesh();
    }

    /// <summary>Adds snow/cold variants after the regular ones.</summary>
    private static void AddCold(PropKind kind, List<ModelVariant> cold)
    {
        var d = Defs[(int)kind];
        if (cold == null || cold.Count == 0 || d == null) return;
        foreach (var v in cold) v.Mesh = BrightenSnow(v.Mesh);
        var all = new List<ModelVariant>(d.Variants);
        d.ColdStart = all.Count;
        all.AddRange(cold);
        d.Variants = all.ToArray();
    }

    private static readonly Dictionary<Mesh, Mesh> SnowFixed = new();

    /// <summary>Brightens the "Snow" material in the low-poly models (it is a dark purple-gray in the original).</summary>
    public static Mesh BrightenSnow(Mesh mesh)
    {
        if (mesh == null) return null;
        if (SnowFixed.TryGetValue(mesh, out var done)) return done;
        Mesh result = mesh;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is not BaseMaterial3D m || !m.ResourceName.Contains("Snow")) continue;
            var snow = (BaseMaterial3D)m.Duplicate();
            snow.AlbedoColor = new Color(0.84f, 0.87f, 0.93f);
            snow.Roughness = 0.75f;
            if (result == mesh) result = (Mesh)mesh.Duplicate();
            ((ArrayMesh)result).SurfaceSetMaterial(s, snow);
        }
        SnowFixed[mesh] = result;
        return result;
    }

    private static void Def(PropKind kind, string name, PropRole role, List<ModelVariant> models, Func<Mesh> fallback, Action<PropDef> setup)
    {
        var d = new PropDef { Kind = kind, Name = name, Role = role };
        d.Variants = models != null && models.Count > 0
            ? models.ToArray()
            : new[] { new ModelVariant { Mesh = fallback(), BaseScale = 1f } };
        foreach (var v in d.Variants) v.Aabb = v.Mesh.GetAabb();
        setup(d);
        Defs[(int)kind] = d;
    }

    private static readonly Dictionary<(Material, PropKind), Material> WindMats = new();

    /// <summary>
    /// Swaps the materials on all variants of a plant to the wind shader (foliage.gdshader).
    /// All surfaces on the same model get the same bend/flutter, so foliage and trunk stay together.
    /// </summary>
    private static void ApplyWind(PropKind kind, float bend, float flutter, bool deciduous)
    {
        var d = Defs[(int)kind];
        if (d == null) return;
        foreach (var v in d.Variants)
        {
            if (v.Mesh is not ArrayMesh) continue;
            var copy = (ArrayMesh)v.Mesh.Duplicate();
            for (int s = 0; s < copy.GetSurfaceCount(); s++)
            {
                var src = copy.SurfaceGetMaterial(s);
                if (src == null) continue;
                if (!WindMats.TryGetValue((src, kind), out var wm))
                {
                    wm = WindMaterial(src, bend, flutter, deciduous);
                    WindMats[(src, kind)] = wm;
                }
                copy.SurfaceSetMaterial(s, wm);
            }
            v.Mesh = copy;
        }
    }

    private static Material WindMaterial(Material src, float bend, float flutter, bool deciduous)
    {
        if (src is ShaderMaterial sm)
        {
            // Custom shaders (birch bark) have built-in wind; give them the right parameters
            var c = (ShaderMaterial)sm.Duplicate();
            c.SetShaderParameter("bend", bend);
            c.SetShaderParameter("flutter", flutter);
            return c;
        }
        if (src is not BaseMaterial3D bm) return src;
        string n = bm.ResourceName ?? "";
        // The low-poly models (Ultimate Nature Pack) call the trunk "Wood", and the birches "White"/"Black"
        bool barkName = n.Contains("Bark") || n.Contains("Wood") || n.Contains("Trunk") || n == "White" || n == "Black";
        bool leafy = !(barkName || n.Contains("Snow"));
        var m = new ShaderMaterial { Shader = FoliageShader ??= GD.Load<Shader>("res://shaders/foliage.gdshader") };
        m.SetShaderParameter("albedo_color", bm.AlbedoColor);
        if (bm.AlbedoTexture != null)
        {
            m.SetShaderParameter("albedo_tex", bm.AlbedoTexture);
            m.SetShaderParameter("use_tex", true);
        }
        m.SetShaderParameter("use_vcol", bm.VertexColorUseAsAlbedo);
        m.SetShaderParameter("vcol_srgb", bm.VertexColorIsSrgb);
        if (bm.NormalEnabled && bm.NormalTexture != null)
        {
            m.SetShaderParameter("normal_tex", bm.NormalTexture);
            m.SetShaderParameter("use_normal", true);
        }
        m.SetShaderParameter("alpha_cut", bm.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor ? Math.Max(0.01f, bm.AlphaScissorThreshold) : 0f);
        m.SetShaderParameter("roughness", bm.Roughness);
        m.SetShaderParameter("bend", bend);
        m.SetShaderParameter("flutter", flutter);
        m.SetShaderParameter("leafy", leafy ? 1f : 0f);
        m.SetShaderParameter("deciduous", deciduous && leafy ? 1f : 0f);
        m.SetShaderParameter("bark", barkName ? 1f : 0f);
        m.SetShaderParameter("bark_layer", deciduous ? 1f : 0f);
        m.ResourceName = n;
        return m;
    }

    private static Shader FoliageShader;

    private static List<ModelVariant> OldBirch(Material birchBark)
    {
        var birch = new List<ModelVariant>();
        foreach (var v in ModelLibrary.Series(Nature + "CommonTree_{0}.gltf", 3, 4))
        {
            var m = ModelLibrary.WithMaterial(v.Mesh, 0, birchBark);
            if (m.GetSurfaceCount() > 1 && m.SurfaceGetMaterial(1) is BaseMaterial3D leaves)
            {
                var lm = (BaseMaterial3D)leaves.Duplicate();
                lm.AlbedoColor = new Color(1.02f, 1.1f, 0.74f);
                ((ArrayMesh)m).SurfaceSetMaterial(1, lm);
            }
            birch.Add(new ModelVariant { Mesh = m, Aabb = v.Aabb, BaseScale = 0.85f });
        }
        return birch;
    }

    private static List<ModelVariant> OldOak()
    {
        var oak = new List<ModelVariant>();
        foreach (int i in new[] { 1, 2, 5 })
        {
            var v = ModelLibrary.Variant(Nature + $"CommonTree_{i}.gltf", 1.15f);
            if (v != null) oak.Add(v);
        }
        return oak;
    }

    private static Material BirchMaterial()
    {
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/birch_bark.gdshader") };
        if (ResourceLoader.Exists(Nature + "Bark_NormalTree.png"))
        {
            mat.SetShaderParameter("bark_tex", GD.Load<Texture2D>(Nature + "Bark_NormalTree.png"));
            mat.SetShaderParameter("bark_normal", GD.Load<Texture2D>(Nature + "Bark_NormalTree_Normal.png"));
        }
        return mat;
    }

    private static Material BarkMaterial()
    {
        var mat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.9f };
        if (ResourceLoader.Exists(Nature + "Bark_NormalTree.png"))
        {
            mat.AlbedoTexture = GD.Load<Texture2D>(Nature + "Bark_NormalTree.png");
            mat.Uv1Triplanar = true;
            mat.Uv1Scale = new Vector3(1.5f, 1.5f, 1.5f);
        }
        return mat;
    }

    /// <summary>Procedurally generated low-poly meshes (fallback when assets are missing, and for branches/logs).</summary>
    private sealed class Procedural
    {
        private readonly Material _mat, _bark;
        public Procedural(Material mat, Material bark) { _mat = mat; _bark = bark; }

        private static readonly Color Bark = new(0.36f, 0.25f, 0.16f);
        private static readonly Color Rock = new(0.50f, 0.49f, 0.46f);

        public Mesh Spruce()
        {
            var rng = new Random(11);
            var b = new MeshBuilder();
            b.Prism(Vector3.Zero, 6, 0.28f, 0.06f, 8.0f, Bark);
            for (int t = 0; t < 5; t++)
                b.Prism(new Vector3(0, 1.1f + t * 1.4f, 0), 7, 2.15f - t * 0.40f, 0f, 2.7f - t * 0.25f, new Color(0.16f, 0.30f, 0.14f), rng, 0.12f, twist: t * 0.4f);
            return b.ToMesh(_mat);
        }

        public Mesh Pine()
        {
            var rng = new Random(12);
            var b = new MeshBuilder();
            var g = new Color(0.21f, 0.34f, 0.15f);
            b.Prism(Vector3.Zero, 6, 0.30f, 0.17f, 9.2f, new Color(0.55f, 0.34f, 0.21f), rng, 0.08f);
            b.Icosphere(new Vector3(0, 9.3f, 0), new Vector3(2.3f, 1.2f, 2.3f), 1, g, rng, 0.15f, 0.1f);
            b.Icosphere(new Vector3(1.1f, 8.2f, 0.4f), new Vector3(1.5f, 0.8f, 1.5f), 1, g, rng, 0.15f, 0.1f);
            return b.ToMesh(_mat);
        }

        public Mesh Birch()
        {
            var rng = new Random(13);
            var b = new MeshBuilder();
            b.Prism(Vector3.Zero, 6, 0.17f, 0.07f, 6.6f, new Color(0.88f, 0.87f, 0.82f));
            var g = new Color(0.47f, 0.64f, 0.24f);
            b.Icosphere(new Vector3(0, 6.4f, 0), new Vector3(1.5f, 1.7f, 1.5f), 1, g, rng, 0.18f, 0.12f);
            b.Icosphere(new Vector3(0.8f, 5.3f, 0.3f), new Vector3(1.2f, 1.2f, 1.2f), 1, g, rng, 0.18f, 0.12f);
            return b.ToMesh(_mat);
        }

        public Mesh Oak()
        {
            var rng = new Random(14);
            var b = new MeshBuilder();
            var g = new Color(0.28f, 0.46f, 0.15f);
            b.Prism(Vector3.Zero, 7, 0.52f, 0.32f, 4.8f, new Color(0.32f, 0.25f, 0.18f), rng, 0.08f);
            b.Icosphere(new Vector3(0, 6.6f, 0), new Vector3(2.6f, 2.0f, 2.6f), 1, g, rng, 0.15f, 0.1f);
            b.Icosphere(new Vector3(1.8f, 5.4f, 0.6f), new Vector3(1.9f, 1.6f, 1.9f), 1, g, rng, 0.15f, 0.1f);
            return b.ToMesh(_mat);
        }

        public Mesh Bush()
        {
            var rng = new Random(15);
            var b = new MeshBuilder();
            var g = new Color(0.24f, 0.40f, 0.14f);
            b.Icosphere(new Vector3(0, 0.45f, 0), new Vector3(0.8f, 0.6f, 0.8f), 0, g, rng, 0.2f, 0.15f);
            b.Icosphere(new Vector3(0.5f, 0.35f, 0.2f), new Vector3(0.55f, 0.45f, 0.55f), 0, g, rng, 0.2f, 0.15f);
            return b.ToMesh(_mat);
        }

        public Mesh Boulder()
        {
            var b = new MeshBuilder();
            b.Icosphere(new Vector3(0, 0.45f, 0), new Vector3(1.3f, 0.95f, 1.1f), 1, Rock, new Random(16), 0.2f, 0.12f);
            return b.ToMesh(_mat);
        }

        public Mesh Stone()
        {
            var b = new MeshBuilder();
            b.Icosphere(new Vector3(0, 0.08f, 0), new Vector3(0.22f, 0.15f, 0.2f), 0, Rock.Lightened(0.1f), new Random(17), 0.2f, 0.1f);
            return b.ToMesh(_mat);
        }

        public Mesh Branch()
        {
            var b = new MeshBuilder();
            var wood = new Color(0.85f, 0.8f, 0.75f);
            b.Xf = new Transform3D(new Basis(Vector3.Back, -Mathf.Pi / 2f), new Vector3(-0.65f, 0.06f, 0));
            b.Prism(Vector3.Zero, 5, 0.05f, 0.03f, 1.3f, wood);
            b.Xf = new Transform3D(new Basis(Vector3.Back, -Mathf.Pi / 2f) * new Basis(Vector3.Right, 0.6f), new Vector3(-0.1f, 0.06f, 0));
            b.Prism(Vector3.Zero, 4, 0.03f, 0.015f, 0.5f, wood);
            return b.ToMesh(_bark);
        }

        public Mesh Log()
        {
            var b = new MeshBuilder();
            b.Xf = new Transform3D(new Basis(Vector3.Back, -Mathf.Pi / 2f), new Vector3(-0.9f, 0, 0));
            b.Prism(Vector3.Zero, 10, 0.24f, 0.22f, 1.8f, new Color(0.9f, 0.85f, 0.8f), new Random(18), 0.05f, new Color(1.6f, 1.35f, 1.0f));
            return b.ToMesh(_bark);
        }

        /// <summary>
        /// Grass tuft: many thin, curved blades (4 segments per blade, tapering toward the tip).
        /// UV.y = 0 at the tip, 1 at the root; UV.x = 0..1 across. COLOR.r = random per blade.
        /// </summary>
        public static ArrayMesh BuildGrassMesh()
        {
            var rng = new Random(99);
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var n = new List<Vector3>();
            var col = new List<Color>();
            var idx = new List<int>();
            const int Blades = 16, Seg = 4;
            for (int i = 0; i < Blades; i++)
            {
                float a = (float)rng.NextDouble() * Mathf.Tau;
                float r = Mathf.Sqrt((float)rng.NextDouble()) * 0.28f;
                var root = new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
                float face = (float)rng.NextDouble() * Mathf.Tau;
                var side = new Vector3(Mathf.Cos(face), 0, Mathf.Sin(face));
                // The blade bends outward from the tuft's center (slightly random)
                var outward = (root.LengthSquared() > 1e-4f ? root.Normalized() : new Vector3(1, 0, 0)).Rotated(Vector3.Up, ((float)rng.NextDouble() - 0.5f) * 1.2f);
                float h = 0.28f + (float)rng.NextDouble() * 0.42f;
                float bend = 0.08f + (float)rng.NextDouble() * 0.22f;
                float w = 0.022f + (float)rng.NextDouble() * 0.018f;
                float rnd = (float)rng.NextDouble();
                int b0 = v.Count;
                for (int k = 0; k <= Seg; k++)
                {
                    float t = (float)k / Seg;
                    var c = root + outward * (bend * t * t * h * 2f) + new Vector3(0, h * t * (1f - bend * t * 0.35f), 0);
                    float hw = w * (1f - t * 0.92f);
                    // The normal points mostly up (the grass is lit like a carpet), slightly toward the blade
                    var nn = (Vector3.Up * 0.8f + outward * 0.2f).Normalized();
                    if (k < Seg)
                    {
                        v.Add(c - side * hw); uv.Add(new Vector2(0, 1f - t)); n.Add(nn); col.Add(new Color(rnd, 0, 0));
                        v.Add(c + side * hw); uv.Add(new Vector2(1, 1f - t)); n.Add(nn); col.Add(new Color(rnd, 0, 0));
                    }
                    else
                    {
                        v.Add(c); uv.Add(new Vector2(0.5f, 0)); n.Add(nn); col.Add(new Color(rnd, 0, 0));
                    }
                }
                for (int k = 0; k < Seg - 1; k++)
                {
                    int q = b0 + k * 2;
                    idx.Add(q); idx.Add(q + 1); idx.Add(q + 2);
                    idx.Add(q + 1); idx.Add(q + 3); idx.Add(q + 2);
                }
                int last = b0 + (Seg - 1) * 2;
                idx.Add(last); idx.Add(last + 1); idx.Add(last + 2);
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = n.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = uv.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = col.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(0, new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/grass.gdshader") });
            mesh.CustomAabb = new Aabb(new Vector3(-1, -0.2f, -1), new Vector3(2, 1.4f, 2));
            return mesh;
        }
    }
}
