using System.Collections.Generic;
using Godot;
using Heimskringla.Vegetation;

namespace Heimskringla.Gameplay;

/// <summary>
/// Animert figur: Quaternius Universal Base Character + skjegg/hår, med animasjoner fra
/// Universal Animation Library (samme skjelett). Verktøy fra Kenney Survival Kit i høyre hånd.
/// </summary>
public partial class CharacterModel : PlayerVisual
{
    public const string BodyPath = "res://assets/characters/Superhero_Male_FullBody.gltf";
    public const string AnimPath = "res://assets/animations/UAL1_Standard.glb";
    private static readonly string[] HairPaths =
    {
        "res://assets/characters/Hair_Beard.gltf",
        "res://assets/characters/Hair_Long.gltf",
    };

    // Plassering av verktøy i hånden (justert mot hand_r-beinet)
    public static Vector3 ToolOffset = new(0.0f, 0.08f, 0.02f);
    public static Vector3 ToolRotationDeg = new(90f, 0f, 0f);
    public const float ToolScale = 2.7f;

    private AnimationPlayer _anim;
    private readonly Dictionary<string, string> _names = new();
    private Node3D _axe, _pick;
    private string _current;
    private float _swingLength = 0.9f;
    private bool _wasSwinging;
    private float _pickupTimer;

    public static bool Available => ResourceLoader.Exists(BodyPath) && ResourceLoader.Exists(AnimPath);

    public override float SwingDuration => _swingLength;
    public override float HitMoment => 0.45f;

    public override void _Ready()
    {
        // glTF-figurer ser mot +Z; spilleren forventer -Z
        var body = GD.Load<PackedScene>(BodyPath).Instantiate<Node3D>();
        body.Rotation = new Vector3(0, Mathf.Pi, 0);
        AddChild(body);
        var skel = (Skeleton3D)body.FindChildren("*", "Skeleton3D", true, false)[0];

        // Hår og skjegg er rigget til samme skjelett: flytt meshene inn i figurens Skeleton3D
        foreach (var path in HairPaths)
        {
            if (!ResourceLoader.Exists(path)) continue;
            var hair = GD.Load<PackedScene>(path).Instantiate<Node3D>();
            foreach (var n in hair.FindChildren("*", "MeshInstance3D", true, false))
            {
                var mi = (MeshInstance3D)n;
                mi.Owner = null;
                mi.GetParent().RemoveChild(mi);
                skel.AddChild(mi);
                mi.Skeleton = mi.GetPathTo(skel);
                // Blondt/rødlig vikinghår
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                    if (mi.Mesh.SurfaceGetMaterial(s) is BaseMaterial3D hm)
                    {
                        var m = (BaseMaterial3D)hm.Duplicate();
                        m.AlbedoColor = new Color(1.0f, 0.75f, 0.45f);
                        mi.SetSurfaceOverrideMaterial(s, m);
                    }
            }
            hair.QueueFree();
        }

        // Animasjoner: kopier biblioteket fra UAL og spill det på figuren
        var ual = GD.Load<PackedScene>(AnimPath).Instantiate();
        var srcPlayer = (AnimationPlayer)ual.FindChildren("*", "AnimationPlayer", true, false)[0];
        var lib = (AnimationLibrary)srcPlayer.GetAnimationLibrary("").Duplicate(true);
        ual.Free();
        // Godot-importen fjerner "_Loop"-suffikset og setter løkke selv. Vi slår opp begge varianter.
        foreach (var name in lib.GetAnimationList())
        {
            string n = name.ToString();
            _names[n] = n;
            if (lib.GetAnimation(name).LoopMode != Animation.LoopModeEnum.None) _names[n + "_Loop"] = n;
        }
        foreach (var looping in new[] { "Idle_Loop", "Jog_Fwd_Loop", "Sprint_Loop", "Walk_Loop", "Jump_Loop", "Swim_Fwd_Loop", "Swim_Idle_Loop" })
        {
            string baseName = looping[..^5];
            if (!_names.ContainsKey(looping) && lib.HasAnimation(baseName))
            {
                lib.GetAnimation(baseName).LoopMode = Animation.LoopModeEnum.Linear;
                _names[looping] = baseName;
            }
        }
        _anim = new AnimationPlayer { Name = "AnimationPlayer" };
        body.AddChild(_anim);
        _anim.AddAnimationLibrary("", lib);
        if (lib.HasAnimation("Sword_Attack")) _swingLength = (float)lib.GetAnimation("Sword_Attack").Length / 1.4f;
        Play("Idle_Loop", 1f);

        // Verktøy
        var hand = new BoneAttachment3D { BoneName = "hand_r" };
        skel.AddChild(hand);
        var rotEnv = OS.GetEnvironment("HK_TOOLROT");   // testhjelp: "x,y,z" i grader
        if (!string.IsNullOrEmpty(rotEnv))
        {
            var p = rotEnv.Split(',');
            ToolRotationDeg = new Vector3(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2]));
        }
        var grip = new Node3D { Position = ToolOffset, RotationDegrees = ToolRotationDeg, Scale = Vector3.One * ToolScale };
        hand.AddChild(grip);
        _axe = LoadTool("res://assets/survival/tool-axe.glb", grip);
        _pick = LoadTool("res://assets/survival/tool-pickaxe.glb", grip);
        SetTool(ToolKind.Axe);
    }

    private static Node3D LoadTool(string path, Node3D parent)
    {
        if (!ResourceLoader.Exists(path)) return new Node3D();
        var t = GD.Load<PackedScene>(path).Instantiate<Node3D>();
        // Grep litt opp på skaftet
        t.Position = new Vector3(0, -0.06f, 0);
        parent.AddChild(t);
        return t;
    }

    public override void SetTool(ToolKind tool)
    {
        _axe.Visible = tool == ToolKind.Axe;
        _pick.Visible = tool == ToolKind.Pickaxe;
    }

    public override void PlayPickup() => _pickupTimer = 0.6f;

    public override string DebugState => $"anim={_anim.CurrentAnimation} playing={_anim.IsPlaying()} pos={_anim.CurrentAnimationPosition:F2} cur={_current}";

    private void Play(string name, float speed, float blend = 0.2f)
    {
        if (!_names.TryGetValue(name, out var real) || !_anim.HasAnimation(real)) return;
        name = real;
        _anim.SpeedScale = speed;
        if (_current == name) return;
        _current = name;
        _anim.Play(name, blend);
    }

    public override void Animate(float speed, bool grounded, bool swimming, float swing, float dt)
    {
        _pickupTimer -= dt;
        bool swinging = swing >= 0f;
        if (swinging && !_wasSwinging)
        {
            _current = null;
            Play("Sword_Attack", 1.4f, 0.08f);
            _anim.Seek(0, true);
        }
        _wasSwinging = swinging;
        if (swinging) return;

        if (swimming)
            Play(speed > 0.5f ? "Swim_Fwd_Loop" : "Swim_Idle_Loop", 1f);
        else if (!grounded)
            Play("Jump_Loop", 1f);
        else if (_pickupTimer > 0f)
            Play("Interact", 1.6f, 0.1f);
        else if (speed < 0.4f)
            Play("Idle_Loop", 1f);
        else if (speed < 6f)
            Play("Jog_Fwd_Loop", Mathf.Clamp(speed / 4.2f, 0.6f, 1.4f));
        else
            Play("Sprint_Loop", Mathf.Clamp(speed / 7.5f, 0.8f, 1.4f));
    }
}
