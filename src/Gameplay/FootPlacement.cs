using System;
using Godot;
using Heimskringla.Core;

namespace Heimskringla.Gameplay;

/// <summary>
/// Fotplassering: kjøres i skjelettet etter animasjonen og før TwoBoneIK3D.
/// Leser fotposisjonene fra animasjonen, finner bakken under hver fot med en stråle,
/// flytter IK-målene dit og senker hoftene slik at den laveste foten når ned.
/// Registrerer også fotsteg (brukes til fotspor, plask og lyd).
/// </summary>
public partial class FootPlacement : SkeletonModifier3D
{
    public Node3D Body;                    // figurens rot (bakkenivå ved føttene, ser mot -Z)
    public Marker3D TargetL, TargetR, PoleL, PoleR;
    public Rid ExcludeRid;
    /// <summary>0 = ren animasjon, 1 = full fotplassering. Styres av CharacterModel.</summary>
    public float Weight;
    public event Action<int, Vector3, float> Footstep;   // side (0=v,1=h), posisjon, retning (yaw)

    private int _footL = -1, _footR, _calfL, _calfR, _pelvis, _pelvisParent;
    private float _offL, _offR, _pelvisOff;
    private bool _contactL = true, _contactR = true;
    public int Steps;
    private readonly float[] _lastH = new float[2];
    public string HeightInfo => $"fotH L={_lastH[0]:F2} R={_lastH[1]:F2}";
    public string DebugInfo => $"ik w={Weight:F2} offL={_offL:F2} offR={_offR:F2} pelvis={_pelvisOff:F2} steps={Steps} footIdx={_footL}";
    private const float MaxOffset = 0.5f;
    private const float AnkleHeight = 0.09f;

    public override void _ProcessModificationWithDelta(double delta)
    {
        var skel = GetSkeleton();
        if (skel == null || Body == null) return;
        if (_footL < 0)
        {
            _footL = skel.FindBone("foot_l"); _footR = skel.FindBone("foot_r");
            _calfL = skel.FindBone("calf_l"); _calfR = skel.FindBone("calf_r");
            _pelvis = skel.FindBone("pelvis");
            _pelvisParent = _pelvis >= 0 ? skel.GetBoneParent(_pelvis) : -1;
            if (_footL < 0 || _footR < 0 || _pelvis < 0) { _footL = -2; return; }
        }
        if (_footL < 0) return;

        float dt = (float)delta;
        var xf = skel.GlobalTransform;
        var fL = xf * skel.GetBoneGlobalPose(_footL).Origin;
        var fR = xf * skel.GetBoneGlobalPose(_footR).Origin;
        float rootY = Body.GlobalPosition.Y;

        // Fotsteg: animert fothøyde over rota
        DetectStep(0, fL, rootY, ref _contactL);
        DetectStep(1, fR, rootY, ref _contactR);

        float gL = GroundY(fL, rootY), gR = GroundY(fR, rootY);
        float k = 1f - Mathf.Exp(-14f * dt);
        _offL = Mathf.Lerp(_offL, Mathf.Clamp(gL - rootY, -MaxOffset, MaxOffset), k);
        _offR = Mathf.Lerp(_offR, Mathf.Clamp(gR - rootY, -MaxOffset, MaxOffset), k);
        _pelvisOff = Mathf.Lerp(_pelvisOff, Mathf.Min(Mathf.Min(_offL, _offR), 0f) * Weight, k);

        // Senk hoftene (i foreldrebeinets rom)
        if (Mathf.Abs(_pelvisOff) > 0.0005f && _pelvisParent >= 0)
        {
            var parentGlobal = xf * skel.GetBoneGlobalPose(_pelvisParent);
            var local = parentGlobal.Basis.Inverse() * new Vector3(0, _pelvisOff, 0);
            skel.SetBonePosePosition(_pelvis, skel.GetBonePosePosition(_pelvis) + local);
        }

        // IK-mål = animert fot flyttet til bakken under den
        TargetL.GlobalPosition = fL + new Vector3(0, _offL * Weight, 0);
        TargetR.GlobalPosition = fR + new Vector3(0, _offR * Weight, 0);
        TargetL.GlobalBasis = (xf * skel.GetBoneGlobalPose(_footL)).Basis;
        TargetR.GlobalBasis = (xf * skel.GetBoneGlobalPose(_footR)).Basis;

        // Knærne peker fremover
        var fwd = -Body.GlobalBasis.Z;
        PoleL.GlobalPosition = xf * skel.GetBoneGlobalPose(_calfL).Origin + fwd * 0.8f;
        PoleR.GlobalPosition = xf * skel.GetBoneGlobalPose(_calfR).Origin + fwd * 0.8f;
    }

    private void DetectStep(int side, Vector3 foot, float rootY, ref bool contact)
    {
        float h = foot.Y - rootY;
        _lastH[side] = h;
        if (contact && h > AnkleHeight + 0.09f) contact = false;
        else if (!contact && h < AnkleHeight + 0.04f)
        {
            contact = true;
            Steps++;
            var fwd = -Body.GlobalBasis.Z;
            Footstep?.Invoke(side, new Vector3(foot.X, rootY, foot.Z), Mathf.Atan2(-fwd.X, -fwd.Z));
        }
    }

    private float GroundY(Vector3 foot, float rootY)
    {
        var space = Body.GetWorld3D()?.DirectSpaceState;
        if (space == null) return rootY;
        var from = new Vector3(foot.X, rootY + 0.6f, foot.Z);
        var to = new Vector3(foot.X, rootY - 0.7f, foot.Z);
        var q = PhysicsRayQueryParameters3D.Create(from, to, Game.LayerWorld);
        if (ExcludeRid.IsValid) q.Exclude = new Godot.Collections.Array<Rid> { ExcludeRid };
        var hit = space.IntersectRay(q);
        return hit.Count > 0 ? hit["position"].AsVector3().Y : rootY;
    }
}
