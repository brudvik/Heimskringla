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
    string TargetName { get; }
    /// <summary>1 = intact, 0 = destroyed.</summary>
    float HealthFraction { get; }
}

public static class Interactables
{
    public static string ToolName(ToolKind t) => t switch
    {
        ToolKind.Axe => "øks",
        ToolKind.Pickaxe => "hakke",
        _ => "hender",
    };

    /// <summary>Creates a real node for a vegetation object the player is near.</summary>
    public static Node3D CreateForProp(PropDef def, PropInstance inst, VegetationManager owner)
    {
        PhysicsBody3D body = def.Role switch
        {
            PropRole.Tree => new TreeNode { Def = def, Inst = inst, Manager = owner },
            PropRole.Rock => new RockNode { Def = def, Inst = inst, Manager = owner },
            PropRole.Obstacle => new StaticBody3D(),
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
        else if (def.Role == PropRole.Obstacle)
        {
            var size = aabb.Size * s * 0.85f;
            shape = new BoxShape3D { Size = size };
            offset = aabb.GetCenter() * s;
            body.CollisionLayer = Game.LayerWorld;
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
    private float _hp = -1f;

    /// <summary>
    /// How many "chops" the tree withstands: the species' value scaled by size (a young tree falls in
    /// one or two chops, an old giant takes many).
    /// </summary>
    private float MaxHp
    {
        get
        {
            var v = Def.Variant(Inst.Variant);
            float typical = v.BaseScale * (Def.MinScale + Def.MaxScale) * 0.5f;
            return Mathf.Max(1f, Def.Hp * Mathf.Clamp(Inst.Scale / Mathf.Max(typical, 0.01f), 0.3f, 1.9f));
        }
    }

    public string HitPrompt(ToolKind tool) =>
        tool == ToolKind.Axe ? $"[Venstre mus] Hugg {Def.Name}" : $"{Def.Name} (trenger øks)";

    public void Hit(Player p, ToolKind tool)
    {
        if (tool != ToolKind.Axe)
        {
            Hud.Instance?.Toast("Du trenger en øks for å felle trær");
            return;
        }
        if (_hp < 0f) _hp = MaxHp;
        // Skill: a beginner deals 1 per chop, a skilled woodcutter up to 2.5, with some variation
        // (some chops land better than others)
        float skill = WorldState.Skill("Hogging");
        float dmg = (1f + skill / 66f) * (float)GD.RandRange(0.8, 1.2);
        _hp -= dmg;
        float before = Mathf.Floor(skill / 10f);
        float now = WorldState.Practice("Hogging", 0.8f);
        if (Mathf.Floor(now / 10f) > before) Hud.Instance?.Toast($"Hogging økte til {Mathf.FloorToInt(now)}");
        // Impact point on the trunk, on the side facing the player
        var toPlayer = p.GlobalPosition - GlobalPosition;
        toPlayer.Y = 0;
        toPlayer = toPlayer.LengthSquared() > 0.001f ? toPlayer.Normalized() : Vector3.Back;
        float r = Def.TrunkRadius * Inst.Scale;
        var hitPos = GlobalPosition + new Vector3(0, 1.0f, 0) + toPlayer * r;
        Effects.Burst(GetParent(), hitPos, new Color(0.82f, 0.68f, 0.45f), 14, 0.15f);     // wood chips
        Effects.Burst(GetParent(), GlobalPosition + new Vector3(0, Def.Variant(Inst.Variant).Aabb.End.Y * Inst.Scale * 0.75f, 0),
            new Color(0.35f, 0.55f, 0.2f), 10, 1.8f, 0.6f);                                   // falling leaves
        p.AddShake(0.28f);
        AddNotch(toPlayer, r);
        // The tree shakes (more the closer it is to falling)
        var mesh = GetNode<Node3D>("Mesh");
        float amp = 0.03f + 0.05f * (1f - HealthFraction);
        var tw = CreateTween();
        tw.TweenProperty(mesh, "rotation", new Vector3(-toPlayer.Z * amp, 0, toPlayer.X * amp), 0.05);
        tw.TweenProperty(mesh, "rotation", new Vector3(toPlayer.Z * amp * 0.5f, 0, -toPlayer.X * amp * 0.5f), 0.1);
        tw.TweenProperty(mesh, "rotation", Vector3.Zero, 0.2);
        if (_hp > 0f) return;

        Manager.OnHarvested(Inst);
        var dir = GlobalPosition - p.GlobalPosition;
        dir.Y = 0;
        dir = dir.LengthSquared() > 0.001f ? dir.Normalized() : Vector3.Forward;
        var fall = new FallingTree { Def = Def, Variant = Def.Variant(Inst.Variant), ScaleF = Inst.Scale, FallDir = dir, Watcher = p };
        GetParent().AddChild(fall);
        // Start a little above the stump so they do not collide
        fall.GlobalTransform = GlobalTransform.Translated(new Vector3(0, 0.55f * Inst.Scale, 0));
        Hud.Instance?.Toast($"{Def.Name} faller!");
        QueueFree();
    }

    public string TargetName => Def.Name;
    public float HealthFraction => _hp < 0f ? 1f : Mathf.Clamp(_hp / MaxHp, 0f, 1f);

    private MeshInstance3D _notch;
    private static StandardMaterial3D _notchMat;

    /// <summary>Visible notch in the trunk that grows with each hit.</summary>
    private void AddNotch(Vector3 toPlayer, float radius)
    {
        _notchMat ??= new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.72f, 0.5f), Roughness = 0.9f };
        if (_notch == null)
        {
            _notch = new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(1, 1, 1), LeftToRight = 0.5f, Material = _notchMat } };
            AddChild(_notch);
        }
        float depth = radius * (0.35f + 0.9f * (1f - HealthFraction));
        _notch.GlobalPosition = GlobalPosition + new Vector3(0, 1.0f, 0) + toPlayer * (radius - depth * 0.45f);
        _notch.GlobalBasis = Basis.LookingAt(-toPlayer, Vector3.Up) * new Basis(Vector3.Right, Mathf.Pi / 2f);
        _notch.Scale = new Vector3(radius * 1.3f, depth, 0.28f);
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
        var top = GlobalPosition + new Vector3(0, Def.Variant(Inst.Variant).Aabb.End.Y * Inst.Scale * 0.7f, 0);
        Effects.Burst(GetParent(), top, new Color(0.55f, 0.54f, 0.5f), 16, 0.4f);
        Effects.Burst(GetParent(), top, new Color(1f, 0.85f, 0.5f), 5, 0.2f, 0.3f);      // sparks
        p.AddShake(0.22f);
        int got = Def.ItemsOnHit + (_hp <= 0 ? Def.ItemsOnBreak : 0);
        Inventory.Add(Def.Item, got);
        // The rock shrinks and shakes with each hit
        var mesh = GetNode<Node3D>("Mesh");
        var baseScale = Vector3.One * Inst.Scale * (0.55f + 0.45f * HealthFraction);
        var tw = CreateTween();
        tw.TweenProperty(mesh, "scale", baseScale * 0.94f, 0.05);
        tw.TweenProperty(mesh, "scale", baseScale, 0.12);
        if (_hp > 0) return;
        Effects.Burst(GetParent(), GlobalPosition + new Vector3(0, 0.5f, 0), new Color(0.6f, 0.58f, 0.54f), 40, 1.2f);
        Manager.OnHarvested(Inst);
        QueueFree();
    }

    public string TargetName => Def.Name;
    public float HealthFraction => _hp < 0 ? 1f : Mathf.Clamp(_hp / (float)Def.Hp, 0f, 1f);
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
        Manager.OnHarvested(Inst);
        QueueFree();
    }
}

/// <summary>Loose item with physics (log or branch from a felled tree).</summary>
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
        QueueFree();
    }
}

/// <summary>Tree that falls after felling and turns into logs.</summary>
public partial class FallingTree : RigidBody3D
{
    public PropDef Def;
    public ModelVariant Variant;
    public float ScaleF = 1f;
    public Vector3 FallDir = Vector3.Forward;
    public Player Watcher;
    private float _t;
    private bool _pushed;
    private bool _landed;
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
            // A small push; gravity does the rest (the tree accelerates as it falls)
            ApplyImpulse(FallDir * Mass * 0.22f, GlobalBasis * new Vector3(0, h * 0.9f, 0));
        }
        // Impact: when the tree is lying almost flat
        if (!_landed && _t > 0.5f && GlobalBasis.Y.Normalized().Y < 0.35f)
        {
            _landed = true;
            var lAxis = GlobalBasis.Y.Normalized();
            Effects.Burst(GetParent(), GlobalPosition + lAxis * TrunkLength * 0.6f, new Color(0.55f, 0.5f, 0.4f), 40, TrunkLength * 0.3f, 1.2f);
            Effects.Burst(GetParent(), GlobalPosition + lAxis * TrunkLength * 0.85f, new Color(0.3f, 0.5f, 0.2f), 40, 2.5f, 1.0f);
            if (Watcher != null && IsInstanceValid(Watcher))
            {
                float d = Watcher.GlobalPosition.DistanceTo(GlobalPosition + lAxis * TrunkLength * 0.5f);
                Watcher.AddShake(Mathf.Clamp(0.7f - d / 40f, 0f, 0.6f));
            }
        }
        if (_t < (_landed ? 3.4f : 6f)) return;

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

    /// <summary>Small particle cloud (wood chips, rock splinters, leaves).</summary>
    public static void Burst(Node parent, Vector3 pos, Color color, int amount, float spread = 0.6f, float speed = 1f)
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
            InitialVelocityMin = 1.5f * speed,
            InitialVelocityMax = 4f * speed,
            Gravity = new Vector3(0, -9.8f, 0),
            Color = color,
        };
        parent.AddChild(p);
        p.GlobalPosition = pos;
        p.Emitting = true;
        p.GetTree().CreateTimer(1.6).Timeout += p.QueueFree;
    }
}
