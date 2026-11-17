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
/// Startup: generates the world in the background, streams in terrain around the spawn point,
/// and places the player when the ground is ready.
/// </summary>
public partial class Main : Node3D
{
    public static int Seed = Settings.DefaultSeed;
    private static bool _loadOnStart;
    private static bool _argsParsed;
    /// <summary>The menu has been passed (F9/F10 reload straight in); false = show the main menu.</summary>
    private static bool _menuDone;
    private static bool _introPlayed;
    private static bool _seedFromArgs;

    private enum State { Menu, Generating, MenuFly, Streaming, Playing }
    private State _state = State.Menu;
    private PauseMenu _pause;
    // Main menu: the world is generated behind the menu, and a cinematic camera flies over it
    private MainMenu _menu;
    private bool _menuFly;
    private Camera3D _cineCam;
    private float _cineT, _cineH;
    private Vector3 _cineCenter;
    private Task<WorldGenerator> _genTask;
    private volatile string _progress;

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
                if (arg.StartsWith("--seed=") && int.TryParse(arg[7..], out int s)) { Seed = s; _seedFromArgs = true; }
                if (arg.StartsWith("--autotest=")) _autoTestDir = arg[11..];
            }
        }
        Settings.Load();
        Loc.Apply(Settings.Language);
        _progress = Loc.T("LOADING_STARTING");

        RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        if (_autoTestDir != null)
        {
            Game.AutoTest = true;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            // Don't capture the mouse while the test runs (the user is working in other windows)
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        _hud = new Hud();
        AddChild(_hud);
        _dayNight = new DayNight { Name = "DayNight" };
        AddChild(_dayNight);
        Settings.Apply();

        if (_autoTestDir != null || _menuDone)
        {
            BeginGeneration();
            return;
        }
        // Main menu (with intro the first time). The world for the chosen seed is generated right away, so
        // the menu can show the live world, and "New game"/"Continue" start without regenerating.
        if (!_seedFromArgs) Seed = Settings.LastSeed;
        _hud.Visible = false;
        _menu = new MainMenu { SkipIntro = _introPlayed, OnStart = OnMenuStart };
        _introPlayed = true;
        AddChild(_menu);
        _menuFly = true;
        BeginGeneration();
    }

    private void OnMenuStart(int seed, bool load)
    {
        _menuDone = true;
        _menu = null;
        if (seed != Seed || (_genTask != null && _genTask.IsFaulted))
        {
            // A different world than the one generated behind the menu: reload with it
            Seed = seed;
            _loadOnStart = load;
            GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
            return;
        }
        _hud.Visible = true;
        _menuFly = false;
        _loadOnStart = load;
        if (_state == State.Generating) return;   // Setup takes the normal path when generation finishes
        StartFromMenu(load);
    }

    /// <summary>The world is already generated and shown behind the menu: place the player and start.</summary>
    private void StartFromMenu(bool load)
    {
        _loadOnStart = false;
        _spawn = Game.World.FindSpawn();
        _dayNight.Day = 4;
        _dayNight.TimeOfDay = 0.32f;
        if (load)
        {
            var loaded = WorldState.Load(Seed);
            if (loaded.HasValue)
            {
                _spawn = loaded.Value.pos;
                _loadedTime = loaded.Value.time;
                _dayNight.Day = Mathf.FloorToInt(loaded.Value.time);
                _dayNight.TimeOfDay = loaded.Value.time - _dayNight.Day;
                _vegetation.Rebuild();   // felled trees etc. from the save
            }
        }
        _weather.Locked = false;
        _weather.Set(WeatherKind.Cloudy, true);
        Game.Focus = _spawn;
        _cineCam?.QueueFree();
        _cineCam = null;
        _loadingCamera = new Camera3D { Far = 12000f, Current = true };
        AddChild(_loadingCamera);
        _loadingCamera.LookAtFromPosition(_spawn + new Vector3(0, 120, 220), _spawn, Vector3.Up);
        _streamTime = 0f;
        _state = State.Streaming;
    }

    /// <summary>
    /// Cinematic camera over the world behind the menu: slow flight towards the evening sun, high above the terrain, with
    /// the horizon in frame and a gentle sway back and forth.
    /// </summary>
    private void UpdateCineCamera(float dt)
    {
        var w = Game.World;
        _cineT += dt;
        var toSun = _dayNight.SunDirection(_weather?.YearT ?? 0.15f, _dayNight.TimeOfDay);
        var fwd0 = new Vector3(toSun.X, 0, toSun.Z);
        fwd0 = fwd0.LengthSquared() > 1e-4f ? fwd0.Normalized() : Vector3.Forward;
        // Slightly to the side of the sun (not straight into it), and a slow turn
        var fwd = fwd0.Rotated(Vector3.Up, 1.2f + Mathf.Sin(_cineT * 0.035f) * 0.2f);
        var side = fwd.Cross(Vector3.Up);
        var pos = _cineCenter - fwd0 * 160f + fwd0 * (_cineT * 5.5f) + side * Mathf.Sin(_cineT * 0.02f) * 60f;
        float ground = 0f;
        for (int i = -1; i <= 6; i++)
        {
            var q = pos + fwd * (i * 40f);
            ground = Mathf.Max(ground, w.GetHeight(q.X, q.Z));
        }
        float target = Mathf.Max(ground, 0f) + 42f;
        _cineH = _cineH <= 0f ? target : Mathf.Lerp(_cineH, target, 1f - Mathf.Exp(-dt / 3f));
        pos.Y = _cineH;
        var look = pos + fwd * 400f;
        look.Y = pos.Y - 55f;
        var xf = Transform3D.Identity.Translated(pos).LookingAt(look, Vector3.Up);
        _cineCam.GlobalTransform = _cineT < 0.1f ? xf : new Transform3D(_cineCam.GlobalBasis.Slerp(xf.Basis, 1f - Mathf.Exp(-dt / 1.2f)).Orthonormalized(), pos);
        Game.Focus = new Vector3(pos.X, 0, pos.Z) + fwd * 60f;
    }

    private void BeginGeneration()
    {
        if (!_loadOnStart) WorldState.Reset();
        _state = State.Generating;
        _sw.Restart();
        int seed = Seed;
        _genTask = Task.Run(() => WorldGenerator.Generate(seed, s => _progress = s));
    }

    /// <summary>Saves the game (campfires, position, time, inventory, skills).</summary>
    private void SaveGame()
    {
        if (_player == null) return;
        WorldState.Fires.Clear();
        foreach (var f in Campfire.All)
            WorldState.Fires.Add(new[] { f.GlobalPosition.X, f.GlobalPosition.Y, f.GlobalPosition.Z, f.Fuel });
        WorldState.Save(Seed, _player.GlobalPosition, _dayNight.Day + _dayNight.TimeOfDay);
        Settings.LastSeed = Seed;
        Settings.Save();
        _hud.Toast(Loc.T("TOAST_SAVED"));
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
            case State.Menu:
                return;
            case State.MenuFly:
                UpdateCineCamera((float)delta);
                Game.Jobs.ProcessCompleted(8.0);
                if (_menu != null && IsInstanceValid(_menu) && !_menu.WorldReady && _cineT > 1.5f
                    && _terrain.IsAreaReady(Game.Focus, 260f))
                    _menu.WorldReady = true;
                break;
            case State.Generating:
                if (!_menuFly) _hud.SetLoading(Loc.F("LOADING_GENERATING", Seed) + "\n" + _progress);
                if (_genTask.IsFaulted)
                {
                    _hud.SetLoading(Loc.T("LOADING_ERROR") + "\n" + _genTask.Exception?.InnerException?.Message);
                    GD.PushError(_genTask.Exception?.ToString());
                    _state = State.Playing;
                    return;
                }
                if (_autoTestDir != null && !_loadShot && _sw.Elapsed.TotalSeconds > 0.8)
                {
                    _loadShot = true;   // screenshot of the loading screen (logo)
                    GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_autoTestDir, "loading.png"));
                }
                if (_genTask.IsCompleted) Setup(_genTask.Result);
                break;

            case State.Streaming:
                _streamTime += (float)delta;
                Game.Focus = _spawn;
                Game.Jobs.ProcessCompleted(14.0);
                _hud.SetLoading(Loc.F("LOADING_TERRAIN", Game.Jobs.PendingCount));
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
        GD.Print($"World generated in {_genSeconds:F1} s (seed {Seed}, river cells {world.RiverCells}, lake cells {world.LakeCells})\n{world.Report}");
        Game.World = world;
        WorldTexture.Publish(world);
        Game.Jobs = new JobQueue(Math.Max(2, System.Environment.ProcessorCount - 2));

        Game.TerrainMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
        TerrainTextures.Apply(Game.TerrainMaterial);
        var waterShader = GD.Load<Shader>("res://shaders/water.gdshader");
        Game.WaterMaterial = new ShaderMaterial { Shader = waterShader };
        Game.WaterMaterial.SetShaderParameter("wave_strength", 0.5f);
        Game.WaterMaterial.SetShaderParameter("shallow_color", new Color(0.18f, 0.40f, 0.37f));
        // Deep fjord water: dark slate green (Norwegian fjords), not tropical blue
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
        if (OS.GetEnvironment("HK_NOGRASS") != "1") AddChild(_grass);   // test switch

        var oceanMat = new ShaderMaterial { Shader = waterShader };
        oceanMat.SetShaderParameter("is_ocean", true);
        AddChild(new Ocean(oceanMat));
        AddChild(new Waterfalls { Name = "Waterfalls" });

        var img = Image.CreateFromData(world.MapSize, world.MapSize, false, Image.Format.Rgb8, world.MapPixels);
        _hud.Map.Texture = ImageTexture.CreateFromImage(img);
        _hud.Map.Seed = Seed;

        if (_menuFly)
        {
            // Behind the menu: golden evening sun, light clouds, cinematic camera over the spawn area
            _weather.Locked = true;
            _weather.Set(WeatherKind.Cloudy, true);
            _dayNight.TimeOfDay = 0.725f;
            _cineCenter = _spawn;
            _cineCam = new Camera3D { Far = 12000f, Current = true, Fov = 62f };
            AddChild(_cineCam);
            _cineT = 0f;
            UpdateCineCamera(0f);
            _state = State.MenuFly;
            return;
        }

        // Temporary camera while the terrain loads
        _loadingCamera = new Camera3D { Far = 12000f, Current = true };
        AddChild(_loadingCamera);
        _loadingCamera.LookAtFromPosition(_spawn + new Vector3(0, 120, 220), _spawn, Vector3.Up);
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
        _hud.Toast(Loc.F("TOAST_WELCOME", _genSeconds));
        if (_loadedTime.HasValue) _hud.Toast(Loc.T("TOAST_LOADED"));
        foreach (var f in WorldState.Fires)
        {
            var fire = new Campfire { Fuel = f[3] };
            AddChild(fire);
            fire.GlobalPosition = new Vector3(f[0], f[1], f[2]);
        }
        if (_autoTestDir != null)
        {
            _weather.Locked = true; _weather.Set(WeatherKind.Broken, true);
            // Record the audio for each step (audio_XX.wav), so the soundscape can be checked/listened to
            _rec = new AudioEffectRecord();
            AudioServer.AddBusEffect(0, _rec);
            _rec.SetRecordingActive(true);
        }
        _pause = new PauseMenu
        {
            OnSave = SaveGame,
            OnMainMenu = () =>
            {
                _menuDone = false;
                GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
            },
        };
        AddChild(_pause);
        _state = State.Playing;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Game.AutoTest) return;
        if (e is not InputEventKey { Pressed: true, Echo: false } k || _state != State.Playing || _player == null) return;
        switch (k.PhysicalKeycode)
        {
            case Key.F5:
                SaveGame();
                break;
            case Key.F6:
                _weather.Set((WeatherKind)(((int)_weather.Kind + 1) % 7));
                _hud.Toast(Loc.F("TOAST_WEATHER_CHANGES", _weather.Description));
                break;
            case Key.F7:
                _dayNight.Day += Weather.DaysPerSeason;
                _hud.Toast(Loc.F("TOAST_SEASON", _weather.SeasonName));
                break;
            case Key.F9:
                if (!WorldState.Exists(Seed)) { _hud.Toast(Loc.T("TOAST_NO_SAVE")); break; }
                _loadOnStart = true;
                GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
                break;
            case Key.F10:
                Seed = (int)(GD.Randi() % 1000000);
                GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
                break;
        }
    }

    // ------------------------------------------------------------ Automated test
    // Run with: Godot.exe --path . -- --autotest=<dir>
    // Takes screenshots from several places in the world, logs performance and quits.
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
        // At the chopping step: start a swing right before the screenshot, and zoom in
        if (_autoStep == 5 && _autoT > 5.55f && !_autoSwing) { _autoSwing = true; _player.CameraDistance = 2.6f; _player.StartSwing(); }
        if (_autoT < 6f) return; // let the streaming settle
        _autoT = 0;
        string name = $"shot_{_autoStep:D2}.png";
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_autoTestDir, name));
        if (_rec != null)
        {
            _rec.SetRecordingActive(false);
            _rec.GetRecording()?.SaveToWav(System.IO.Path.Combine(_autoTestDir, $"audio_{_autoStep:D2}.wav"));
            _rec.SetRecordingActive(true);
        }
        GD.Print($"[autotest] character: {_player.VisualState}");
        GD.Print($"[autotest] footprints: {Footprints.Spawned} {Footprints.LastInfo}");
        GD.Print($"[autotest] {name}: avg FPS {_fpsSum / Math.Max(1, _fpsN):F0}\n{DebugText()}\n");
        _fpsSum = 0; _fpsN = 0;
        var w = Game.World;
        _autoStep++;
        if (_autoStep < _autoFrom) _autoStep = _autoFrom;
        switch (_autoStep)
        {
            case 1: // high above the spawn point, looking out
                _player.ToggleFly();
                _player.GlobalPosition = _spawn + new Vector3(0, 160, 0);
                break;
            case 2: // towards the mountains: find the highest point in a coarse search
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
            case 3: // north (colder)
                _player.GlobalPosition = new Vector3(_spawn.X, 0, -2500);
                _player.GlobalPosition = new Vector3(_spawn.X, w.GetHeight(_spawn.X, -2500) + 60, -2500);
                break;
            case 4: // back on the ground at spawn, close-up of the character from the side
                _player.ToggleFly();
                _player.Teleport(_spawn.X, _spawn.Z);
                _player.CameraDistance = 3f;
                _player.CameraYaw += 1.2f;
                break;
            case 5: // fell the nearest tree
            {
                var tree = Nearest<TreeNode>();
                if (tree == null) { GD.Print("[autotest] no tree found nearby"); break; }
                GD.Print($"[autotest] felling {tree.Def.Name} {tree.GlobalPosition.DistanceTo(_player.GlobalPosition):F1} m away");
                var look = tree.GlobalPosition - _player.GlobalPosition;
                _player.Teleport(tree.GlobalPosition.X - look.Normalized().X * 2.5f, tree.GlobalPosition.Z - look.Normalized().Z * 2.5f);
                // Chop until the tree is almost down (count depends on size and skill)
                for (int i = 0; i < 60 && tree.HealthFraction > 0.2f; i++) tree.Hit(_player, ToolKind.Axe);
                _autoTree = tree;
                _player.CameraYaw = Mathf.Atan2(-(tree.GlobalPosition.X - _player.GlobalPosition.X), -(tree.GlobalPosition.Z - _player.GlobalPosition.Z));
                _player.CameraPitch = -0.15f;
                break;
            }
            case 6: // final chop (the tree falls), pick up all loose items, mine a rock, pick up a branch/stone
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
                GD.Print($"[autotest] picked up {picked} loose, rock mined: {rock != null}, pickup: {pick?.Def.Name}");
                GD.Print("[autotest] inventory: " + string.Join(", ", System.Linq.Enumerable.Select(Inventory.Items, kv => $"{kv.Key}={kv.Value}")));
                break;
            }
            case 7: // ground level in cold heathland (snowy trees, snowy rocks)
                _player.Teleport(_spawn.X + 60f, -2500f);
                _player.CameraDistance = OS.GetEnvironment("HK_LOOKDOWN") == "1" ? 3f : 6f;
                if (OS.GetEnvironment("HK_LOOKDOWN") == "1") _player.CameraPitch = -1.2f;
                break;
            case 8: // sunset over the mountains
                _dayNight.TimeOfDay = 0.74f;
                _player.ToggleFly();
                _player.GlobalPosition = _spawn + new Vector3(0, 140, 0);
                _player.CameraYaw = Mathf.Pi * 0.5f;   // look west
                break;
            case 9: // fjord: deep water with high mountains around
            {
                _dayNight.TimeOfDay = 0.42f;
                var p = FindPoint((x, z) => w.GetHeight(x, z) < -15f && MaxAround(x, z, 350f) > 160f);
                GD.Print($"[autotest] fjord: {p}");
                _player.GlobalPosition = new Vector3(p.X, 90f, p.Z);
                _player.CameraPitch = -0.35f;
                break;
            }
            case 10: // beach
            {
                var p = FindPoint((x, z) => { float h = w.GetHeight(x, z); return h > 0.6f && h < 2.2f && w.GetNormal(x, z, 3f).Y > 0.97f && w.GetHeight(x + 25f, z) < 0f; });
                GD.Print($"[autotest] beach: {p}");
                _player.ToggleFly();
                _player.Teleport(p.X, p.Z);
                _player.CameraPitch = -0.25f; _player.CameraDistance = 7f;
                _player.CameraYaw = -Mathf.Pi * 0.5f;   // towards +X (the sea)
                break;
            }
            case 11: // animal trail
            {
                var p = FindPoint((x, z) => { float h = w.GetHeight(x, z); return w.TrailMask(x, z, h, w.GetNormal(x, z).Y) > 0.9f; });
                GD.Print($"[autotest] trail: {p}");
                _player.Teleport(p.X, p.Z);
                _player.CameraPitch = -0.6f; _player.CameraDistance = 6f;
                break;
            }
            case 12: // mountain foot with cliffs
            {
                var p = FindPoint((x, z) => { float h = w.GetHeight(x, z); return h > 70f && h < 110f && w.GetNormal(x, z, 4f).Y < 0.8f; });
                GD.Print($"[autotest] cliffs: {p}");
                _player.ToggleFly();
                _player.GlobalPosition = new Vector3(p.X, w.GetHeight(p.X, p.Z) + 25f, p.Z + 40f);
                _player.CameraPitch = -0.15f; _player.CameraDistance = 8f; _player.CameraYaw = 0f;
                break;
            }
            case 13: // walk backwards on the beach towards the camera: footprints behind the character
            {
                _player.ToggleFly();
                var p = FindPoint((x, z) => { float h = w.GetHeight(x, z); return h > 0.8f && h < 2.0f && w.GetNormal(x, z, 4f).Y > 0.985f && w.GetHeight(x, z + 12f) > 0.6f && w.GetHeight(x, z - 12f) > 0.6f; });
                GD.Print($"[autotest] footprints: {p}");
                _player.Teleport(p.X, p.Z);
                _player.CameraYaw = 0f; _player.CameraPitch = -1.05f; _player.CameraDistance = 7f;
                _player.DebugInput = new Vector2(0, 0.45f);   // slow walk towards the camera
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
            case 14: // slope: feet on uneven ground, seen from the side
            {
                _player.DebugInput = null;
                var p = FindPoint((x, z) => { float h = w.GetHeight(x, z); var n = w.GetNormal(x, z, 1.5f); return h > 5f && h < 60f && n.Y > 0.78f && n.Y < 0.86f; });
                GD.Print($"[autotest] slope: {p}");
                _player.Teleport(p.X, p.Z);
                var n2 = w.GetNormal(p.X, p.Z, 1.5f);
                // Look along the slope (from the side), so the height difference between the feet is visible
                _player.CameraYaw = Mathf.Atan2(n2.X, n2.Z) + Mathf.Pi * 0.5f;
                _player.CameraPitch = -0.1f; _player.CameraDistance = 2.6f;
                break;
            }
            case 15: // storm with rain at the spawn point (daytime)
            {
                _dayNight.TimeOfDay = 0.45f;
                _weather.Set(WeatherKind.Storm, true);
                _player.Teleport(_spawn.X, _spawn.Z);
                _player.CameraPitch = -0.1f; _player.CameraDistance = 5f;
                break;
            }
            case 16: // autumn: yellow/red deciduous trees, flying a little above a deciduous forest
            {
                _dayNight.Day = Weather.DaysPerSeason * 2 + 3;
                _dayNight.TimeOfDay = 0.5f;
                _weather.Set(WeatherKind.Clear, true);
                var p = FindPoint((x, z) => { float t = w.GetTemp(x, z); return t > 0.55f && w.GetMoist(x, z) > 0.5f && w.GetHeight(x, z) > 5f; });
                GD.Print($"[autotest] autumn forest: {p}");
                _player.Teleport(p.X, p.Z);
                _player.CameraPitch = -0.25f; _player.CameraDistance = 9f;
                break;
            }
            case 17: // winter: snow cover and snowfall
            {
                _dayNight.Day = Weather.DaysPerSeason * 3 + 3;
                _dayNight.TimeOfDay = 0.5f;
                _weather.Set(WeatherKind.Rain, true);
                _player.Teleport(_spawn.X, _spawn.Z);
                _player.CameraPitch = -0.2f; _player.CameraDistance = 7f;
                break;
            }
            case 18: // winter night with aurora, looking north
            {
                _weather.Set(WeatherKind.Clear, true);
                _dayNight.TimeOfDay = 0.98f;
                _dayNight.ForceAurora = true;
                _player.ToggleFly();
                _player.GlobalPosition = _spawn + new Vector3(0, 40, 0);
                _player.CameraYaw = 0f;        // the camera looks towards -Z (north)
                _player.CameraPitch = 0.35f;
                break;
            }
            case 19: // river with current, seen obliquely from above (summer, day)
            {
                _dayNight.ForceAurora = false;
                _dayNight.Day = Weather.DaysPerSeason + 2;
                _dayNight.TimeOfDay = 0.45f;
                _weather.Set(WeatherKind.Cloudy, true);
                var p = FindPoint((x, z) => { float fx = w.SampleFlow(x, z).Length(); return fx > 1.0f && w.GetLakeWater(x, z) > w.GetHeight(x, z) + 0.5f; });
                GD.Print($"[autotest] river: {p} flow {w.SampleFlow(p.X, p.Z)}");
                _player.GlobalPosition = new Vector3(p.X + 14f, w.GetLakeWater(p.X, p.Z) + 9f, p.Z + 14f);
                _player.CameraYaw = Mathf.Pi * 0.25f; _player.CameraPitch = -0.6f; _player.CameraDistance = 4f;
                break;
            }
            case 20: // storm at the beach: waves and breakers
            {
                _weather.Set(WeatherKind.Storm, true);
                var p = FindPoint((x, z) => { float h = w.GetHeight(x, z); return h > 0.6f && h < 2.2f && w.GetNormal(x, z, 3f).Y > 0.97f && w.GetHeight(x + 25f, z) < 0f; });
                _player.GlobalPosition = new Vector3(p.X - 10f, 7f, p.Z);
                _player.CameraYaw = -Mathf.Pi * 0.5f; _player.CameraPitch = -0.2f; _player.CameraDistance = 4f;
                break;
            }
            case 21: // largest waterfall near the spawn point
            {
                _weather.Set(WeatherKind.Clear, true);
                Cascade best = null; float bs = 0f;
                foreach (var c in w.Cascades)
                {
                    float score = c.Drop * c.Widths[0] / (1f + c.Top.DistanceTo(_spawn) / 1500f);
                    if (score > bs) { bs = score; best = c; }
                }
                if (best == null) break;
                GD.Print($"[autotest] waterfall: top {best.Top} bottom {best.Bottom} drop {best.Drop:F0} m, {best.Points.Count} points");
                SetFly(true);
                _dayNight.TimeOfDay = 0.45f;
                // Stand downstream and look up the fall
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
            case 22: // campfire at night (early autumn), close-up
            {
                _dayNight.Day = Weather.DaysPerSeason * 2;
                _dayNight.TimeOfDay = 0.94f;
                _weather.Set(WeatherKind.Cloudy, true);
                SetFly(false);
                _player.Teleport(_spawn.X, _spawn.Z);
                var fire = _player.BuildCampfire(free: true);
                GD.Print($"[autotest] campfire built: {fire != null}");
                _player.CameraDistance = 5f; _player.CameraPitch = -0.3f;
                if (fire != null)
                {
                    var to = fire.GlobalPosition - _player.GlobalPosition;
                    _player.CameraYaw = Mathf.Atan2(-to.X, -to.Z) + 0.5f;
                }
                break;
            }
            case 23: // close-up of a tree trunk in conifer forest (bark, moss, roots)
            {
                SetFly(false);
                _dayNight.Day = 4; _dayNight.TimeOfDay = 0.45f;
                _weather.Set(WeatherKind.Cloudy, true);
                var p = FindPoint((x, z) => w.GetBiome(x, z) == Biome.PineForest && w.GetNormal(x, z, 3f).Y > 0.95f);
                _player.Teleport(p.X, p.Z);
                break;
            }
            case 24: // ... and aim at the nearest spruce/pine once the nearby trees are promoted
                AimAtTree(t => t.Def.Kind is PropKind.Pine or PropKind.Spruce);
                break;
            case 25: // close-up of a boulder
            {
                var rock = Nearest<RockNode>();
                GD.Print($"[autotest] rock: {rock?.GlobalPosition}");
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
            case 26: // birch (teleport to it, aim in the next step)
                GoNearTree(i => i.Kind == PropKind.Birch && !i.Cold);
                break;
            case 27:
                AimAtTree(t => t.Def.Kind == PropKind.Birch);
                break;
            case 28: // dead pine
                GoNearTree(i => i.Kind == PropKind.DeadTree && !i.Cold);
                break;
            case 29:
                AimAtTree(t => t.Def.Kind == PropKind.DeadTree, t => !t.Inst.Cold);
                break;
            case 30: // snowy trees in a cold zone (fly there first, so the cells there load)
            {
                var p = FindPoint((x, z) => w.GetTemp(x, z) < 0.2f && w.GetHeight(x, z) > 5f && w.GetHeight(x, z) < 150f && w.GetNormal(x, z, 3f).Y > 0.93f);
                _player.Teleport(p.X, p.Z);
                _autoT = 3f;   // shorter wait, the next step finds the tree
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

    /// <summary>Autotest: teleport right next to the nearest matching generated tree (promoted by the next step).</summary>
    private void GoNearTree(Func<PropInstance, bool> ok)
    {
        var inst = _vegetation.FindNearest(_player.GlobalPosition, ok);
        if (inst == null) { GD.Print("[autotest] tree not found (instance)"); return; }
        _player.Teleport(inst.Pos.X + 3f, inst.Pos.Z + 3f);
    }

    /// <summary>Autotest: aim the camera at the nearest matching tree (trunk in frame).</summary>
    private void AimAtTree(Func<TreeNode, bool> kind, Func<TreeNode, bool> extra = null)
    {
        TreeNode tree = null; float bd = float.MaxValue;
        foreach (var nd in _vegetation.GetChildren())
            if (nd is TreeNode tn && kind(tn) && (extra == null || extra(tn)))
            {
                float d = tn.GlobalPosition.DistanceTo(_player.GlobalPosition);
                if (d < bd) { bd = d; tree = tn; }
            }
        if (tree == null) { GD.Print("[autotest] tree not found"); return; }
        var dir = (_player.GlobalPosition - tree.GlobalPosition) with { Y = 0 };
        if (dir.LengthSquared() < 0.01f) dir = Vector3.Back;
        var at = tree.GlobalPosition + dir.Normalized() * 2.4f;
        _player.Teleport(at.X, at.Z);
        var to = tree.GlobalPosition - at;
        _player.CameraYaw = Mathf.Atan2(-to.X, -to.Z) + 0.5f;   // character to the side, trunk in the center
        _player.CameraPitch = -0.15f; _player.CameraDistance = 1.6f;
        GD.Print($"[autotest] trunk: {tree.Def.Name}{(tree.Inst.Cold ? " (snow)" : "")}");
    }

    /// <summary>Searches in a spiral from the spawn point for a point that satisfies the predicate (autotest).</summary>
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
            $"FPS {Engine.GetFramesPerSecond():F0}  (process {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:F1} ms · " +
            $"physics {Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000:F1} ms)\n" +
            $"Render CPU {RenderingServer.ViewportGetMeasuredRenderTimeCpu(GetViewport().GetViewportRid()):F1} ms · " +
            $"GPU {RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid()):F1} ms\n" +
            $"Draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):F0} · " +
            $"triangles {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1000:F0}k\n" +
            $"VRAM {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576:F0} MB · " +
            $"RAM {Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576:F0} MB (+.NET {GC.GetTotalMemory(false) / 1048576} MB)\n" +
            $"Terrain: {_terrain.ChunkCount} chunks ({_terrain.PendingCount} pending)\n" +
            $"Vegetation: {_vegetation.CellCount} cells, {_vegetation.InstanceCount} obj, {_vegetation.PromotedCount} active\n" +
            $"Grass: {_grass.CellCount} cells · Collision: {_collision.BodyCount}\n" +
            $"Jobs: {Game.Jobs.PendingCount} queued, {Game.Jobs.RunningCount} running\n" +
            $"Pos {p.X:F0}, {p.Y:F1}, {p.Z:F0}  (ground {h:F1} m)\n" +
            $"Zone: {Biomes.Name(w.GetBiome(p.X, p.Z))} · temp {w.GetTemp(p.X, p.Z):F2} · moisture {w.GetMoist(p.X, p.Z):F2}\n" +
            $"{_weather.SeasonName}, day {_weather.DayOfSeason} · {_weather.Description} · wind {_weather.Wind * 20f:F0} m/s" +
            (_dayNight.Aurora > 0.05f ? " · aurora" : "") + "\n" +
            $"Seed {Seed} · Time {hours:D2}:{mins:D2}" + (_player?.Flying == true ? " · FLYING" : "") + (_player?.Swimming == true ? " · SWIMMING" : "");
    }
}
