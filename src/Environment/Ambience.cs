using System;
using System.Collections.Generic;
using Godot;
using Heimskringla.Core;
using Heimskringla.World;

namespace Heimskringla.Environment;

/// <summary>
/// Prosedyrisk lydbilde (ingen lydfiler): vind, regn, torden, elv/foss, brenninger, fugler og sirisser.
/// Alt syntetiseres i sanntid i en AudioStreamGenerator og blandes etter vær, tid, årstid og hvor
/// spilleren er. Vann- og kystlyder panoreres mot kilden.
/// </summary>
public partial class Ambience : Node
{
    private const int Rate = 22050;
    private AudioStreamGeneratorPlayback _pb;
    private readonly Random _rng = new(1234);

    // Målnivåer (oppdateres sjeldent) og glidende nivåer (per sample)
    private float _tWind, _tRain, _tRiver, _tFalls, _tSurf, _tBirds, _tCrickets;
    private float _gWind, _gRain, _gRiver, _gFalls, _gSurf;
    private float _panWater, _panSurf, _gPanWater, _gPanSurf;
    private float _windCut = 400f;
    private float _scanTimer;

    // Filtertilstander
    private float _brownW, _lpW1, _lpW2, _bpW1, _bpW2;
    private float _hpR, _lpR, _prevR;
    private float _brownRiv, _lpRiv, _bpF1, _bpF2;
    private float _brownS, _lpS, _swellPhase;
    private double _time;

    // Hendelser
    private readonly List<Voice> _voices = new();
    private float _birdTimer = 2f, _thunderDelay = -1f, _lastFlash;
    private float _cricketPhase, _cricketEnv;

    private sealed class Voice
    {
        public float T, Dur, F0, F1, Amp, Pan, Phase;
        public int Kind;      // 0 = fugletone, 1 = torden, 2 = regndråpe
        public float Lp;      // filtertilstand (torden)
    }

    public override void _Ready()
    {
        var gen = new AudioStreamGenerator { MixRate = Rate, BufferLength = 0.25f };
        var player = new AudioStreamPlayer { Stream = gen, VolumeDb = -4f, Name = "AmbiencePlayer" };
        AddChild(player);
        player.Play();
        _pb = (AudioStreamGeneratorPlayback)player.GetStreamPlayback();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _scanTimer -= dt;
        if (_scanTimer <= 0f) { _scanTimer = 0.4f; ScanSurroundings(); }
        UpdateTargets();
        Fill();
    }

    // ------------------------------------------------------------ Omgivelser
    private void ScanSurroundings()
    {
        var w = Game.World;
        var cam = GetViewport()?.GetCamera3D();
        if (w == null || cam == null) return;
        var p = cam.GlobalPosition;
        var right = cam.GlobalBasis.X;

        // Elver: strøm i nærheten (10 m-rutenett, 60 m radius)
        float river = 0f; Vector3 riverDir = Vector3.Zero;
        for (int dz = -6; dz <= 6; dz++)
        for (int dx = -6; dx <= 6; dx++)
        {
            float x = p.X + dx * 10f, z = p.Z + dz * 10f;
            float sp = w.SampleFlow(x, z).Length();
            if (sp < 0.15f) continue;
            float d = Mathf.Sqrt(dx * dx + dz * dz) * 10f + Mathf.Abs(p.Y - w.GetHeight(x, z)) * 0.5f;
            float c = sp * 0.5f / (1f + d * d / 300f);
            river += c;
            riverDir += new Vector3(dx, 0, dz) * c;
        }
        // Fossefall: nærmeste kjedepunkt, vektet med fallhøyden
        float falls = 0f; Vector3 fallsDir = Vector3.Zero;
        foreach (var c in w.Cascades)
        {
            if (Mathf.Abs(c.Top.X - p.X) > 400f || Mathf.Abs(c.Top.Z - p.Z) > 400f) continue;
            float loud = Mathf.Clamp(c.Drop / 25f, 0.2f, 1.5f);
            foreach (var q in c.Points)
            {
                float d = q.DistanceTo(p);
                float g = loud / (1f + d * d / 2500f);
                if (g > 0.01f) { falls = Mathf.Max(falls, g); fallsDir += (q - p).Normalized() * g; }
            }
        }
        _tRiver = Mathf.Clamp(river * 0.6f, 0f, 1f);
        _tFalls = Mathf.Clamp(falls, 0f, 1f);
        var wd = riverDir + fallsDir * 3f;
        _panWater = wd.LengthSquared() > 1e-4f ? Mathf.Clamp(wd.Normalized().Dot(right), -1f, 1f) * 0.7f : 0f;

        // Kyst: hav innen 160 m, men bare når det også er land nær (brenninger, ikke åpent hav)
        float sea = 0f, land = 0f; Vector3 seaDir = Vector3.Zero;
        for (int i = 0; i < 16; i++)
        {
            float a = i * Mathf.Tau / 16f;
            for (int r = 1; r <= 4; r++)
            {
                float rr = r * 40f;
                var q = new Vector3(p.X + Mathf.Cos(a) * rr, 0, p.Z + Mathf.Sin(a) * rr);
                if (w.GetHeight(q.X, q.Z) < -0.5f) { sea += 1f / r; seaDir += new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) / r; }
                else land += 1f;
            }
        }
        float coast = Mathf.Clamp(sea / 8f, 0f, 1f) * Mathf.Clamp(land / 10f, 0f, 1f);
        float height = Mathf.Max(0f, p.Y - 10f);
        _tSurf = coast / (1f + height / 40f);
        _panSurf = seaDir.LengthSquared() > 1e-4f ? Mathf.Clamp(seaDir.Normalized().Dot(right), -1f, 1f) * 0.6f : 0f;

        // Fugler: dag, ikke vinter, ikke regn/storm; mest i skog
        var biome = w.GetBiome(p.X, p.Z);
        float forest = biome is Biome.Forest or Biome.PineForest ? 1f : biome is Biome.Meadow or Biome.Swamp ? 0.5f : 0.1f;
        var dn = GetParent().GetNodeOrNull<DayNight>("DayNight");
        var wx = Weather.Current;
        float day = dn != null ? Biomes.SmoothStep(0.22f, 0.3f, dn.TimeOfDay) * (1f - Biomes.SmoothStep(0.8f, 0.88f, dn.TimeOfDay)) : 1f;
        float season = wx == null ? 1f : wx.Season switch { 0 => 1f, 1 => 0.9f, 2 => 0.35f, _ => 0.05f };
        float calm = wx == null ? 1f : (1f - wx.Precip) * (1f - Mathf.Max(0f, wx.Wind - 0.5f));
        _tBirds = forest * day * season * calm / (1f + height / 30f);
        float warmNight = (1f - day) * (wx == null ? 0f : wx.Season == 1 ? 1f : wx.Season == 0 ? 0.3f : 0f) * calm;
        _tCrickets = warmNight * (biome is Biome.Meadow or Biome.Forest ? 1f : 0.3f) / (1f + height / 20f);
    }

    private void UpdateTargets()
    {
        var wx = Weather.Current;
        var cam = GetViewport()?.GetCamera3D();
        float alt = cam != null ? Mathf.Max(0f, cam.GlobalPosition.Y - 20f) : 0f;
        if (wx != null)
        {
            // Vinden høres mer høyt oppe
            _tWind = Mathf.Clamp(0.08f + wx.Wind * 0.75f, 0f, 1f) * (1f + Mathf.Min(alt / 150f, 0.6f));
            _windCut = 250f + wx.Wind * 900f;
            _tRain = wx.PrecipIsSnow ? 0f : wx.Precip;
            // Lyn → torden etter en stund (lyden bruker tid)
            if (wx.Flash > 0.95f && _lastFlash < 0.95f) _thunderDelay = 0.6f + (float)_rng.NextDouble() * 2.5f;
            _lastFlash = wx.Flash;
        }
    }

    // ------------------------------------------------------------ Syntese
    private float White() => (float)_rng.NextDouble() * 2f - 1f;

    private void Fill()
    {
        if (_pb == null) return;
        int frames = _pb.GetFramesAvailable();
        if (frames <= 0) return;
        float dt = 1f / Rate;
        float k = 1f - Mathf.Exp(-dt / 0.6f);   // glatting av nivåer (~0.6 s)
        var buf = new Vector2[frames];

        // Hendelser planlegges per bufferstykke
        float chunk = frames * dt;
        _birdTimer -= chunk;
        if (_birdTimer <= 0f)
        {
            _birdTimer = 0.6f + (float)_rng.NextDouble() * 4f / Mathf.Max(_tBirds, 0.05f);
            if (_tBirds > 0.05f) SpawnBirdSong();
        }
        if (_thunderDelay > 0f)
        {
            _thunderDelay -= chunk;
            if (_thunderDelay <= 0f) _voices.Add(new Voice { Kind = 1, Dur = 5f + (float)_rng.NextDouble() * 3f, Amp = 0.9f, Pan = (float)_rng.NextDouble() - 0.5f });
        }
        // Regndråper (enkeltklikk mot blader/tak)
        int drops = (int)(_gRain * 60f * chunk + _rng.NextDouble());
        for (int i = 0; i < drops; i++)
            _voices.Add(new Voice { Kind = 2, T = -(float)_rng.NextDouble() * chunk, Dur = 0.012f, F0 = 2500f + (float)_rng.NextDouble() * 3500f, Amp = 0.12f * _gRain, Pan = (float)_rng.NextDouble() * 1.6f - 0.8f });

        for (int i = 0; i < frames; i++)
        {
            _time += dt;
            _gWind += (_tWind - _gWind) * k;
            _gRain += (_tRain - _gRain) * k;
            _gRiver += (_tRiver - _gRiver) * k;
            _gFalls += (_tFalls - _gFalls) * k;
            _gSurf += (_tSurf - _gSurf) * k;
            _gPanWater += (_panWater - _gPanWater) * k;
            _gPanSurf += (_panSurf - _gPanSurf) * k;
            float l = 0f, r = 0f;

            // Vind: brunstøy, båndpass som sveiper med kastene
            float wn = White();
            _brownW = Mathf.Clamp(_brownW + wn * 0.02f, -1f, 1f) * 0.998f;
            float gust = 0.55f + 0.45f * Mathf.Sin((float)_time * 0.23f) * Mathf.Sin((float)_time * 0.071f + 1.3f);
            float cut = _windCut * (0.6f + gust * 0.8f);
            float f = 2f * Mathf.Sin(Mathf.Pi * Mathf.Min(cut, 4000f) / Rate);
            // Tilstandsvariabelfilter (båndpass)
            _bpW1 += f * _bpW2;
            float hp = wn - _bpW1 - 0.35f * _bpW2;
            _bpW2 += f * hp;
            float wind = (_bpW2 * 0.35f + _brownW * 0.8f) * _gWind * (0.4f + gust * 0.8f);
            l += wind * (0.9f + 0.1f * Mathf.Sin((float)_time * 0.3f));
            r += wind * (0.9f + 0.1f * Mathf.Cos((float)_time * 0.27f));

            // Regn: høypassert hvit støy
            float rn = White();
            _hpR = 0.97f * (_hpR + rn - _prevR); _prevR = rn;
            _lpR += (_hpR - _lpR) * 0.5f;
            float rain = _lpR * 0.32f * _gRain;
            l += rain; r += rain * 0.95f;

            // Elv: dyp brunstøy med svak "gurgling"; foss: bredbåndet brus
            float vn = White();
            _brownRiv = Mathf.Clamp(_brownRiv + vn * 0.03f, -1f, 1f) * 0.997f;
            _lpRiv += (vn - _lpRiv) * 0.12f;
            float gurgle = 0.75f + 0.25f * Mathf.Sin((float)_time * 7.3f + Mathf.Sin((float)_time * 1.7f) * 3f);
            float riverS = (_brownRiv * 0.6f + _lpRiv * 0.5f * gurgle) * _gRiver * 0.5f;
            float ff = 2f * Mathf.Sin(Mathf.Pi * 1400f / Rate);
            _bpF1 += ff * _bpF2;
            float fh = vn - _bpF1 - 0.9f * _bpF2;
            _bpF2 += ff * fh;
            float fallsS = (_bpF2 * 0.45f + _brownRiv * 0.5f) * _gFalls * 0.7f;
            float water = riverS + fallsS;
            l += water * (1f - _gPanWater) * 0.8f;
            r += water * (1f + _gPanWater) * 0.8f;

            // Brenninger: lavfrekvent sus som sveller og trekker seg tilbake
            _swellPhase += dt / 7.5f;
            float sw = Mathf.Sin(_swellPhase * Mathf.Tau);
            float env = 0.25f + 0.75f * Mathf.Max(0f, sw) * Mathf.Max(0f, sw);
            _brownS = Mathf.Clamp(_brownS + White() * 0.025f, -1f, 1f) * 0.998f;
            _lpS += (White() - _lpS) * (0.05f + env * 0.15f);
            float surf = (_brownS * 0.6f + _lpS * 0.6f) * env * _gSurf * 0.7f;
            l += surf * (1f - _gPanSurf); r += surf * (1f + _gPanSurf);

            // Sirisser: 4.6 kHz, pulser i grupper
            if (_tCrickets > 0.02f)
            {
                _cricketPhase += 4600f * dt;
                float group = (float)(_time * 2.1 % 1.0);
                float pulse = group < 0.3f ? (Mathf.Sin((float)_time * Mathf.Tau * 32f) > 0f ? 1f : 0f) : 0f;
                _cricketEnv += (pulse - _cricketEnv) * 0.05f;
                float cr = Mathf.Sin(_cricketPhase * Mathf.Tau) * _cricketEnv * 0.05f * _tCrickets;
                l += cr * 0.7f; r += cr;
            }

            // Hendelser
            for (int v = _voices.Count - 1; v >= 0; v--)
            {
                var vo = _voices[v];
                vo.T += dt;
                if (vo.T < 0f) continue;
                if (vo.T > vo.Dur) { _voices.RemoveAt(v); continue; }
                float x = vo.T / vo.Dur;
                float s = 0f;
                switch (vo.Kind)
                {
                    case 0: // fugletone: frekvenssveip med harmonisk
                    {
                        float fr = Mathf.Lerp(vo.F0, vo.F1, x);
                        vo.Phase += fr * dt;
                        float e = Mathf.Sin(Mathf.Pi * x);
                        s = (Mathf.Sin(vo.Phase * Mathf.Tau) + 0.25f * Mathf.Sin(vo.Phase * 2f * Mathf.Tau)) * e * e * vo.Amp;
                        break;
                    }
                    case 1: // torden: lavpasset støy, rask start, lang hale med rumling
                    {
                        float e = Mathf.Min(x * 25f, 1f) * Mathf.Pow(1f - x, 2.2f) * (0.6f + 0.4f * Mathf.Sin(vo.T * 3.1f + Mathf.Sin(vo.T * 0.9f) * 4f));
                        vo.Lp += (White() - vo.Lp) * 0.02f;
                        s = vo.Lp * 6f * e * vo.Amp;
                        break;
                    }
                    case 2: // regndråpe: kort dempet sinus
                    {
                        vo.Phase += vo.F0 * dt;
                        s = Mathf.Sin(vo.Phase * Mathf.Tau) * (1f - x) * (1f - x) * vo.Amp;
                        break;
                    }
                }
                l += s * (1f - vo.Pan);
                r += s * (1f + vo.Pan);
            }

            buf[i] = new Vector2(SoftClip(l), SoftClip(r));
        }
        _pb.PushBuffer(buf);
    }

    private static float SoftClip(float x) => x / (1f + Mathf.Abs(x) * 0.6f);

    /// <summary>Et lite fuglekvitter: 2–7 toner fra en tilfeldig "art" (tonehøyde og mønster).</summary>
    private void SpawnBirdSong()
    {
        int species = _rng.Next(4);
        float pan = (float)_rng.NextDouble() * 1.4f - 0.7f;
        float amp = (0.025f + (float)_rng.NextDouble() * 0.03f) * Mathf.Clamp(_tBirds, 0.2f, 1f);
        int notes = 2 + _rng.Next(6);
        float t = 0f;
        float baseF = species switch { 0 => 3200f, 1 => 4200f, 2 => 2400f, _ => 5200f };
        for (int i = 0; i < notes; i++)
        {
            float dur = species switch { 0 => 0.09f, 1 => 0.05f, 2 => 0.18f, _ => 0.04f } * (0.8f + (float)_rng.NextDouble() * 0.4f);
            float f0 = baseF * (0.85f + (float)_rng.NextDouble() * 0.3f);
            float f1 = species switch { 0 => f0 * 1.3f, 1 => f0 * 0.8f, 2 => f0 * (i % 2 == 0 ? 1.25f : 0.8f), _ => f0 * 1.1f };
            _voices.Add(new Voice { Kind = 0, T = -t, Dur = dur, F0 = f0, F1 = f1, Amp = amp, Pan = pan });
            t += dur + (species == 2 ? 0.08f : 0.03f) + (float)_rng.NextDouble() * 0.05f;
        }
    }
}
