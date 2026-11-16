using System;
using Godot;
using Heimskringla.Vegetation;

namespace Heimskringla.Gameplay;

/// <summary>Felles grensesnitt for spillerens utseende (animert figur eller kodegenerert reserve). Ser mot -Z.</summary>
public abstract partial class PlayerVisual : Node3D
{
    /// <summary>Varighet for ett hugg i sekunder.</summary>
    public virtual float SwingDuration => 0.65f;
    /// <summary>Andel (0..1) av hugget der treffet skjer.</summary>
    public virtual float HitMoment => 0.5f;

    public abstract void SetTool(ToolKind tool);

    /// <param name="speed">horisontal fart (m/s)</param>
    /// <param name="swing">0..1 under hugg, ellers negativ</param>
    public abstract void Animate(float speed, bool grounded, bool swimming, float swing, float dt);

    /// <summary>Kort animasjon når noe plukkes opp.</summary>
    public virtual void PlayPickup() { }

    public virtual string DebugState => "";

    /// <summary>Fotsteg: side (0=v,1=h), posisjon på bakken, retning (yaw).</summary>
    public event Action<int, Vector3, float> Footstep;
    protected void RaiseFootstep(int side, Vector3 pos, float yaw) => Footstep?.Invoke(side, pos, yaw);

    /// <summary>Spillerens kropp, så fotstråler ikke treffer den.</summary>
    public Rid OwnerRid;
}
