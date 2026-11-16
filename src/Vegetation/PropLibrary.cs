using System;
using System.Collections.Generic;
using Godot;
using Heimskringla.Core;

namespace Heimskringla.Vegetation;

public enum PropKind { Spruce, Pine, Birch, Oak, DeadTree, Bush, Fern, Flower, Mushroom, Boulder, Stone, Branch, Log }

public enum ToolKind { Hands, Axe, Pickaxe }

public enum PropRole { Decor, Tree, Rock, Pickup }

public sealed class PropDef
{
    public PropKind Kind;
    public string Name;
    public PropRole Role;
    public ModelVariant[] Variants;
    public float MaxDistance = 950f;   // vises ikke lenger unna enn dette
    public float MinScale = 0.85f, MaxScale = 1.2f;
    public float Sink;                  // hvor mye den senkes i bakken (m, før skala)
    public bool CastShadow = true;
    // Fysisk form (før skalering). Trær: stammeradius. Høyde hentes fra modellen.
    public float TrunkRadius = 0.3f;
    // Høsting
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
/// Register over alle props. Bruker CC0-modeller fra res://assets når de finnes, ellers
/// kodegenererte low-poly-mesher (så spillet alltid virker).
/// </summary>
public static class PropLibrary
{
    public const string Nature = "res://assets/nature/";
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

        // ---------- Trær
        var spruce = ModelLibrary.Series(Nature + "Pine_{0}.gltf", 1, 3);
        Def(PropKind.Spruce, "Gran", PropRole.Tree, spruce, proc.Spruce, d =>
        {
            d.MinScale = 0.9f; d.MaxScale = 1.45f; d.Sink = 0.15f; d.TrunkRadius = 0.3f;
            d.Tool = ToolKind.Axe; d.Hp = 5; d.LogsOnFell = 3; d.BranchesOnFell = 1;
        });
        var pine = ModelLibrary.Series(Nature + "Pine_{0}.gltf", 4, 5, 1.3f);
        Def(PropKind.Pine, "Furu", PropRole.Tree, pine, proc.Pine, d =>
        {
            d.MinScale = 0.85f; d.MaxScale = 1.2f; d.Sink = 0.2f; d.TrunkRadius = 0.3f;
            d.Tool = ToolKind.Axe; d.Hp = 6; d.LogsOnFell = 4; d.BranchesOnFell = 1;
        });
        var birch = new List<ModelVariant>();
        var birchBark = BirchMaterial();
        foreach (var v in ModelLibrary.Series(Nature + "CommonTree_{0}.gltf", 3, 4))
        {
            var m = ModelLibrary.WithMaterial(v.Mesh, 0, birchBark);
            if (m.GetSurfaceCount() > 1 && m.SurfaceGetMaterial(1) is BaseMaterial3D leaves)
            {
                var lm = (BaseMaterial3D)leaves.Duplicate();
                lm.AlbedoColor = new Color(1.25f, 1.35f, 0.75f);
                ((ArrayMesh)m).SurfaceSetMaterial(1, lm);
            }
            birch.Add(new ModelVariant { Mesh = m, Aabb = v.Aabb, BaseScale = 0.85f });
        }
        Def(PropKind.Birch, "Bjørk", PropRole.Tree, birch, proc.Birch, d =>
        {
            d.MinScale = 0.75f; d.MaxScale = 1.2f; d.Sink = 0.15f; d.TrunkRadius = 0.25f;
            d.Tool = ToolKind.Axe; d.Hp = 4; d.LogsOnFell = 2; d.BranchesOnFell = 2;
        });
        var oak = new List<ModelVariant>();
        foreach (int i in new[] { 1, 2, 5 })
        {
            var v = ModelLibrary.Variant(Nature + $"CommonTree_{i}.gltf", 1.15f);
            if (v != null) oak.Add(v);
        }
        Def(PropKind.Oak, "Eik", PropRole.Tree, oak, proc.Oak, d =>
        {
            d.MinScale = 0.85f; d.MaxScale = 1.3f; d.Sink = 0.15f; d.TrunkRadius = 0.4f;
            d.Tool = ToolKind.Axe; d.Hp = 8; d.LogsOnFell = 5; d.BranchesOnFell = 2;
        });
        var dead = ModelLibrary.Series(Nature + "DeadTree_{0}.gltf", 1, 5, 0.55f);
        Def(PropKind.DeadTree, "Tørrfuru", PropRole.Tree, dead, proc.Pine, d =>
        {
            d.MinScale = 0.8f; d.MaxScale = 1.2f; d.Sink = 0.25f; d.TrunkRadius = 0.45f;
            d.Tool = ToolKind.Axe; d.Hp = 3; d.LogsOnFell = 2; d.BranchesOnFell = 3;
        });

        // ---------- Småvegetasjon (pynt)
        var bush = ModelLibrary.Series(Nature + "Bush_Common.gltf", 0, 0, 0.8f);
        // Bush_Common bruker høstrødt løv; gi den det grønne løvmaterialet fra vanlige trær
        var greenLeaves = ModelLibrary.LoadMesh(Nature + "CommonTree_1.gltf")?.SurfaceGetMaterial(1);
        if (greenLeaves != null)
            foreach (var b in bush) b.Mesh = ModelLibrary.WithMaterial(b.Mesh, 0, greenLeaves);
        Def(PropKind.Bush, "Busk", PropRole.Decor, bush, proc.Bush, d =>
        {
            d.MaxDistance = 400f; d.MinScale = 0.7f; d.MaxScale = 1.3f; d.Sink = 0.1f;
        });
        var fern = ModelLibrary.Series(Nature + "Fern_1.gltf", 0, 0, 0.2f);
        fern.AddRange(ModelLibrary.Series(Nature + "Plant_1.gltf", 0, 0, 0.9f));
        fern.AddRange(ModelLibrary.Series(Nature + "Plant_1_Big.gltf", 0, 0, 0.4f));
        Def(PropKind.Fern, "Bregne", PropRole.Decor, fern, proc.Bush, d =>
        {
            d.MaxDistance = 160f; d.MinScale = 0.8f; d.MaxScale = 1.3f; d.CastShadow = false;
        });
        var flower = ModelLibrary.Series(Nature + "Flower_{0}_Group.gltf", 3, 4, 0.3f);
        flower.AddRange(ModelLibrary.Series(Nature + "Clover_{0}.gltf", 1, 2, 0.4f));
        Def(PropKind.Flower, "Blomster", PropRole.Decor, flower, proc.Bush, d =>
        {
            d.MaxDistance = 120f; d.MinScale = 0.8f; d.MaxScale = 1.3f; d.CastShadow = false;
        });
        var mush = ModelLibrary.Series(Nature + "Mushroom_Common.gltf", 0, 0, 0.45f);
        Def(PropKind.Mushroom, "Sopp", PropRole.Pickup, mush, proc.Stone, d =>
        {
            d.MaxDistance = 100f; d.MinScale = 0.8f; d.MaxScale = 1.3f; d.Item = "Sopp"; d.CastShadow = false;
        });

        // ---------- Stein
        var boulder = ModelLibrary.Series(Nature + "Rock_Medium_{0}.gltf", 1, 3, 0.75f);
        Def(PropKind.Boulder, "Steinblokk", PropRole.Rock, boulder, proc.Boulder, d =>
        {
            d.MaxDistance = 550f; d.MinScale = 0.6f; d.MaxScale = 1.6f; d.Sink = 0.15f;
            d.Tool = ToolKind.Pickaxe; d.Hp = 6; d.Item = "Stein"; d.ItemsOnHit = 1; d.ItemsOnBreak = 3;
        });
        var stone = ModelLibrary.Series(Nature + "Pebble_Round_{0}.gltf", 1, 5, 0.8f);
        stone.AddRange(ModelLibrary.Series(Nature + "Pebble_Square_{0}.gltf", 1, 6, 0.8f));
        Def(PropKind.Stone, "Stein", PropRole.Pickup, stone, proc.Stone, d =>
        {
            d.MaxDistance = 120f; d.MinScale = 0.8f; d.MaxScale = 1.3f; d.Item = "Stein"; d.CastShadow = false;
        });

        // ---------- Tre-ressurser (alltid kodegenerert, med barktekstur)
        Def(PropKind.Branch, "Grein", PropRole.Pickup, null, proc.Branch, d =>
        {
            d.MaxDistance = 120f; d.MinScale = 0.8f; d.MaxScale = 1.3f; d.Item = "Grein"; d.CastShadow = false;
        });
        Def(PropKind.Log, "Tømmerstokk", PropRole.Pickup, null, proc.Log, d => { d.Item = "Tømmer"; });

        MaxVariants = 1;
        foreach (var d in Defs) MaxVariants = Math.Max(MaxVariants, d.Variants.Length);
        GrassMesh = Procedural.BuildGrassMesh();
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

    /// <summary>Kodegenererte low-poly-mesher (reserve når assets mangler, og for greiner/stokker).</summary>
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

        public static ArrayMesh BuildGrassMesh()
        {
            var rng = new Random(99);
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var n = new List<Vector3>();
            for (int i = 0; i < 7; i++)
            {
                float a = (float)rng.NextDouble() * Mathf.Tau;
                var off = new Vector3((float)rng.NextDouble() - 0.5f, 0, (float)rng.NextDouble() - 0.5f) * 0.35f;
                var side = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 0.045f;
                var lean = new Vector3(Mathf.Sin(a), 0, -Mathf.Cos(a)) * ((float)rng.NextDouble() * 0.15f);
                float h = 0.35f + (float)rng.NextDouble() * 0.35f;
                var top = off + lean + new Vector3(0, h, 0);
                v.Add(off - side); uv.Add(new Vector2(0, 1));
                v.Add(off + side); uv.Add(new Vector2(1, 1));
                v.Add(top); uv.Add(new Vector2(0.5f, 0));
                for (int k = 0; k < 3; k++) n.Add(Vector3.Up);
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = n.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = uv.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(0, new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/grass.gdshader") });
            return mesh;
        }
    }
}
