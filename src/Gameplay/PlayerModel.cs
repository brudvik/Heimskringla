using Godot;
using Heimskringla.Vegetation;

namespace Heimskringla.Gameplay;

/// <summary>
/// Stilisert viking bygget av enkle former, med prosedyrisk animasjon (gange, løp, hugg, svømming).
/// Kan byttes ut med en rigget CC0-figur senere; Player bruker bare Animate() og SetTool().
/// Modellen ser mot -Z.
/// </summary>
public partial class PlayerModel : PlayerVisual
{
    private Node3D _legL, _legR, _armL, _armR, _torso, _toolHolder;
    private Node3D _axe, _pick;
    private float _phase;

    public override void _Ready()
    {
        var skin = Mat(new Color(0.86f, 0.67f, 0.54f));
        var tunic = Mat(new Color(0.30f, 0.40f, 0.55f));
        var trousers = Mat(new Color(0.42f, 0.35f, 0.25f));
        var boots = Mat(new Color(0.24f, 0.16f, 0.10f));
        var belt = Mat(new Color(0.18f, 0.12f, 0.07f));
        var hair = Mat(new Color(0.74f, 0.52f, 0.24f));
        var metal = Mat(new Color(0.58f, 0.59f, 0.62f), 0.7f, 0.35f);
        var dark = Mat(new Color(0.08f, 0.07f, 0.07f));
        var wood = Mat(new Color(0.45f, 0.32f, 0.19f));

        _legL = Pivot(this, new Vector3(-0.12f, 0.95f, 0));
        _legR = Pivot(this, new Vector3(0.12f, 0.95f, 0));
        foreach (var leg in new[] { _legL, _legR })
        {
            Box(leg, new Vector3(0.17f, 0.56f, 0.2f), new Vector3(0, -0.3f, 0), trousers);
            Box(leg, new Vector3(0.19f, 0.32f, 0.28f), new Vector3(0, -0.79f, -0.03f), boots);
        }

        _torso = Pivot(this, new Vector3(0, 0.95f, 0));
        Box(_torso, new Vector3(0.48f, 0.62f, 0.30f), new Vector3(0, 0.36f, 0), tunic);
        Box(_torso, new Vector3(0.53f, 0.22f, 0.33f), new Vector3(0, 0.0f, 0), tunic);
        Box(_torso, new Vector3(0.50f, 0.07f, 0.32f), new Vector3(0, 0.09f, 0), belt);
        // Hode
        var head = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.16f, Height = 0.32f, Material = skin }, Position = new Vector3(0, 0.83f, 0) };
        _torso.AddChild(head);
        Box(_torso, new Vector3(0.25f, 0.22f, 0.1f), new Vector3(0, 0.71f, -0.12f), hair);   // skjegg
        Box(_torso, new Vector3(0.30f, 0.18f, 0.12f), new Vector3(0, 0.80f, 0.1f), hair);   // hår bak
        Box(_torso, new Vector3(0.035f, 0.025f, 0.02f), new Vector3(-0.055f, 0.86f, -0.152f), dark);
        Box(_torso, new Vector3(0.035f, 0.025f, 0.02f), new Vector3(0.055f, 0.86f, -0.152f), dark);
        // Konisk hjelm med nesebeskytter
        _torso.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.03f, BottomRadius = 0.175f, Height = 0.2f, RadialSegments = 10, Material = metal },
            Position = new Vector3(0, 0.98f, 0),
        });
        _torso.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.178f, BottomRadius = 0.178f, Height = 0.05f, RadialSegments = 10, Material = metal },
            Position = new Vector3(0, 0.89f, 0),
        });
        Box(_torso, new Vector3(0.03f, 0.13f, 0.03f), new Vector3(0, 0.84f, -0.17f), metal);

        _armL = Pivot(_torso, new Vector3(-0.31f, 0.6f, 0));
        _armR = Pivot(_torso, new Vector3(0.31f, 0.6f, 0));
        foreach (var arm in new[] { _armL, _armR })
        {
            Box(arm, new Vector3(0.13f, 0.36f, 0.14f), new Vector3(0, -0.17f, 0), tunic);
            Box(arm, new Vector3(0.11f, 0.30f, 0.12f), new Vector3(0, -0.48f, 0), skin);
        }

        _toolHolder = Pivot(_armR, new Vector3(0, -0.62f, -0.02f));
        _axe = new Node3D();
        Box(_axe, new Vector3(0.04f, 0.04f, 0.8f), new Vector3(0, 0, -0.28f), wood);
        Box(_axe, new Vector3(0.025f, 0.2f, 0.13f), new Vector3(0, -0.07f, -0.62f), metal);
        _toolHolder.AddChild(_axe);
        _pick = new Node3D();
        Box(_pick, new Vector3(0.04f, 0.04f, 0.85f), new Vector3(0, 0, -0.3f), wood);
        Box(_pick, new Vector3(0.04f, 0.5f, 0.05f), new Vector3(0, 0, -0.68f), metal);
        _toolHolder.AddChild(_pick);
        SetTool(ToolKind.Axe);
    }

    public override void SetTool(ToolKind tool)
    {
        _axe.Visible = tool == ToolKind.Axe;
        _pick.Visible = tool == ToolKind.Pickaxe;
    }

    /// <param name="speed">horisontal fart (m/s)</param>
    /// <param name="swing">0..1 under hugg, ellers negativ</param>
    public override void Animate(float speed, bool grounded, bool swimming, float swing, float dt)
    {
        _phase += dt * (2.2f + speed * 1.1f);
        float amount = Mathf.Clamp(speed / 4.5f, 0f, 1.3f);
        float legSwing = Mathf.Sin(_phase) * 0.65f * amount;

        if (swimming)
        {
            float s = Mathf.Sin(_phase * 0.8f);
            _torso.Rotation = new Vector3(-0.9f, 0, 0);
            _legL.Rotation = new Vector3(-0.6f + s * 0.4f, 0, 0);
            _legR.Rotation = new Vector3(-0.6f - s * 0.4f, 0, 0);
            _armL.Rotation = new Vector3(2.4f + s * 0.8f, 0, 0.3f);
            _armR.Rotation = new Vector3(2.4f - s * 0.8f, 0, -0.3f);
            return;
        }

        if (!grounded)
        {
            _legL.Rotation = new Vector3(0.5f, 0, 0);
            _legR.Rotation = new Vector3(-0.2f, 0, 0);
        }
        else
        {
            _legL.Rotation = new Vector3(legSwing, 0, 0);
            _legR.Rotation = new Vector3(-legSwing, 0, 0);
        }
        float bob = grounded ? Mathf.Abs(Mathf.Sin(_phase)) * 0.04f * amount : 0f;
        _torso.Position = new Vector3(0, 0.95f + bob, 0);
        _torso.Rotation = new Vector3(-0.12f * amount, 0, 0);
        _armL.Rotation = new Vector3(-legSwing * 0.8f, 0, 0.08f);

        if (swing >= 0f)
        {
            // Løft over hodet, så hugg ned foran
            float a;
            if (swing < 0.4f) a = Mathf.Lerp(0f, 2.9f, Ease(swing / 0.4f));
            else if (swing < 0.62f) a = Mathf.Lerp(2.9f, 0.45f, (swing - 0.4f) / 0.22f);
            else a = Mathf.Lerp(0.45f, 0f, (swing - 0.62f) / 0.38f);
            _armR.Rotation = new Vector3(a, 0, -0.1f);
            _armL.Rotation = new Vector3(a * 0.7f, 0, 0.25f);
            _torso.Rotation = new Vector3(-0.12f * amount + (a > 1.5f ? 0.1f : -0.15f), 0, 0);
        }
        else
        {
            _armR.Rotation = new Vector3(legSwing * 0.8f + 0.25f, 0, -0.08f);
        }
    }

    private static float Ease(float t) => 1f - (1f - t) * (1f - t);

    private static StandardMaterial3D Mat(Color c, float metallic = 0f, float roughness = 0.85f) =>
        new() { AlbedoColor = c, Metallic = metallic, Roughness = roughness };

    private static Node3D Pivot(Node3D parent, Vector3 pos)
    {
        var n = new Node3D { Position = pos };
        parent.AddChild(n);
        return n;
    }

    private static void Box(Node3D parent, Vector3 size, Vector3 pos, Material mat)
    {
        parent.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size, Material = mat }, Position = pos });
    }
}
