using System;
using Godot;
using Heimskringla.Vegetation;

namespace Heimskringla.Gameplay;

/// <summary>Common interface for the player's appearance (animated character or procedurally generated fallback). Faces -Z.</summary>
public abstract partial class PlayerVisual : Node3D
{
    /// <summary>Duration of one chop in seconds.</summary>
    public virtual float SwingDuration => 0.65f;
    /// <summary>Fraction (0..1) of the chop at which the hit happens.</summary>
    public virtual float HitMoment => 0.5f;

    public abstract void SetTool(ToolKind tool);

    /// <param name="speed">horizontal speed (m/s)</param>
    /// <param name="swing">0..1 during a chop, otherwise negative</param>
    public abstract void Animate(float speed, bool grounded, bool swimming, float swing, float dt);

    /// <summary>Short animation when something is picked up.</summary>
    public virtual void PlayPickup() { }

    public virtual string DebugState => "";

    /// <summary>Footstep: side (0=left,1=right), position on the ground, heading (yaw).</summary>
    public event Action<int, Vector3, float> Footstep;
    protected void RaiseFootstep(int side, Vector3 pos, float yaw) => Footstep?.Invoke(side, pos, yaw);

    /// <summary>The player's body, so foot rays do not hit it.</summary>
    public Rid OwnerRid;
}
