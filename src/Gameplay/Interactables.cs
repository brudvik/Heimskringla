using Godot;
using Heimskringla.Core;
using Heimskringla.UI;
using Heimskringla.Vegetation;

namespace Heimskringla.Gameplay;

public interface IInteractable
{
    string Prompt { get; }
    void Interact(Player p);
}

public interface IHittable
{
    string HitPrompt(ToolKind tool);
    void Hit(Player p, ToolKind tool);
}

public static class Interactables
{
    public static string ToolName(ToolKind t) => t switch
    {
        ToolKind.Axe => "øks",
        ToolKind.Pickaxe => "hakke",
        _ => "hender",
    };

    /// <summary>Lager en ekte node for et vegetasjonsobjekt som spilleren er nær.</summary>
    public static Node3D CreateForProp(PropDef def, PropInstance inst, VegetationManager owner)
    {
        PhysicsBody3D body = def.Role switch
        {
            PropRole.Tree => new TreeNode { Def = def, Inst = inst, Manager = owner },
            PropRole.Rock => new RockNode { Def = def, Inst = inst, Manager = owner },
            _ => new PropPickup { Def = def, Inst = inst, Manager = owner },
        };
        body.Position = inst.Pos;
        body.Rotation = new Vector3(0, inst.Yaw, 0);
        var variant = def.Variant(inst.Variant);
        body.AddChild(new MeshInstance3D
        {
            Name = "Mesh",
            Mesh = variant.Mesh,
            Scale = Vector3.One * inst.Scale,
        });
        float s = inst.Scale;
        var aabb = variant.Aabb;
        Shape3D shape;
        Vector3 offset;
        if (def.Role == PropRole.Tree)
        {
            float h = aabb.End.Y * s * 0.7f;
            shape = new CylinderShape3D { Radius = def.TrunkRadius * s, Height = h };
            offset = new Vector3(0, h * 0.5f, 0);
            body.CollisionLayer = Game.LayerWorld | Game.LayerInteract;
        }
        else if (def.Role == PropRole.Rock)
        {
            float r = Mathf.Min(aabb.Size.X, aabb.Size.Z) * 0.42f * s;
            shape = new SphereShape3D { Radius = r };
            offset = new Vector3(aabb.GetCenter().X * s, Mathf.Max(aabb.End.Y * s - r, 0.2f), aabb.GetCenter().Z * s);
            body.CollisionLayer = Game.LayerWorld | Game.LayerInteract;
        }
        else
        {
            shape = new SphereShape3D { Radius = 0.4f };
            offset = new Vector3(0, 0.15f, 0);
            body.CollisionLayer = Game.LayerInteract;
        }
        body.CollisionMask = 0;
        body.AddChild(new CollisionShape3D { Shape = shape, Position = offset });
        return body;
    }
}

public partial class TreeNode : StaticBody3D, IHittable
{
    public PropDef Def;
    public PropInstance Inst;
    public VegetationManager Manager;
    private int _hp = -1;

    public string HitPrompt(ToolKind tool) =>
        tool == ToolKind.Axe ? $"[Venstre mus] Hugg {Def.Name}" : $"{Def.Name} (trenger øks)";

    public void Hit(Player p, ToolKind tool)
    {
        if (tool != ToolKind.Axe)
        {
            Hud.Instance?.Toast("Du trenger en øks for å felle trær");
            return;
        }
        if (_hp < 0) _hp = Def.Hp;
        _hp--;
        var top = GlobalPosition + new Vector3(0, 1.2f, 0);
        Effects.Burst(GetParent(), top, new Color(0.75f, 0.6f, 0.4f), 10);
        var mesh = GetNode<Node3D>("Mesh");
        var tw = CreateTween();
        tw.TweenProperty(mesh, "rotation", new Vector3(0.04f, 0, 0.03f), 0.05);
        tw.TweenProperty(mesh, "rotation", Vector3.Zero, 0.15);
        if (_hp > 0) return;

        Manager.OnHarvested(Inst);
        var dir = GlobalPosition - p.GlobalPosition;
        dir.Y = 0;
        dir = dir.LengthSquared() > 0.001f ? dir.Normalized() : Vector3.Forward;
        var fall = new FallingTree { Def = Def, Variant = Def.Variant(Inst.Variant), ScaleF = Inst.Scale, FallDir = dir };
        GetParent().AddChild(fall);
        fall.GlobalTransform = GlobalTransform;
        Hud.Instance?.Toast($"{Def.Name} faller!");
        QueueFree();
    }
}

public partial class RockNode : StaticBody3D, IHittable
{
    public PropDef Def;
    public PropInstance Inst;
    public VegetationManager Manager;
    private int _hp = -1;

    public string HitPrompt(ToolKind tool) =>
        tool == ToolKind.Pickaxe ? $"[Venstre mus] Hakk {Def.Name}" : $"{Def.Name} (trenger hakke)";

    public void Hit(Player p, ToolKind tool)
    {
        if (tool != ToolKind.Pickaxe)
        {
            Hud.Instance?.Toast("Du trenger en hakke for å hakke stein");
            return;
        }
        if (_hp < 0) _hp = Def.Hp;
        _hp--;
        Effects.Burst(GetParent(), GlobalPosition + new Vector3(0, Def.Variant(Inst.Variant).Aabb.End.Y * Inst.Scale * 0.7f, 0), new Color(0.55f, 0.54f, 0.5f), 12);
        int got = Def.ItemsOnHit + (_hp <= 0 ? Def.ItemsOnBreak : 0);
        Inventory.Add(Def.Item, got);
        Hud.Instance?.Toast($"+{got} {Def.Item}");
        if (_hp > 0) return;
        Manager.OnHarvested(Inst);
        QueueFree();
    }
}

public partial class PropPickup : StaticBody3D, IInteractable
{
    public PropDef Def;
    public PropInstance Inst;
    public VegetationManager Manager;

    public string Prompt => $"[E] Plukk opp {Def.Name.ToLowerInvariant()}";

    public void Interact(Player p)
    {
        Inventory.Add(Def.Item, 1);
        Hud.Instance?.Toast($"+1 {Def.Item}");
        Manager.OnHarvested(Inst);
        QueueFree();
    }
}

/// <summary>Løs gjenstand med fysikk (tømmerstokk eller grein fra et felt tre).</summary>
public partial class LooseItem : RigidBody3D, IInteractable
{
    public PropDef Def;
    private float _check;

    public string Prompt => $"[E] Plukk opp {Def.Name.ToLowerInvariant()}";

    public override void _Ready()
    {
        CollisionLayer = Game.LayerInteract;
        CollisionMask = Game.LayerWorld;
        Mass = Def.Kind == PropKind.Log ? 40f : 2f;
        AddChild(new MeshInstance3D { Mesh = Def.Variants[0].Mesh });
        if (Def.Kind == PropKind.Log)
            AddChild(new CollisionShape3D
            {
                Shape = new CylinderShape3D { Radius = 0.24f, Height = 1.8f },
                Rotation = new Vector3(0, 0, Mathf.Pi / 2f),
            });
        else
            AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.3f, 0.12f, 0.3f) }, Position = new Vector3(0, 0.06f, 0) });
    }

    public override void _PhysicsProcess(double delta)
    {
        _check += (float)delta;
        if (_check < 1f) return;
        _check = 0;
        float d = GlobalPosition.DistanceTo(Game.Focus);
        Freeze = d > 90f;
        if (d > 160f) QueueFree();
    }

    public void Interact(Player p)
    {
        Inventory.Add(Def.Item, 1);
        Hud.Instance?.Toast($"+1 {Def.Item}");
        QueueFree();
    }
}

/// <summary>Tre som faller etter felling, og blir til tømmerstokker.</summary>
public partial class FallingTree : RigidBody3D
{
    public PropDef Def;
    public ModelVariant Variant;
    public float ScaleF = 1f;
    public Vector3 FallDir = Vector3.Forward;
    private float _t;
    private bool _pushed;
    private float TrunkLength => Variant.Aabb.End.Y * ScaleF * 0.75f;

    public override void _Ready()
    {
        CollisionLayer = 0;
        CollisionMask = Game.LayerWorld;
        Mass = 300f;
        AngularDamp = 0.5f;
        AddChild(new MeshInstance3D { Mesh = Variant.Mesh, Scale = Vector3.One * ScaleF });
        float h = TrunkLength, r = Mathf.Max(Def.TrunkRadius * ScaleF, 0.25f);
        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = r, Height = Mathf.Max(h, r * 2.1f) },
            Position = new Vector3(0, h * 0.5f + 0.1f, 0),
        });
    }

    public override void _PhysicsProcess(double delta)
    {
        _t += (float)delta;
        if (!_pushed)
        {
            _pushed = true;
            float h = TrunkLength;
            ApplyImpulse(FallDir * Mass * 1.2f, GlobalBasis * new Vector3(0, h * 0.8f, 0));
        }
        if (_t < 3.2f) return;

        var axis = GlobalBasis.Y.Normalized();
        var basePos = GlobalPosition;
        float len = TrunkLength;
        int logs = Def.LogsOnFell;
        float yaw = Mathf.Atan2(-axis.Z, axis.X);
        for (int i = 0; i < logs; i++)
        {
            var pos = basePos + axis * (len * (i + 0.6f) / (logs + 0.4f)) + new Vector3(0, 0.5f, 0);
            Spawn(PropKind.Log, pos, yaw);
        }
        for (int i = 0; i < Def.BranchesOnFell; i++)
        {
            var pos = basePos + axis * (len * 0.85f) + new Vector3(i * 0.6f - 0.3f, 0.6f, 0.4f);
            Spawn(PropKind.Branch, pos, yaw + 1.2f * i);
        }
        Effects.Burst(GetParent(), basePos + axis * len * 0.7f, new Color(0.3f, 0.5f, 0.2f), 30, 2.5f);
        QueueFree();
    }

    private void Spawn(PropKind kind, Vector3 pos, float yaw)
    {
        var item = new LooseItem { Def = PropLibrary.Get(kind) };
        GetParent().AddChild(item);
        item.GlobalPosition = pos;
        item.Rotation = new Vector3(0, yaw, 0);
    }
}

public static class Effects
{
    private static StandardMaterial3D _mat;

    /// <summary>Liten partikkelsky (flis, steinsprut, løv).</summary>
    public static void Burst(Node parent, Vector3 pos, Color color, int amount, float spread = 0.6f)
    {
        _mat ??= new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex };
        var p = new CpuParticles3D
        {
            Emitting = false,
            OneShot = true,
            Amount = amount,
            Lifetime = 0.9f,
            Explosiveness = 0.95f,
            Mesh = new BoxMesh { Size = new Vector3(0.07f, 0.07f, 0.07f), Material = _mat },
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = spread,
            Direction = Vector3.Up,
            Spread = 70f,
            InitialVelocityMin = 1.5f,
            InitialVelocityMax = 4f,
            Gravity = new Vector3(0, -9.8f, 0),
            Color = color,
        };
        parent.AddChild(p);
        p.GlobalPosition = pos;
        p.Emitting = true;
        p.GetTree().CreateTimer(1.6).Timeout += p.QueueFree;
    }
}
