using Godot;
using Heimskringla.Core;
using Heimskringla.UI;
using Heimskringla.Vegetation;
using Heimskringla.World;

namespace Heimskringla.Gameplay;

/// <summary>Tredjepersons spiller: bevegelse, svømming, flymodus, kamera, sikting og høsting.</summary>
public partial class Player : CharacterBody3D
{
    public const float WalkSpeed = 4.5f;
    public const float RunSpeed = 8.5f;
    public const float SwimSpeed = 3f;
    public const float JumpVelocity = 7.5f;
    public const float Gravity = 20f;
    public const float Reach = 3.3f;

    private Node3D _camPivot;
    private SpringArm3D _arm;
    private Camera3D _camera;
    private PlayerVisual _model;
    private float _yaw, _pitch = -0.25f;
    private float _modelYaw;
    private float _swing = -1f;
    private bool _hitDone;

    public bool Flying { get; private set; }
    public bool Swimming { get; private set; }
    public ToolKind Tool { get; private set; } = ToolKind.Axe;
    public GodotObject Target { get; private set; }
    public Camera3D Camera => _camera;
    public string VisualState => _model.DebugState + $" floor={IsOnFloor()} vel={Velocity.Length():F2}";
    public float CameraDistance { get => _arm.SpringLength; set => _arm.SpringLength = value; }
    public float CameraYaw { get => _yaw; set => _yaw = value; }

    public override void _Ready()
    {
        CollisionLayer = Game.LayerPlayer;
        CollisionMask = Game.LayerWorld;
        FloorMaxAngle = Mathf.DegToRad(50f);
        FloorSnapLength = 0.4f;

        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.85f },
            Position = new Vector3(0, 0.925f, 0),
        });
        _model = CharacterModel.Available ? new CharacterModel() : new PlayerModel();
        AddChild(_model);

        _camPivot = new Node3D { Position = new Vector3(0, 1.65f, 0) };
        AddChild(_camPivot);
        _arm = new SpringArm3D { SpringLength = 4.5f, CollisionMask = Game.LayerWorld, Margin = 0.2f, Position = new Vector3(0.45f, 0, 0) };
        _arm.Shape = new SphereShape3D { Radius = 0.2f };
        _arm.AddExcludedObject(GetRid());
        _camPivot.AddChild(_arm);
        _camera = new Camera3D { Far = 12000f, Near = 0.1f, Fov = 70f, Current = true };
        _arm.AddChild(_camera);

        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        if (e is InputEventMouseMotion mm && captured)
        {
            _yaw -= mm.Relative.X * 0.0025f;
            _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.0025f, -1.35f, 1.0f);
        }
        else if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (!captured)
            {
                if (mb.ButtonIndex == MouseButton.Left && !(Hud.Instance?.MapOpen ?? false))
                    Input.MouseMode = Input.MouseModeEnum.Captured;
                return;
            }
            if (mb.ButtonIndex == MouseButton.Left) StartSwing();
            else if (mb.ButtonIndex == MouseButton.WheelUp) _arm.SpringLength = Mathf.Max(1.5f, _arm.SpringLength - 0.5f);
            else if (mb.ButtonIndex == MouseButton.WheelDown) _arm.SpringLength = Mathf.Min(12f, _arm.SpringLength + 0.5f);
        }
        else if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            switch (k.PhysicalKeycode)
            {
                case Key.Escape: Input.MouseMode = Input.MouseModeEnum.Visible; break;
                case Key.E: TryInteract(); break;
                case Key.Key1: SetTool(ToolKind.Axe); break;
                case Key.Key2: SetTool(ToolKind.Pickaxe); break;
                case Key.Key3: SetTool(ToolKind.Hands); break;
                case Key.F: ToggleFly(); break;
            }
        }
    }

    private void SetTool(ToolKind t)
    {
        Tool = t;
        _model.SetTool(t);
        Hud.Instance?.RefreshHotbar(t);
    }

    public void ToggleFly()
    {
        Flying = !Flying;
        Velocity = Vector3.Zero;
        Hud.Instance?.Toast(Flying ? "Flymodus PÅ (Shift = raskere)" : "Flymodus AV");
    }

    public void Teleport(float x, float z)
    {
        var w = Game.World;
        float h = Mathf.Max(w.GetHeight(x, z), w.GetWaterLevel(x, z) - 1.2f);
        GlobalPosition = new Vector3(x, h + 1.0f, z);
        Game.Focus = GlobalPosition;
        Game.Collision?.UpdateAround(GlobalPosition, -1);
        Velocity = Vector3.Zero;
    }

    public void StartSwing()
    {
        if (_swing >= 0f || Flying || Swimming) return;
        _swing = 0f;
        _hitDone = false;
        _modelYaw = _yaw; // snu mot kameraretningen
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _camPivot.Rotation = new Vector3(_pitch, _yaw, 0);

        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        var input = Vector2.Zero;
        if (captured)
        {
            if (Input.IsPhysicalKeyPressed(Key.W)) input.Y -= 1;
            if (Input.IsPhysicalKeyPressed(Key.S)) input.Y += 1;
            if (Input.IsPhysicalKeyPressed(Key.A)) input.X -= 1;
            if (Input.IsPhysicalKeyPressed(Key.D)) input.X += 1;
        }
        input = input.LimitLength(1f);
        bool run = Input.IsPhysicalKeyPressed(Key.Shift);
        bool jump = captured && Input.IsPhysicalKeyPressed(Key.Space);
        var yawBasis = new Basis(Vector3.Up, _yaw);
        var dir = yawBasis * new Vector3(input.X, 0, input.Y);

        if (Flying)
        {
            var camBasis = _camera.GlobalBasis;
            var fly = camBasis * new Vector3(input.X, 0, input.Y);
            if (jump) fly += Vector3.Up;
            if (captured && Input.IsPhysicalKeyPressed(Key.Ctrl)) fly += Vector3.Down;
            float speed = run ? 220f : 40f;
            GlobalPosition += fly.LimitLength(1f) * speed * dt;
            float ground = Game.World.GetHeight(GlobalPosition.X, GlobalPosition.Z);
            if (GlobalPosition.Y < ground + 0.5f) GlobalPosition = new Vector3(GlobalPosition.X, ground + 0.5f, GlobalPosition.Z);
            Swimming = false;
            Animate(dt, 0f, false);
            Game.Focus = GlobalPosition;
            return;
        }

        var w = Game.World;
        float water = w.GetWaterLevel(GlobalPosition.X, GlobalPosition.Z);
        Swimming = water > WorldGenerator.NoWater && water > GlobalPosition.Y + 1.25f;
        var v = Velocity;

        float targetSpeed = Swimming ? SwimSpeed : (run ? RunSpeed : WalkSpeed);
        if (_swing >= 0f) targetSpeed *= 0.35f;
        var horiz = new Vector3(v.X, 0, v.Z);
        float accel = Swimming ? 4f : IsOnFloor() ? 14f : 3f;
        horiz = horiz.Lerp(dir * targetSpeed, Mathf.Clamp(accel * dt, 0f, 1f));

        if (Swimming)
        {
            float depth = water - (GlobalPosition.Y + 1.4f);
            v.Y += depth * 10f * dt;
            v.Y -= v.Y * 3f * dt;
            if (jump) v.Y = Mathf.Max(v.Y, 1.5f);
        }
        else
        {
            if (!IsOnFloor()) v.Y -= Gravity * dt;
            else if (jump && _swing < 0f) v.Y = JumpVelocity;
        }
        Velocity = new Vector3(horiz.X, v.Y, horiz.Z);
        MoveAndSlide();

        // Sikkerhetsnett hvis kollisjonen ikke var klar
        float ground2 = w.GetHeight(GlobalPosition.X, GlobalPosition.Z);
        if (GlobalPosition.Y < ground2 - 1.5f)
        {
            GlobalPosition = new Vector3(GlobalPosition.X, ground2 + 0.3f, GlobalPosition.Z);
            Velocity = Vector3.Zero;
        }

        // Modellen ser i bevegelsesretningen
        if (_swing < 0f && dir.LengthSquared() > 0.01f)
            _modelYaw = Mathf.Atan2(-dir.X, -dir.Z);
        _model.Rotation = new Vector3(0, Mathf.LerpAngle(_model.Rotation.Y, _modelYaw, Mathf.Clamp(12f * dt, 0f, 1f)), 0);

        if (_swing >= 0f)
        {
            _swing += dt / _model.SwingDuration;
            if (!_hitDone && _swing >= _model.HitMoment) { _hitDone = true; DoHit(); }
            if (_swing >= 1f) _swing = -1f;
        }

        Animate(dt, horiz.Length(), IsOnFloor());
        Game.Focus = GlobalPosition;
    }

    private void Animate(float dt, float speed, bool grounded) =>
        _model.Animate(speed, grounded || Flying, Swimming, _swing, dt);

    public override void _Process(double delta) => UpdateTarget();

    private void UpdateTarget()
    {
        Target = null;
        var space = GetWorld3D().DirectSpaceState;
        var from = _camera.GlobalPosition;
        var to = from - _camera.GlobalBasis.Z * (_arm.SpringLength + Reach + 2f);
        var q = PhysicsRayQueryParameters3D.Create(from, to, Game.LayerWorld | Game.LayerInteract, new Godot.Collections.Array<Rid> { GetRid() });
        var hit = space.IntersectRay(q);
        if (hit.Count > 0)
        {
            var col = hit["collider"].AsGodotObject();
            var pos = hit["position"].AsVector3();
            if ((col is IInteractable || col is IHittable) && pos.DistanceTo(GlobalPosition + new Vector3(0, 1f, 0)) < Reach + 0.8f)
                Target = col;
        }
        string prompt = Target switch
        {
            IInteractable i => i.Prompt,
            IHittable h => h.HitPrompt(Tool),
            _ => null,
        };
        Hud.Instance?.SetPrompt(prompt);
    }

    private void TryInteract()
    {
        if (Target is IInteractable i && IsInstanceValid((GodotObject)i))
        {
            _model.PlayPickup();
            i.Interact(this);
            return;
        }
        // Plukk opp det nærmeste innen rekkevidde om siktet bommer
        var near = QueryNear(1.6f, Game.LayerInteract);
        foreach (var o in near)
            if (o is IInteractable ii) { _model.PlayPickup(); ii.Interact(this); return; }
    }

    private void DoHit()
    {
        if (Tool == ToolKind.Hands) return;
        IHittable target = Target as IHittable;
        if (target == null || !IsInstanceValid((GodotObject)target))
        {
            target = null;
            foreach (var o in QueryNear(1.1f, Game.LayerInteract))
                if (o is IHittable h) { target = h; break; }
        }
        target?.Hit(this, Tool);
    }

    private System.Collections.Generic.List<GodotObject> QueryNear(float radius, uint mask)
    {
        var forward = new Basis(Vector3.Up, _model.Rotation.Y) * Vector3.Forward;
        var center = GlobalPosition + new Vector3(0, 1f, 0) + forward * 1.1f;
        var q = new PhysicsShapeQueryParameters3D
        {
            Shape = new SphereShape3D { Radius = radius },
            Transform = new Transform3D(Basis.Identity, center),
            CollisionMask = mask,
        };
        var res = GetWorld3D().DirectSpaceState.IntersectShape(q, 8);
        var list = new System.Collections.Generic.List<GodotObject>();
        foreach (var r in res) list.Add(r["collider"].AsGodotObject());
        return list;
    }
}
