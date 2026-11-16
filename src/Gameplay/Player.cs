using Godot;
using Heimskringla.Core;
using Heimskringla.UI;
using Heimskringla.Vegetation;
using Heimskringla.World;

namespace Heimskringla.Gameplay;

/// <summary>
/// Tredjepersons spiller: bevegelse (bakker, skliing, trinn, utholdenhet), svømming, flymodus,
/// mykt kamera med risting, sikting og høsting.
/// </summary>
public partial class Player : CharacterBody3D
{
    public const float WalkSpeed = 2.2f;     // Alt: rolig gange
    public const float JogSpeed = 4.6f;      // standard
    public const float RunSpeed = 8.2f;      // Shift (bruker utholdenhet)
    public const float SwimSpeed = 2.6f;
    public const float SwimFastSpeed = 4.2f;
    public const float JumpVelocity = 7.2f;
    public const float Gravity = 20f;
    public const float Reach = 3.3f;
    public const float MaxStamina = 100f;
    private const float SlideAngle = 40f;    // grader: brattere enn dette sklir man
    private const float StepHeight = 0.45f;

    private Node3D _camPivot;
    private SpringArm3D _arm;
    private Camera3D _camera;
    private PlayerVisual _model;
    private float _yaw, _pitch = -0.25f;
    private float _modelYaw;
    private float _swing = -1f;
    private bool _hitDone;
    private float _staminaDelay;
    private float _shake;
    private float _shakeT;
    private float _armTarget = 4.5f;
    private bool _wasOnFloor = true;
    private float _fallSpeed;

    public bool Flying { get; private set; }
    public bool Swimming { get; private set; }
    public bool Sliding { get; private set; }
    public float Stamina { get; private set; } = MaxStamina;
    public bool Exhausted { get; private set; }
    public ToolKind Tool { get; private set; } = ToolKind.Axe;
    public GodotObject Target { get; private set; }
    public Camera3D Camera => _camera;
    public PlayerVisual Visual => _model;
    public string VisualState => _model.DebugState + $" floor={IsOnFloor()} vel={Velocity.Length():F2}";
    public float CameraDistance { get => _armTarget; set { _armTarget = value; if (_arm != null) _arm.SpringLength = value; } }
    public float CameraYaw { get => _yaw; set => _yaw = value; }
    public float CameraPitch { get => _pitch; set => _pitch = value; }
    /// <summary>Testhjelp (autotest): simulert WASD-input når satt.</summary>
    public Vector2? DebugInput;

    public override void _Ready()
    {
        CollisionLayer = Game.LayerPlayer;
        CollisionMask = Game.LayerWorld;
        FloorMaxAngle = Mathf.DegToRad(SlideAngle + 8f);
        FloorSnapLength = 0.5f;
        FloorConstantSpeed = true;          // samme fart opp og ned bakker (vi styrer selv)
        FloorBlockOnWall = true;

        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.85f },
            Position = new Vector3(0, 0.925f, 0),
        });
        _model = CharacterModel.Available ? new CharacterModel() : new PlayerModel();
        _model.OwnerRid = GetRid();
        AddChild(_model);
        _model.Footstep += (side, pos, yaw) => Footprints.OnStep(GetParent(), side, pos, yaw);

        // Kamerapivoten følger spilleren mykt (TopLevel), så ujevnt terreng ikke rister bildet
        _camPivot = new Node3D { TopLevel = true };
        AddChild(_camPivot);
        _camPivot.GlobalPosition = GlobalPosition + new Vector3(0, 1.65f, 0);
        _arm = new SpringArm3D { SpringLength = _armTarget, CollisionMask = Game.LayerWorld, Margin = 0.2f, Position = new Vector3(0.45f, 0, 0) };
        _arm.Shape = new SphereShape3D { Radius = 0.2f };
        _arm.AddExcludedObject(GetRid());
        _camPivot.AddChild(_arm);
        _camera = new Camera3D { Far = 12000f, Near = 0.1f, Fov = 70f, Current = true };
        _arm.AddChild(_camera);

        if (!Game.AutoTest) Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Game.AutoTest) return;
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
            else if (mb.ButtonIndex == MouseButton.WheelUp) _armTarget = Mathf.Max(1.5f, _armTarget - 0.5f);
            else if (mb.ButtonIndex == MouseButton.WheelDown) _armTarget = Mathf.Min(12f, _armTarget + 0.5f);
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
                case Key.B: BuildCampfire(); break;
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
        if (_camPivot != null) _camPivot.GlobalPosition = GlobalPosition + new Vector3(0, 1.65f, 0);
    }

    public void StartSwing()
    {
        if (_swing >= 0f || Flying || Swimming) return;
        if (Tool != ToolKind.Hands && Stamina < 6f) { Hud.Instance?.Toast("For sliten til å hugge"); return; }
        if (Tool != ToolKind.Hands) UseStamina(6f);
        _swing = 0f;
        _hitDone = false;
        _modelYaw = _yaw; // snu mot kameraretningen
    }

    /// <summary>Kamerarist (0..1). Avtar av seg selv.</summary>
    public void AddShake(float amount) => _shake = Mathf.Min(1f, _shake + amount);

    private void UseStamina(float amount)
    {
        Stamina = Mathf.Max(0f, Stamina - amount);
        _staminaDelay = 1.0f;
        if (Stamina <= 0.01f) Exhausted = true;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        var input = Vector2.Zero;
        if (captured)
        {
            if (Input.IsPhysicalKeyPressed(Key.W)) input.Y -= 1;
            if (Input.IsPhysicalKeyPressed(Key.S)) input.Y += 1;
            if (Input.IsPhysicalKeyPressed(Key.A)) input.X -= 1;
            if (Input.IsPhysicalKeyPressed(Key.D)) input.X += 1;
        }
        if (DebugInput.HasValue) input = DebugInput.Value;
        input = input.LimitLength(1f);
        bool runKey = captured && Input.IsPhysicalKeyPressed(Key.Shift);
        bool walkKey = captured && Input.IsPhysicalKeyPressed(Key.Alt);
        bool jump = captured && Input.IsPhysicalKeyPressed(Key.Space);
        var yawBasis = new Basis(Vector3.Up, _yaw);
        var dir = yawBasis * new Vector3(input.X, 0, input.Y);

        if (Flying)
        {
            var camBasis = _camera.GlobalBasis;
            var fly = camBasis * new Vector3(input.X, 0, input.Y);
            if (jump) fly += Vector3.Up;
            if (captured && Input.IsPhysicalKeyPressed(Key.Ctrl)) fly += Vector3.Down;
            float speed = runKey ? 220f : 40f;
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
        bool onFloor = IsOnFloor();
        var v = Velocity;
        bool moving = dir.LengthSquared() > 0.01f;

        // --- Utholdenhet
        bool sprinting = runKey && moving && !Exhausted && _swing < 0f && (onFloor || Swimming);
        if (sprinting) UseStamina((Swimming ? 14f : 11f) * dt);
        else if (Swimming && moving) UseStamina(3f * dt);
        if (_staminaDelay > 0f) _staminaDelay -= dt;
        else Stamina = Mathf.Min(MaxStamina, Stamina + (Swimming ? 4f : moving ? 14f : 22f) * dt);
        if (Exhausted && Stamina > MaxStamina * 0.3f) Exhausted = false;

        // --- Ønsket fart
        float targetSpeed = Swimming ? (sprinting ? SwimFastSpeed : SwimSpeed)
                          : sprinting ? RunSpeed : walkKey ? WalkSpeed : JogSpeed;
        if (Exhausted && !Swimming) targetSpeed = Mathf.Min(targetSpeed, JogSpeed * 0.75f);
        if (_swing >= 0f) targetSpeed *= 0.35f;

        // --- Bakker: saktere oppover, litt raskere nedover; for bratt = skli
        Sliding = false;
        if (onFloor && !Swimming)
        {
            var fn = GetFloorNormal();
            float angle = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(fn.Y, -1f, 1f)));
            var downhill = new Vector3(fn.X, 0, fn.Z);
            if (moving && downhill.LengthSquared() > 1e-4f)
            {
                float along = dir.Normalized().Dot(downhill.Normalized());   // >0 = nedover
                float steep = Mathf.Clamp(angle / SlideAngle, 0f, 1f);
                targetSpeed *= along < 0f ? Mathf.Lerp(1f, 0.45f, steep * -along) : Mathf.Lerp(1f, 1.15f, steep * along);
            }
            if (angle > SlideAngle)
            {
                Sliding = true;
                var slide = downhill.Normalized() * (angle - SlideAngle) * 0.35f;
                v += slide * dt * 10f;
            }
        }

        var horiz = new Vector3(v.X, 0, v.Z);
        float accel = Swimming ? 3.5f : onFloor ? (Sliding ? 2f : 12f) : 2.5f;
        var wanted = dir * targetSpeed;
        if (Sliding) wanted += new Vector3(v.X, 0, v.Z) * 0.8f;
        horiz = horiz.Lerp(wanted, Mathf.Clamp(accel * dt, 0f, 1f));

        if (Swimming)
        {
            float depth = water - (GlobalPosition.Y + 1.4f);
            v.Y += depth * 10f * dt;
            v.Y -= v.Y * 3f * dt;
            if (Exhausted) v.Y -= 1.5f * dt;   // sliten: synker litt
            if (jump && !Exhausted) v.Y = Mathf.Max(v.Y, 1.5f);
        }
        else
        {
            if (!onFloor) v.Y -= Gravity * dt;
            else if (jump && _swing < 0f && !Sliding && Stamina >= 8f)
            {
                v.Y = JumpVelocity;
                UseStamina(8f);
            }
        }
        if (!onFloor) _fallSpeed = Mathf.Max(_fallSpeed, -v.Y);
        Velocity = new Vector3(horiz.X, v.Y, horiz.Z);
        var before = GlobalPosition;
        MoveAndSlide();
        TryStepUp(before, horiz, dt);

        // Landing: rist kameraet ved hardt fall
        if (IsOnFloor() && !_wasOnFloor && _fallSpeed > 9f) AddShake(Mathf.Clamp((_fallSpeed - 9f) / 12f, 0f, 0.6f));
        if (IsOnFloor()) _fallSpeed = 0f;
        _wasOnFloor = IsOnFloor();

        // Sikkerhetsnett hvis kollisjonen ikke var klar
        float ground2 = w.GetHeight(GlobalPosition.X, GlobalPosition.Z);
        if (GlobalPosition.Y < ground2 - 1.5f)
        {
            GlobalPosition = new Vector3(GlobalPosition.X, ground2 + 0.3f, GlobalPosition.Z);
            Velocity = Vector3.Zero;
        }

        // Modellen ser i bevegelsesretningen
        if (_swing < 0f && moving)
            _modelYaw = Mathf.Atan2(-dir.X, -dir.Z);
        _model.Rotation = new Vector3(0, Mathf.LerpAngle(_model.Rotation.Y, _modelYaw, Mathf.Clamp(12f * dt, 0f, 1f)), 0);

        if (_swing >= 0f)
        {
            _swing += dt / _model.SwingDuration;
            if (!_hitDone && _swing >= _model.HitMoment) { _hitDone = true; DoHit(); }
            if (_swing >= 1f) _swing = -1f;
        }

        Animate(dt, new Vector3(Velocity.X, 0, Velocity.Z).Length(), IsOnFloor() || Sliding);
        Game.Focus = GlobalPosition;
    }

    /// <summary>Klatrer opp små kanter (steiner, røtter) i stedet for å stoppe helt.</summary>
    private void TryStepUp(Vector3 before, Vector3 horiz, float dt)
    {
        if (!IsOnWall() || horiz.LengthSquared() < 0.25f || Swimming) return;
        var moved = GlobalPosition - before;
        var wantMove = horiz * dt;
        if (new Vector3(moved.X, 0, moved.Z).Length() > wantMove.Length() * 0.6f) return;
        var up = new Vector3(0, StepHeight, 0);
        var from = GlobalTransform;
        if (TestMove(from, up)) return;                              // tak over
        var raised = from.Translated(up);
        var fwd = horiz.Normalized() * 0.3f;
        if (TestMove(raised, fwd)) return;                           // fortsatt blokkert
        GlobalPosition += up + fwd;
        ApplyFloorSnap();
    }

    private void Animate(float dt, float speed, bool grounded) =>
        _model.Animate(speed, grounded || Flying, Swimming, _swing, dt);

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        // Mykt kamera: følger raskt sideveis, roligere i høyden
        var target = GlobalPosition + new Vector3(0, 1.65f, 0);
        var p = _camPivot.GlobalPosition;
        float kx = 1f - Mathf.Exp(-25f * dt), ky = 1f - Mathf.Exp(-10f * dt);
        p = new Vector3(Mathf.Lerp(p.X, target.X, kx), Mathf.Lerp(p.Y, target.Y, ky), Mathf.Lerp(p.Z, target.Z, kx));
        if (p.DistanceSquaredTo(target) > 25f) p = target;   // teleport
        _camPivot.GlobalPosition = p;
        _camPivot.Rotation = new Vector3(_pitch, _yaw, 0);
        _arm.SpringLength = Mathf.Lerp(_arm.SpringLength, _armTarget, 1f - Mathf.Exp(-10f * dt));
        // Løv mellom kameraet og figuren tones bort i foliage-shaderen (trekroner har ingen kollisjon)
        RenderingServer.GlobalShaderParameterSet("player_head", GlobalPosition + new Vector3(0, 1.3f, 0));

        // FOV øker litt ved sprint
        bool sprint = Input.IsPhysicalKeyPressed(Key.Shift) && !Exhausted && Velocity.Length() > JogSpeed + 0.5f;
        _camera.Fov = Mathf.Lerp(_camera.Fov, sprint ? 76f : 70f, 1f - Mathf.Exp(-6f * dt));

        // Kamerarist
        _shake = Mathf.Max(0f, _shake - dt * 1.8f);
        _shakeT += dt * 30f;
        float s = _shake * _shake;
        _camera.HOffset = (Mathf.Sin(_shakeT * 1.3f) * 0.6f + Mathf.Sin(_shakeT * 2.9f) * 0.4f) * 0.12f * s;
        _camera.VOffset = (Mathf.Sin(_shakeT * 1.7f + 1f) * 0.6f + Mathf.Sin(_shakeT * 3.3f) * 0.4f) * 0.12f * s;

        UpdateTarget();
        var hud = Hud.Instance;
        if (hud != null)
        {
            hud.SetStamina(Stamina / MaxStamina, Exhausted);
            hud.SetHeading(-_yaw);
        }
    }

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
        if (Target is IHittable th) Hud.Instance?.SetTargetHealth(th.TargetName, th.HealthFraction);
        else Hud.Instance?.SetTargetHealth(null, 0f);
    }

    /// <summary>Bygger et bål foran spilleren (koster greiner og stein).</summary>
    public Campfire BuildCampfire(bool free = false)
    {
        if (Flying || Swimming) return null;
        if (!free && (Inventory.Count("Grein") < Campfire.CostBranches || Inventory.Count("Stein") < Campfire.CostStones))
        {
            Hud.Instance?.Toast($"Et bål krever {Campfire.CostBranches} greiner og {Campfire.CostStones} stein.");
            return null;
        }
        var forward = new Basis(Vector3.Up, _model.Rotation.Y) * Vector3.Forward;
        var pos = GlobalPosition + forward * 1.8f;
        pos.Y = Game.World.GetHeight(pos.X, pos.Z);
        if (Game.World.GetWaterLevel(pos.X, pos.Z) > pos.Y) { Hud.Instance?.Toast("Kan ikke bygge bål i vann."); return null; }
        if (!free)
        {
            Inventory.Add("Grein", -Campfire.CostBranches);
            Inventory.Add("Stein", -Campfire.CostStones);
        }
        var fire = new Campfire();
        GetParent().AddChild(fire);
        fire.GlobalPosition = pos;
        Hud.Instance?.Toast("Du bygde et bål.");
        return fire;
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
