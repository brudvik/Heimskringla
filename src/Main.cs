using System;
using System.Threading.Tasks;
using Godot;
using Heimskringla.Core;
using Heimskringla.Environment;
using Heimskringla.Gameplay;
using Heimskringla.Terrain;
using Heimskringla.UI;
using Heimskringla.Vegetation;
using Heimskringla.World;

namespace Heimskringla;

/// <summary>
/// Oppstart: genererer verden i bakgrunnen, strømmer inn terreng rundt startpunktet,
/// og plasserer spilleren når bakken er klar.
/// </summary>
public partial class Main : Node3D
{
    public static int Seed = 4242;
    private static bool _loadOnStart;
    private static bool _argsParsed;

    private enum State { Generating, Streaming, Playing }
    private State _state = State.Generating;
    private Task<WorldGenerator> _genTask;
    private volatile string _progress = "Starter...";

    private TerrainManager _terrain;
    private CollisionManager _collision;
    private VegetationManager _vegetation;
    private GrassManager _grass;
    private DayNight _dayNight;
    private Hud _hud;
    private Player _player;
    private Camera3D _loadingCamera;
    private Vector3 _spawn;
    private float _streamTime;
    private float? _loadedTime;
    private double _genSeconds;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

    public override void _Ready()
    {
        if (!_argsParsed)
        {
            _argsParsed = true;
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                if (arg.StartsWith("--seed=") && int.TryParse(arg[7..], out int s)) Seed = s;
                if (arg.StartsWith("--autotest=")) _autoTestDir = arg[11..];
            }
        }
        if (!_loadOnStart) WorldState.Reset();

        RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        if (_autoTestDir != null) DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        _hud = new Hud();
        AddChild(_hud);
        _dayNight = new DayNight();
        AddChild(_dayNight);

        int seed = Seed;
        _genTask = Task.Run(() => WorldGenerator.Generate(seed, s => _progress = s));
    }

    public override void _ExitTree()
    {
        Game.Jobs?.Dispose();
        Game.Jobs = null;
    }

    public override void _Process(double delta)
    {
        switch (_state)
        {
            case State.Generating:
                _hud.SetLoading($"Genererer verden (seed {Seed})\n{_progress}");
                if (_genTask.IsFaulted)
                {
                    _hud.SetLoading("Feil under generering:\n" + _genTask.Exception?.InnerException?.Message);
                    GD.PushError(_genTask.Exception?.ToString());
                    _state = State.Playing;
                    return;
                }
                if (_genTask.IsCompleted) Setup(_genTask.Result);
                break;

            case State.Streaming:
                _streamTime += (float)delta;
                Game.Focus = _spawn;
                Game.Jobs.ProcessCompleted(14.0);
                _hud.SetLoading($"Laster terreng rundt startpunktet... ({Game.Jobs.PendingCount} jobber igjen)");
                if ((_terrain.IsAreaReady(_spawn, 200f) && _collision.IsReadyAt(_spawn) && _streamTime > 0.5f) || _streamTime > 25f)
                    SpawnPlayer();
                break;

            case State.Playing:
                if (Game.Jobs == null) return;
                Game.Jobs.ProcessCompleted(4.0);
                if (_hud.DebugVisible) _hud.SetDebug(DebugText());
                if (_autoTestDir != null && _player != null) AutoTest((float)delta);
                break;
        }
    }

    private void Setup(WorldGenerator world)
    {
        _genSeconds = _sw.Elapsed.TotalSeconds;
        GD.Print($"Verden generert på {_genSeconds:F1} s (seed {Seed}, elveceller {world.RiverCells}, innsjøceller {world.LakeCells})\n{world.Report}");
        Game.World = world;
        Game.Jobs = new JobQueue(Math.Max(2, System.Environment.ProcessorCount - 2));

        Game.TerrainMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
        var waterShader = GD.Load<Shader>("res://shaders/water.gdshader");
        Game.WaterMaterial = new ShaderMaterial { Shader = waterShader };
        Game.WaterMaterial.SetShaderParameter("wave_strength", 0.5f);
        Game.WaterMaterial.SetShaderParameter("shallow_color", new Color(0.22f, 0.45f, 0.42f));
        Game.PropMaterial = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            Roughness = 0.9f,
        };
        PropLibrary.Init();

        _spawn = world.FindSpawn();
        if (_loadOnStart)
        {
            _loadOnStart = false;
            var loaded = WorldState.Load(Seed);
            if (loaded.HasValue)
            {
                _spawn = loaded.Value.pos;
                _loadedTime = loaded.Value.time;
                _dayNight.TimeOfDay = loaded.Value.time;
            }
        }
        Game.Focus = _spawn;

        _terrain = new TerrainManager { Name = "Terrain" };
        AddChild(_terrain);
        _collision = new CollisionManager { Name = "Collision" };
        AddChild(_collision);
        Game.Collision = _collision;
        _vegetation = new VegetationManager { Name = "Vegetation" };
        AddChild(_vegetation);
        _grass = new GrassManager { Name = "Grass" };
        AddChild(_grass);

        var oceanMat = new ShaderMaterial { Shader = waterShader };
        AddChild(new MeshInstance3D
        {
            Name = "Ocean",
            Mesh = new PlaneMesh { Size = new Vector2(40000, 40000), Material = oceanMat },
            Position = new Vector3(0, WorldSettings.SeaLevel, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // Midlertidig kamera mens terrenget lastes
        _loadingCamera = new Camera3D { Far = 12000f, Current = true };
        AddChild(_loadingCamera);
        _loadingCamera.LookAtFromPosition(_spawn + new Vector3(0, 120, 220), _spawn, Vector3.Up);

        var img = Image.CreateFromData(world.MapSize, world.MapSize, false, Image.Format.Rgb8, world.MapPixels);
        _hud.Map.Texture = ImageTexture.CreateFromImage(img);
        _hud.Map.Seed = Seed;

        _state = State.Streaming;
    }

    private void SpawnPlayer()
    {
        _player = new Player { Name = "Player" };
        AddChild(_player);
        _player.Teleport(_spawn.X, _spawn.Z);
        _loadingCamera.QueueFree();
        _hud.Map.PlayerPosition = () => _player.GlobalPosition;
        _hud.Map.PlayerYaw = () => _player.Camera.GlobalRotation.Y;
        _hud.Map.OnTeleport = (x, z) => _player.Teleport(x, z);
        _hud.SetLoading(null);
        _hud.Toast($"Velkommen til Heimskringla! Verden generert på {_genSeconds:F1} s.");
        if (_loadedTime.HasValue) _hud.Toast("Lagret spill lastet.");
        _state = State.Playing;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || _state != State.Playing || _player == null) return;
        switch (k.PhysicalKeycode)
        {
            case Key.F5:
                WorldState.Save(Seed, _player.GlobalPosition, _dayNight.TimeOfDay);
                _hud.Toast("Spillet er lagret.");
                break;
            case Key.F9:
                if (!WorldState.Exists(Seed)) { _hud.Toast("Ingen lagring funnet for denne verdenen."); break; }
                _loadOnStart = true;
                GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
                break;
            case Key.F10:
                Seed = (int)(GD.Randi() % 1000000);
                GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
                break;
        }
    }

    // ------------------------------------------------------------ Automatisk test
    // Kjør med: Godot.exe --path . -- --autotest=<mappe>
    // Tar skjermbilder fra flere steder i verden, logger ytelse og avslutter.
    private static string _autoTestDir;
    private float _autoT;
    private int _autoStep;
    private bool _autoSwing;
    private float _fpsSum;
    private int _fpsN;

    private void AutoTest(float dt)
    {
        _autoT += dt;
        _fpsSum += (float)Engine.GetFramesPerSecond();
        _fpsN++;
        // Ved hugge-steget: start et hugg rett før bildet tas, og zoom inn
        if (_autoStep == 5 && _autoT > 5.55f && !_autoSwing) { _autoSwing = true; _player.CameraDistance = 2.6f; _player.StartSwing(); }
        if (_autoT < 6f) return; // la strømmingen roe seg
        _autoT = 0;
        string name = $"shot_{_autoStep:D2}.png";
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_autoTestDir, name));
        GD.Print($"[autotest] figur: {_player.VisualState}");
        GD.Print($"[autotest] {name}: snitt-FPS {_fpsSum / Math.Max(1, _fpsN):F0}\n{DebugText()}\n");
        _fpsSum = 0; _fpsN = 0;
        var w = Game.World;
        _autoStep++;
        switch (_autoStep)
        {
            case 1: // høyt over startpunktet, se utover
                _player.ToggleFly();
                _player.GlobalPosition = _spawn + new Vector3(0, 160, 0);
                break;
            case 2: // mot fjellene: finn høyeste punkt i en grov søk
            {
                Vector3 best = _spawn; float bh = -1;
                for (int z = -4500; z <= 4500; z += 150)
                for (int x = -4500; x <= 4500; x += 150)
                {
                    float h = w.GetHeight(x, z);
                    if (h > bh) { bh = h; best = new Vector3(x, h, z); }
                }
                _player.GlobalPosition = best + new Vector3(250, 120, 250);
                break;
            }
            case 3: // nord (kaldere)
                _player.GlobalPosition = new Vector3(_spawn.X, 0, -2500);
                _player.GlobalPosition = new Vector3(_spawn.X, w.GetHeight(_spawn.X, -2500) + 60, -2500);
                break;
            case 4: // tilbake på bakken ved start, nærbilde av figuren fra siden
                _player.ToggleFly();
                _player.Teleport(_spawn.X, _spawn.Z);
                _player.CameraDistance = 3f;
                _player.CameraYaw += 1.2f;
                break;
            case 5: // fell nærmeste tre
            {
                var tree = Nearest<TreeNode>();
                if (tree == null) { GD.Print("[autotest] fant ikke tre i nærheten"); break; }
                GD.Print($"[autotest] feller {tree.Def.Name} {tree.GlobalPosition.DistanceTo(_player.GlobalPosition):F1} m unna");
                var look = tree.GlobalPosition - _player.GlobalPosition;
                _player.Teleport(tree.GlobalPosition.X - look.Normalized().X * 2.5f, tree.GlobalPosition.Z - look.Normalized().Z * 2.5f);
                for (int i = 0; i < tree.Def.Hp; i++) tree.Hit(_player, ToolKind.Axe);
                break;
            }
            case 6: // plukk opp alt løst, hakk en stein, plukk en grein/stein
            {
                int picked = 0;
                foreach (var n in GetTree().Root.FindChildren("*", "RigidBody3D", true, false))
                    if (n is LooseItem li) { li.Interact(_player); picked++; }
                var rock = Nearest<RockNode>();
                if (rock != null) for (int i = 0; i < rock.Def.Hp; i++) rock.Hit(_player, ToolKind.Pickaxe);
                var pick = Nearest<PropPickup>();
                pick?.Interact(_player);
                GD.Print($"[autotest] plukket {picked} løse, stein hakket: {rock != null}, pickup: {pick?.Def.Name}");
                GD.Print("[autotest] inventar: " + string.Join(", ", System.Linq.Enumerable.Select(Inventory.Items, kv => $"{kv.Key}={kv.Value}")));
                break;
            }
            default:
                GetTree().Quit();
                break;
        }
    }

    private T Nearest<T>() where T : Node3D
    {
        T best = null;
        float bd = float.MaxValue;
        foreach (var n in _vegetation.GetChildren())
            if (n is T t)
            {
                float d = t.GlobalPosition.DistanceTo(_player.GlobalPosition);
                if (d < bd) { bd = d; best = t; }
            }
        return best;
    }

    private string DebugText()
    {
        var p = _player?.GlobalPosition ?? Game.Focus;
        var w = Game.World;
        float h = w.GetHeight(p.X, p.Z);
        int hours = (int)(_dayNight.TimeOfDay * 24f), mins = (int)((_dayNight.TimeOfDay * 24f - hours) * 60f);
        return
            $"FPS {Engine.GetFramesPerSecond():F0}  (prosess {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:F1} ms · " +
            $"fysikk {Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000:F1} ms)\n" +
            $"Render CPU {RenderingServer.ViewportGetMeasuredRenderTimeCpu(GetViewport().GetViewportRid()):F1} ms · " +
            $"GPU {RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid()):F1} ms\n" +
            $"Draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):F0} · " +
            $"trekanter {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1000:F0}k\n" +
            $"VRAM {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576:F0} MB · " +
            $"RAM {Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576:F0} MB (+.NET {GC.GetTotalMemory(false) / 1048576} MB)\n" +
            $"Terreng: {_terrain.ChunkCount} chunks ({_terrain.PendingCount} venter)\n" +
            $"Vegetasjon: {_vegetation.CellCount} celler, {_vegetation.InstanceCount} obj, {_vegetation.PromotedCount} aktive\n" +
            $"Gress: {_grass.CellCount} celler · Kollisjon: {_collision.BodyCount}\n" +
            $"Jobber: {Game.Jobs.PendingCount} i kø, {Game.Jobs.RunningCount} kjører\n" +
            $"Pos {p.X:F0}, {p.Y:F1}, {p.Z:F0}  (bakke {h:F1} m)\n" +
            $"Sone: {Biomes.Name(w.GetBiome(p.X, p.Z))} · temp {w.GetTemp(p.X, p.Z):F2} · fukt {w.GetMoist(p.X, p.Z):F2}\n" +
            $"Seed {Seed} · Klokka {hours:D2}:{mins:D2}" + (_player?.Flying == true ? " · FLYR" : "") + (_player?.Swimming == true ? " · SVØMMER" : "");
    }
}
