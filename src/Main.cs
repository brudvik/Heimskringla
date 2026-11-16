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
    private Weather _weather;
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
        if (_autoTestDir != null)
        {
            Game.AutoTest = true;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            // Ikke fang musa mens testen går (brukeren jobber i andre vinduer)
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        _hud = new Hud();
        AddChild(_hud);
        _dayNight = new DayNight { Name = "DayNight" };
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
                if (_autoTestDir != null && !_loadShot && _sw.Elapsed.TotalSeconds > 0.8)
                {
                    _loadShot = true;   // bilde av lasteskjermen (logo)
                    GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_autoTestDir, "loading.png"));
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
        WorldTexture.Publish(world);
        Game.Jobs = new JobQueue(Math.Max(2, System.Environment.ProcessorCount - 2));

        Game.TerrainMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
        TerrainTextures.Apply(Game.TerrainMaterial);
        var waterShader = GD.Load<Shader>("res://shaders/water.gdshader");
        Game.WaterMaterial = new ShaderMaterial { Shader = waterShader };
        Game.WaterMaterial.SetShaderParameter("wave_strength", 0.5f);
        Game.WaterMaterial.SetShaderParameter("shallow_color", new Color(0.18f, 0.40f, 0.37f));
        // Dypt fjordvann: mørkt skifergrønt (norske fjorder), ikke tropisk blått
        Game.WaterMaterial.SetShaderParameter("deep_color", new Color(0.02f, 0.075f, 0.1f));
        Game.PropMaterial = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            Roughness = 0.9f,
        };
        PropLibrary.Init();
        _hud.RequestIcons();

        _spawn = world.FindSpawn();
        if (_loadOnStart)
        {
            _loadOnStart = false;
            var loaded = WorldState.Load(Seed);
            if (loaded.HasValue)
            {
                _spawn = loaded.Value.pos;
                _loadedTime = loaded.Value.time;
                _dayNight.Day = Mathf.FloorToInt(loaded.Value.time);
                _dayNight.TimeOfDay = loaded.Value.time - _dayNight.Day;
            }
        }
        Game.Focus = _spawn;
        _weather = new Weather { Name = "Weather" };
        AddChild(_weather);
        _dayNight.Weather = _weather;
        AddChild(new Ambience { Name = "Ambience" });

        _terrain = new TerrainManager { Name = "Terrain" };
        AddChild(_terrain);
        _collision = new CollisionManager { Name = "Collision" };
        AddChild(_collision);
        Game.Collision = _collision;
        _vegetation = new VegetationManager { Name = "Vegetation" };
        AddChild(_vegetation);
        _grass = new GrassManager { Name = "Grass" };
        if (OS.GetEnvironment("HK_NOGRASS") != "1") AddChild(_grass);   // testbryter

        var oceanMat = new ShaderMaterial { Shader = waterShader };
        oceanMat.SetShaderParameter("is_ocean", true);
        AddChild(new Ocean(oceanMat));
        AddChild(new Waterfalls { Name = "Waterfalls" });

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
        foreach (var f in WorldState.Fires)
        {
            var fire = new Campfire { Fuel = f[3] };
            AddChild(fire);
            fire.GlobalPosition = new Vector3(f[0], f[1], f[2]);
        }
        if (_autoTestDir != null)
        {
            _weather.Locked = true; _weather.Set(WeatherKind.Broken, true);
            // Ta opp lyden for hvert steg (audio_XX.wav), så lydbildet kan sjekkes/lyttes til
            _rec = new AudioEffectRecord();
            AudioServer.AddBusEffect(0, _rec);
            _rec.SetRecordingActive(true);
        }
        _state = State.Playing;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Game.AutoTest) return;
        if (e is not InputEventKey { Pressed: true, Echo: false } k || _state != State.Playing || _player == null) return;
        switch (k.PhysicalKeycode)
        {
            case Key.F5:
                WorldState.Fires.Clear();
                foreach (var f in Campfire.All)
                    WorldState.Fires.Add(new[] { f.GlobalPosition.X, f.GlobalPosition.Y, f.GlobalPosition.Z, f.Fuel });
                WorldState.Save(Seed, _player.GlobalPosition, _dayNight.Day + _dayNight.TimeOfDay);
                _hud.Toast("Spillet er lagret.");
                break;
            case Key.F6:
                _weather.Set((WeatherKind)(((int)_weather.Kind + 1) % 7));
                _hud.Toast($"Været skifter: {_weather.Description}");
                break;
            case Key.F7:
                _dayNight.Day += Weather.DaysPerSeason;
                _hud.Toast($"Årstid: {_weather.SeasonName}");
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
    private TreeNode _autoTree;
    private bool _autoSwing;
    private float _fpsSum;
    private bool _loadShot;
    private AudioEffectRecord _rec;
    private readonly int _autoFrom = int.TryParse(OS.GetEnvironment("HK_FROM"), out int f) ? f : 0;
    private void SetFly(bool on) { if (_player.Flying != on) _player.ToggleFly(); }
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
        if (_rec != null)
        {
            _rec.SetRecordingActive(false);
            _rec.GetRecording()?.SaveToWav(System.IO.Path.Combine(_autoTestDir, $"audio_{_autoStep:D2}.wav"));
            _rec.SetRecordingActive(true);
        }
        GD.Print($"[autotest] figur: {_player.VisualState}");
        GD.Print($"[autotest] fotspor: {Footprints.Spawned} {Footprints.LastInfo}");
        GD.Print($"[autotest] {name}: snitt-FPS {_fpsSum / Math.Max(1, _fpsN):F0}\n{DebugText()}\n");
        _fpsSum = 0; _fpsN = 0;
        var w = Game.World;
        _autoStep++;
        if (_autoStep < _autoFrom) _autoStep = _autoFrom;
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
                // Hogg til treet er nesten nede (antall avhenger av størrelse og ferdighet)
                for (int i = 0; i < 60 && tree.HealthFraction > 0.2f; i++) tree.Hit(_player, ToolKind.Axe);
                _autoTree = tree;
                _player.CameraYaw = Mathf.Atan2(-(tree.GlobalPosition.X - _player.GlobalPosition.X), -(tree.GlobalPosition.Z - _player.GlobalPosition.Z));
                _player.CameraPitch = -0.15f;
                break;
            }
            case 6: // siste hugg (treet faller), plukk opp alt løst, hakk en stein, plukk en grein/stein
            {
                for (int i = 0; i < 10 && _autoTree != null && IsInstanceValid(_autoTree) && !_autoTree.IsQueuedForDeletion(); i++)
                    _autoTree.Hit(_player, ToolKind.Axe);
                _autoTree = null;
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
            case 7: // bakkenivå i kald lynghei (snøtrær, snøsteiner)
                _player.Teleport(_spawn.X + 60f, -2500f);
                _player.CameraDistance = OS.GetEnvironment("HK_LOOKDOWN") == "1" ? 3f : 6f;
                if (OS.GetEnvironment("HK_LOOKDOWN") == "1") _player.CameraPitch = -1.2f;
                break;
            case 8: // solnedgang over fjellet
                _dayNight.TimeOfDay = 0.74f;
                _player.ToggleFly();
                _player.GlobalPosition = _spawn + new Vector3(0, 140, 0);
                _player.CameraYaw = Mathf.Pi * 0.5f;   // se mot vest
                break;
            case 9: // fjord: dypt vann med høye fjell rundt
            {
                _dayNight.TimeOfDay = 0.42f;
                var p = FindPoint((x, z) => w.GetHeight(x, z) < -15f && MaxAround(x, z, 350f) > 160f);
                GD.Print($"[autotest] fjord: {p}");
                _player.GlobalPosition = new Vector3(p.X, 90f, p.Z);
                _player.CameraPitch = -0.35f;
                break;
            }
            case 10: // strand
            {
                var p = FindPoint((x, z) => { float h = w.GetHeight(x, z); return h > 0.6f && h < 2.2f && w.GetNormal(x, z, 3f).Y > 0.97f && w.GetHeight(x + 25f, z) < 0f; });
                GD.Print($"[autotest] strand: {p}");
                _player.ToggleFly();
                _player.Teleport(p.X, p.Z);
                _player.CameraPitch = -0.25f; _player.CameraDistance = 7f;
                _player.CameraYaw = -Mathf.Pi * 0.5f;   // mot +X (havet)
                break;
            }
            case 11: // dyretråkk
            {
                var p = FindPoint((x, z) => { float h = w.GetHeight(x, z); return w.TrailMask(x, z, h, w.GetNormal(x, z).Y) > 0.9f; });
                GD.Print($"[autotest] tråkk: {p}");
                _player.Teleport(p.X, p.Z);
                _player.CameraPitch = -0.6f; _player.CameraDistance = 6f;
                break;
            }
            case 12: // fjellfot med klipper
            {
                var p = FindPoint((x, z) => { float h = w.GetHeight(x, z); return h > 70f && h < 110f && w.GetNormal(x, z, 4f).Y < 0.8f; });
                GD.Print($"[autotest] klipper: {p}");
                _player.ToggleFly();
                _player.GlobalPosition = new Vector3(p.X, w.GetHeight(p.X, p.Z) + 25f, p.Z + 40f);
                _player.CameraPitch = -0.15f; _player.CameraDistance = 8f; _player.CameraYaw = 0f;
                break;
            }
            case 13: // gå baklengs på stranda mot kameraet: fotspor bak figuren
            {
                _player.ToggleFly();
                var p = FindPoint((x, z) => { float h = w.GetHeight(x, z); return h > 0.8f && h < 2.0f && w.GetNormal(x, z, 4f).Y > 0.985f && w.GetHeight(x, z + 12f) > 0.6f && w.GetHeight(x, z - 12f) > 0.6f; });
                GD.Print($"[autotest] fotspor: {p}");
                _player.Teleport(p.X, p.Z);
                _player.CameraYaw = 0f; _player.CameraPitch = -1.05f; _player.CameraDistance = 7f;
                _player.DebugInput = new Vector2(0, 0.45f);   // rolig gange mot kameraet
                if (OS.GetEnvironment("HK_DECALTEST") == "1")
                {
                    var dd = new Decal { Size = new Vector3(3, 2, 3), Modulate = new Color(1, 0, 0, 1) };
                    var img = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8); img.Fill(Colors.White);
                    dd.TextureAlbedo = ImageTexture.CreateFromImage(img);
                    AddChild(dd);
                    dd.GlobalPosition = _player.GlobalPosition + new Vector3(0, 0.2f, 3f);
                }
                break;
            }
            case 14: // skråning: føttene på ujevn bakke, sett fra siden
            {
                _player.DebugInput = null;
                var p = FindPoint((x, z) => { float h = w.GetHeight(x, z); var n = w.GetNormal(x, z, 1.5f); return h > 5f && h < 60f && n.Y > 0.78f && n.Y < 0.86f; });
                GD.Print($"[autotest] skråning: {p}");
                _player.Teleport(p.X, p.Z);
                var n2 = w.GetNormal(p.X, p.Z, 1.5f);
                // Se langs skråningen (fra siden), så høydeforskjellen mellom føttene synes
                _player.CameraYaw = Mathf.Atan2(n2.X, n2.Z) + Mathf.Pi * 0.5f;
                _player.CameraPitch = -0.1f; _player.CameraDistance = 2.6f;
                break;
            }
            case 15: // storm med regn ved startpunktet (dagtid)
            {
                _dayNight.TimeOfDay = 0.45f;
                _weather.Set(WeatherKind.Storm, true);
                _player.Teleport(_spawn.X, _spawn.Z);
                _player.CameraPitch = -0.1f; _player.CameraDistance = 5f;
                break;
            }
            case 16: // høst: gule/røde løvtrær, flyr litt over en skog med løvtrær
            {
                _dayNight.Day = Weather.DaysPerSeason * 2 + 3;
                _dayNight.TimeOfDay = 0.5f;
                _weather.Set(WeatherKind.Clear, true);
                var p = FindPoint((x, z) => { float t = w.GetTemp(x, z); return t > 0.55f && w.GetMoist(x, z) > 0.5f && w.GetHeight(x, z) > 5f; });
                GD.Print($"[autotest] høstskog: {p}");
                _player.Teleport(p.X, p.Z);
                _player.CameraPitch = -0.25f; _player.CameraDistance = 9f;
                break;
            }
            case 17: // vinter: snødekke og snøvær
            {
                _dayNight.Day = Weather.DaysPerSeason * 3 + 3;
                _dayNight.TimeOfDay = 0.5f;
                _weather.Set(WeatherKind.Rain, true);
                _player.Teleport(_spawn.X, _spawn.Z);
                _player.CameraPitch = -0.2f; _player.CameraDistance = 7f;
                break;
            }
            case 18: // vinternatt med nordlys, se mot nord
            {
                _weather.Set(WeatherKind.Clear, true);
                _dayNight.TimeOfDay = 0.98f;
                _dayNight.ForceAurora = true;
                _player.ToggleFly();
                _player.GlobalPosition = _spawn + new Vector3(0, 40, 0);
                _player.CameraYaw = 0f;        // kameraet ser mot -Z (nord)
                _player.CameraPitch = 0.35f;
                break;
            }
            case 19: // elv med strøm, sett ovenfra på skrå (sommer, dag)
            {
                _dayNight.ForceAurora = false;
                _dayNight.Day = Weather.DaysPerSeason + 2;
                _dayNight.TimeOfDay = 0.45f;
                _weather.Set(WeatherKind.Cloudy, true);
                var p = FindPoint((x, z) => { float fx = w.SampleFlow(x, z).Length(); return fx > 1.0f && w.GetLakeWater(x, z) > w.GetHeight(x, z) + 0.5f; });
                GD.Print($"[autotest] elv: {p} strøm {w.SampleFlow(p.X, p.Z)}");
                _player.GlobalPosition = new Vector3(p.X + 14f, w.GetLakeWater(p.X, p.Z) + 9f, p.Z + 14f);
                _player.CameraYaw = Mathf.Pi * 0.25f; _player.CameraPitch = -0.6f; _player.CameraDistance = 4f;
                break;
            }
            case 20: // storm ved stranda: bølger og brenninger
            {
                _weather.Set(WeatherKind.Storm, true);
                var p = FindPoint((x, z) => { float h = w.GetHeight(x, z); return h > 0.6f && h < 2.2f && w.GetNormal(x, z, 3f).Y > 0.97f && w.GetHeight(x + 25f, z) < 0f; });
                _player.GlobalPosition = new Vector3(p.X - 10f, 7f, p.Z);
                _player.CameraYaw = -Mathf.Pi * 0.5f; _player.CameraPitch = -0.2f; _player.CameraDistance = 4f;
                break;
            }
            case 21: // største fossefall nær startpunktet
            {
                _weather.Set(WeatherKind.Clear, true);
                Cascade best = null; float bs = 0f;
                foreach (var c in w.Cascades)
                {
                    float score = c.Drop * c.Widths[0] / (1f + c.Top.DistanceTo(_spawn) / 1500f);
                    if (score > bs) { bs = score; best = c; }
                }
                if (best == null) break;
                GD.Print($"[autotest] fossefall: topp {best.Top} bunn {best.Bottom} fall {best.Drop:F0} m, {best.Points.Count} punkter");
                SetFly(true);
                _dayNight.TimeOfDay = 0.45f;
                // Stå nedstrøms og se oppover fallet
                var dir = ((best.Bottom - best.Top) with { Y = 0 }).Normalized();
                var look = best.Bottom + dir * 45f;
                float gy = Mathf.Max(w.GetHeight(look.X, look.Z), 0f);
                _player.GlobalPosition = look with { Y = Mathf.Max(gy + 4f, best.Bottom.Y + 12f) };
                var to = (best.Top + best.Bottom) * 0.5f - _player.GlobalPosition;
                _player.CameraYaw = Mathf.Atan2(-to.X, -to.Z);
                _player.CameraPitch = Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length()) * 0.8f;
                _player.CameraDistance = 2f;
                break;
            }
            case 22: // bål om natta (tidlig høst), nærbilde
            {
                _dayNight.Day = Weather.DaysPerSeason * 2;
                _dayNight.TimeOfDay = 0.94f;
                _weather.Set(WeatherKind.Cloudy, true);
                SetFly(false);
                _player.Teleport(_spawn.X, _spawn.Z);
                var fire = _player.BuildCampfire(free: true);
                GD.Print($"[autotest] bål bygd: {fire != null}");
                _player.CameraDistance = 5f; _player.CameraPitch = -0.3f;
                if (fire != null)
                {
                    var to = fire.GlobalPosition - _player.GlobalPosition;
                    _player.CameraYaw = Mathf.Atan2(-to.X, -to.Z) + 0.5f;
                }
                break;
            }
            case 23: // nærbilde av en trestamme i barskog (bark, mose, rot)
            {
                SetFly(false);
                _dayNight.Day = 4; _dayNight.TimeOfDay = 0.45f;
                _weather.Set(WeatherKind.Cloudy, true);
                var p = FindPoint((x, z) => w.GetBiome(x, z) == Biome.PineForest && w.GetNormal(x, z, 3f).Y > 0.95f);
                _player.Teleport(p.X, p.Z);
                break;
            }
            case 24: // ... og sikt inn på nærmeste gran/furu når de nære trærne er forfremmet
                AimAtTree(t => t.Def.Kind is PropKind.Pine or PropKind.Spruce);
                break;
            case 25: // nærbilde av en steinblokk
            {
                var rock = Nearest<RockNode>();
                GD.Print($"[autotest] stein: {rock?.GlobalPosition}");
                if (rock != null)
                {
                    var dir = (_player.GlobalPosition - rock.GlobalPosition) with { Y = 0 };
                    var at = rock.GlobalPosition + dir.Normalized() * 4f;
                    _player.Teleport(at.X, at.Z);
                    var to = rock.GlobalPosition - at;
                    _player.CameraYaw = Mathf.Atan2(-to.X, -to.Z);
                    _player.CameraPitch = -0.25f; _player.CameraDistance = 2.5f;
                }
                break;
            }
            case 26: // bjørk (teleporter til den, sikt inn i neste steg)
                GoNearTree(i => i.Kind == PropKind.Birch && !i.Cold);
                break;
            case 27:
                AimAtTree(t => t.Def.Kind == PropKind.Birch);
                break;
            case 28: // tørrfuru
                GoNearTree(i => i.Kind == PropKind.DeadTree && !i.Cold);
                break;
            case 29:
                AimAtTree(t => t.Def.Kind == PropKind.DeadTree, t => !t.Inst.Cold);
                break;
            case 30: // snøtrær i kald sone (flyr dit først, så cellene der lastes)
            {
                var p = FindPoint((x, z) => w.GetTemp(x, z) < 0.2f && w.GetHeight(x, z) > 5f && w.GetHeight(x, z) < 150f && w.GetNormal(x, z, 3f).Y > 0.93f);
                _player.Teleport(p.X, p.Z);
                _autoT = 3f;   // kortere ventetid, neste steg finner treet
                break;
            }
            case 31:
                GoNearTree(i => i.Cold && (i.Kind == PropKind.Spruce || i.Kind == PropKind.Pine));
                break;
            case 32:
                AimAtTree(t => t.Def.Kind is PropKind.Spruce or PropKind.Pine, t => t.Inst.Cold);
                break;
            default:
                GetTree().Quit();
                break;
        }
    }

    /// <summary>Autotest: teleporter like ved nærmeste genererte tre som passer (forfremmes til neste steg).</summary>
    private void GoNearTree(Func<PropInstance, bool> ok)
    {
        var inst = _vegetation.FindNearest(_player.GlobalPosition, ok);
        if (inst == null) { GD.Print("[autotest] fant ikke treet (instans)"); return; }
        _player.Teleport(inst.Pos.X + 3f, inst.Pos.Z + 3f);
    }

    /// <summary>Autotest: still kameraet inn på nærmeste tre som passer (stamme i bildet).</summary>
    private void AimAtTree(Func<TreeNode, bool> kind, Func<TreeNode, bool> extra = null)
    {
        TreeNode tree = null; float bd = float.MaxValue;
        foreach (var nd in _vegetation.GetChildren())
            if (nd is TreeNode tn && kind(tn) && (extra == null || extra(tn)))
            {
                float d = tn.GlobalPosition.DistanceTo(_player.GlobalPosition);
                if (d < bd) { bd = d; tree = tn; }
            }
        if (tree == null) { GD.Print("[autotest] fant ikke treet"); return; }
        var dir = (_player.GlobalPosition - tree.GlobalPosition) with { Y = 0 };
        if (dir.LengthSquared() < 0.01f) dir = Vector3.Back;
        var at = tree.GlobalPosition + dir.Normalized() * 2.4f;
        _player.Teleport(at.X, at.Z);
        var to = tree.GlobalPosition - at;
        _player.CameraYaw = Mathf.Atan2(-to.X, -to.Z) + 0.5f;   // figuren til siden, stammen i midten
        _player.CameraPitch = -0.15f; _player.CameraDistance = 1.6f;
        GD.Print($"[autotest] stamme: {tree.Def.Name}{(tree.Inst.Cold ? " (snø)" : "")}");
    }

    /// <summary>Søker i spiral fra startpunktet etter et punkt som oppfyller kravet (autotest).</summary>
    private Vector3 FindPoint(Func<float, float, bool> ok)
    {
        for (float r = 0; r < 4500f; r += 40f)
        {
            int steps = Math.Max(1, (int)(r * Mathf.Tau / 40f));
            for (int i = 0; i < steps; i++)
            {
                float a = i * Mathf.Tau / steps;
                float x = _spawn.X + Mathf.Cos(a) * r, z = _spawn.Z + Mathf.Sin(a) * r;
                if (Mathf.Abs(x) > 4900 || Mathf.Abs(z) > 4900) continue;
                if (ok(x, z)) return new Vector3(x, Game.World.GetHeight(x, z), z);
            }
        }
        return _spawn;
    }

    private static float MaxAround(float x, float z, float r)
    {
        float m = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.Tau / 8;
            m = Mathf.Max(m, Game.World.GetHeight(x + Mathf.Cos(a) * r, z + Mathf.Sin(a) * r));
        }
        return m;
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
            $"{_weather.SeasonName}, dag {_weather.DayOfSeason} · {_weather.Description} · vind {_weather.Wind * 20f:F0} m/s" +
            (_dayNight.Aurora > 0.05f ? " · nordlys" : "") + "\n" +
            $"Seed {Seed} · Klokka {hours:D2}:{mins:D2}" + (_player?.Flying == true ? " · FLYR" : "") + (_player?.Swimming == true ? " · SVØMMER" : "");
    }
}
