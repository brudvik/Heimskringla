using System;
using System.Collections.Generic;
using Godot;

namespace Heimskringla.UI;

/// <summary>
/// Lager ikoner ved å rendre 3D-mesher i en skjult SubViewport (én om gangen, over noen frames).
/// Ikonene matcher dermed modellene i spillet automatisk.
/// </summary>
public partial class IconRenderer : Node
{
    public static IconRenderer Instance;
    public readonly Dictionary<string, Texture2D> Icons = new();
    public event Action<string> IconReady;

    private SubViewport _vp;
    private Camera3D _cam;
    private MeshInstance3D _mi;
    private readonly Queue<(string key, Mesh mesh, Vector3 rot)> _queue = new();
    private string _current;
    private int _wait;

    public override void _Ready()
    {
        Instance = this;
        _vp = new SubViewport
        {
            Size = new Vector2I(128, 128),
            TransparentBg = true,
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Msaa3D = Viewport.Msaa.Msaa4X,
        };
        AddChild(_vp);
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.ClearColor,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.75f, 0.78f, 0.85f),
            AmbientLightEnergy = 0.6f,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
        };
        _vp.AddChild(new WorldEnvironment { Environment = env });
        var sun = new DirectionalLight3D { LightEnergy = 1.6f };
        _vp.AddChild(sun);
        sun.RotationDegrees = new Vector3(-50, 35, 0);
        _cam = new Camera3D { Fov = 30f, Current = true };
        _vp.AddChild(_cam);
        _mi = new MeshInstance3D();
        _vp.AddChild(_mi);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public void Request(string key, Mesh mesh, Vector3 rotationDeg)
    {
        if (mesh == null || Icons.ContainsKey(key)) return;
        _queue.Enqueue((key, mesh, rotationDeg));
    }

    public override void _Process(double delta)
    {
        if (_current != null)
        {
            if (--_wait > 0) return;
            var img = _vp.GetTexture().GetImage();
            Icons[_current] = ImageTexture.CreateFromImage(img);
            _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            var done = _current;
            _current = null;
            IconReady?.Invoke(done);
            return;
        }
        if (_queue.Count == 0) return;
        var (key, mesh, rot) = _queue.Dequeue();
        _mi.Mesh = mesh;
        _mi.RotationDegrees = rot;
        // Ramm inn modellen: avgrensningskule rundt den roterte AABB-en
        var aabb = mesh.GetAabb();
        var center = _mi.Basis * aabb.GetCenter();
        float radius = aabb.Size.Length() * 0.5f;
        float dist = radius / Mathf.Tan(Mathf.DegToRad(_cam.Fov * 0.5f)) * 1.02f;
        _cam.LookAtFromPosition(center + new Vector3(0, radius * 0.35f, dist), center, Vector3.Up);
        _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _current = key;
        _wait = 3;
    }
}
