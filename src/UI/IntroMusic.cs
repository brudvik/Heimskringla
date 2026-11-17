using System;
using Godot;

namespace Heimskringla.UI;

/// <summary>
/// Slow, dark pad for the menu: D minor with fifth and octave (detuned saws, breathing
/// low-pass), a deep root note, and now and then a distant "lur horn" (low swelling fifth).
/// </summary>
public partial class IntroMusic : Node
{
    public float Level;
    private const int Rate = 22050;
    private AudioStreamGeneratorPlayback _pb;
    private double _t;
    private readonly double[] _ph = new double[12];
    private float _lp1, _lp2, _lvl, _hornT = -1f, _hornPh, _hornLp, _nextHorn = 9f;
    private int _chord;
    private float _chordT;
    private readonly Random _rng = new(7);

    // Chords (Hz, root + three voices): Dm, Bb, F, C — calm Nordic progression
    private static readonly float[][] Chords =
    {
        new[] { 73.42f, 146.83f, 174.61f, 220.00f },
        new[] { 58.27f, 116.54f, 174.61f, 233.08f },
        new[] { 87.31f, 130.81f, 174.61f, 220.00f },
        new[] { 65.41f, 130.81f, 196.00f, 261.63f },
    };
    private readonly float[] _freq = new float[4];

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        var gen = new AudioStreamGenerator { MixRate = Rate, BufferLength = 0.3f };
        var p = new AudioStreamPlayer { Stream = gen, VolumeDb = -6f };
        AddChild(p);
        p.Play();
        _pb = (AudioStreamGeneratorPlayback)p.GetStreamPlayback();
        Array.Copy(Chords[0], _freq, 4);
    }

    public override void _Process(double delta)
    {
        if (_pb == null) return;
        int frames = _pb.GetFramesAvailable();
        if (frames <= 0) return;
        var buf = new Vector2[frames];
        float dt = 1f / Rate;
        for (int i = 0; i < frames; i++)
        {
            _t += dt;
            _lvl += (Level - _lvl) * 0.0002f;
            _chordT += dt;
            if (_chordT > 11f) { _chordT = 0f; _chord = (_chord + 1) % Chords.Length; }
            var target = Chords[_chord];
            for (int v = 0; v < 4; v++) _freq[v] += (target[v] - _freq[v]) * 0.00004f;   // slow glide between chords

            float l = 0f, r = 0f;
            for (int v = 0; v < 4; v++)
            {
                for (int d = 0; d < 3; d++)
                {
                    int k = v * 3 + d;
                    float det = 1f + (d - 1) * 0.0035f;
                    _ph[k] += _freq[v] * det * dt;
                    if (_ph[k] > 1.0) _ph[k] -= 1.0;
                    float saw = (float)(_ph[k] * 2.0 - 1.0);
                    float amp = v == 0 ? 0.32f : 0.14f;
                    float pan = (d - 1) * 0.5f + (v - 1.5f) * 0.15f;
                    l += saw * amp * (1f - pan * 0.5f);
                    r += saw * amp * (1f + pan * 0.5f);
                }
            }
            // Low-pass that slowly "breathes" (opens and closes over ~14 s)
            float cut = 380f + 260f * (0.5f + 0.5f * Mathf.Sin((float)_t * 0.45f));
            float a = 1f - Mathf.Exp(-Mathf.Tau * cut / Rate);
            _lp1 += (l - _lp1) * a;
            _lp2 += (r - _lp2) * a;
            float sl = _lp1, sr = _lp2;

            // Distant lur horn: a low tone that swells and fades, every now and then
            _nextHorn -= dt;
            if (_nextHorn <= 0f && _hornT < 0f) { _hornT = 0f; _nextHorn = 18f + (float)_rng.NextDouble() * 14f; }
            if (_hornT >= 0f)
            {
                _hornT += dt;
                float env = Mathf.Sin(Mathf.Pi * Mathf.Clamp(_hornT / 5.5f, 0f, 1f));
                env *= env;
                _hornPh += 110f * (1f + 0.004f * Mathf.Sin(_hornT * 5f)) * dt;
                if (_hornPh > 1f) _hornPh -= 1f;
                float sq = _hornPh < 0.5f ? 1f : -1f;
                _hornLp += (sq - _hornLp) * 0.06f;
                float hs = _hornLp * env * 0.12f;
                sl += hs * 0.8f; sr += hs;
                if (_hornT > 5.5f) _hornT = -1f;
            }
            float g = _lvl * 0.35f;
            buf[i] = new Vector2(Mathf.Clamp(sl * g, -1f, 1f), Mathf.Clamp(sr * g, -1f, 1f));
        }
        _pb.PushBuffer(buf);
    }
}
