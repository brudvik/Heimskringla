using System.Collections.Generic;
using Godot;
using Heimskringla.Core;
using Heimskringla.Gameplay;

namespace Heimskringla.Vegetation;

/// <summary>
/// Strømmer vegetasjon i 128 m-celler rundt spilleren. Alt tegnes med MultiMesh (billig).
/// Objekter nær spilleren "forfremmes" til ekte noder med kollisjon og høsting,
/// og skjules i MultiMeshen så lenge de er forfremmet.
/// </summary>
public partial class VegetationManager : Node3D
{
    public const float CellSize = 128f;
    private const float LoadRadius = 950f;
    private const float UnloadRadius = 1050f;
    private const float NearBand = 180f;
    private const float MidBand = 550f;
    private const float PromoteRadius = 20f;
    private const float DemoteRadius = 26f;

    private static readonly Transform3D Hidden = new(new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero), new Vector3(0, -10000, 0));

    private sealed class VegCell
    {
        public int X, Z;
        public List<PropInstance>[] ByKind;
        public MultiMeshInstance3D[][] Mmi; // [type][variant]
        public int Band = -1;
        public int LastDistStep = -1;
        public readonly List<Node3D> Stumps = new();
        public bool Ready;
        public JobQueue.Job Job;
    }

    private readonly Dictionary<(int, int), VegCell> _cells = new();
    private readonly HashSet<PropInstance> _promoted = new();
    private float _cellTimer, _promoteTimer;

    public int CellCount => _cells.Count;
    public int PromotedCount => _promoted.Count;
    public int InstanceCount { get; private set; }

    public override void _Process(double delta)
    {
        _cellTimer += (float)delta;
        _promoteTimer += (float)delta;
        if (_cellTimer > 0.25f) { _cellTimer = 0; UpdateCells(); }
        if (_promoteTimer > 0.1f) { _promoteTimer = 0; UpdatePromotion(); }
    }

    private static float CellDistance(int cx, int cz, Vector3 p)
    {
        float x0 = cx * CellSize, z0 = cz * CellSize;
        float dx = Mathf.Max(Mathf.Max(x0 - p.X, 0f), p.X - (x0 + CellSize));
        float dz = Mathf.Max(Mathf.Max(z0 - p.Z, 0f), p.Z - (z0 + CellSize));
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private void UpdateCells()
    {
        var p = Game.Focus;
        int pcx = Mathf.FloorToInt(p.X / CellSize), pcz = Mathf.FloorToInt(p.Z / CellSize);
        int r = Mathf.CeilToInt(LoadRadius / CellSize) + 1;
        var w = Game.World;

        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            int cx = pcx + dx, cz = pcz + dz;
            float dist = CellDistance(cx, cz, p);
            if (dist > LoadRadius) continue;
            if (_cells.TryGetValue((cx, cz), out var cell))
            {
                if (cell.Job != null) cell.Job.Priority = dist + 40f;
                continue;
            }
            cell = new VegCell { X = cx, Z = cz };
            _cells[(cx, cz)] = cell;
            int fx = cx, fz = cz;
            var c = cell;
            cell.Job = Game.Jobs.Enqueue(dist + 40f,
                () => VegetationGenerator.Generate(w, fx, fz, CellSize),
                result => OnGenerated(c, (List<PropInstance>[])result));
        }

        var remove = new List<VegCell>();
        int instances = 0;
        foreach (var cell in _cells.Values)
        {
            float dist = CellDistance(cell.X, cell.Z, p);
            if (dist > UnloadRadius) { remove.Add(cell); continue; }
            if (!cell.Ready) continue;
            int band = dist < NearBand ? 0 : dist < MidBand ? 1 : 2;
            if (band != cell.Band || (cell.Band > 0 && (int)(dist / 50f) != cell.LastDistStep)) { cell.LastDistStep = (int)(dist / 50f); ApplyBand(cell, band, dist); }
            if (cell.ByKind != null) foreach (var l in cell.ByKind) instances += l.Count;
        }
        InstanceCount = instances;
        foreach (var cell in remove) RemoveCell(cell);
    }

    private void OnGenerated(VegCell cell, List<PropInstance>[] lists)
    {
        if (!_cells.TryGetValue((cell.X, cell.Z), out var cur) || cur != cell) return;
        cell.Job = null;
        cell.ByKind = lists;
        cell.Mmi = new MultiMeshInstance3D[lists.Length][];
        for (int k = 0; k < lists.Length; k++)
        {
            var list = lists[k];
            if (list.Count == 0) continue;
            var def = PropLibrary.Defs[k];
            int nv = def.Variants.Length;
            cell.Mmi[k] = new MultiMeshInstance3D[nv];
            var groups = new List<PropInstance>[nv];
            for (int v = 0; v < nv; v++) groups[v] = new List<PropInstance>();
            foreach (var inst in list)
            {
                inst.Cell = cell;
                if (WorldState.Removed.Contains(inst.Id)) inst.Removed = true;
                var g = groups[Mathf.Clamp(inst.Variant, 0, nv - 1)];
                inst.MmIndex = g.Count;
                g.Add(inst);
            }
            for (int v = 0; v < nv; v++)
            {
                var g = groups[v];
                if (g.Count == 0) continue;
                var buffer = new float[g.Count * 12];
                for (int i = 0; i < g.Count; i++)
                    WriteTransform(buffer, i * 12, g[i].Removed ? Hidden : g[i].Transform);
                var mm = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    Mesh = def.Variants[v].Mesh,
                };
                mm.InstanceCount = g.Count;
                mm.Buffer = buffer;
                var mmi = new MultiMeshInstance3D { Multimesh = mm, Visible = false };
                AddChild(mmi);
                cell.Mmi[k][v] = mmi;
            }
        }
        // Stubber etter trær som er felt tidligere (fra lagring)
        foreach (var list in lists)
            foreach (var inst in list)
                if (inst.Removed && PropLibrary.Defs[(int)inst.Kind].Role == PropRole.Tree)
                    AddStump(cell, inst);

        cell.Ready = true;
        cell.Band = -1;
        float dist = CellDistance(cell.X, cell.Z, Game.Focus);
        ApplyBand(cell, dist < NearBand ? 0 : dist < MidBand ? 1 : 2, dist);
    }

    private static MultiMeshInstance3D SlotFor(VegCell cell, PropInstance inst)
    {
        var arr = cell.Mmi?[(int)inst.Kind];
        return arr == null ? null : arr[Mathf.Clamp(inst.Variant, 0, arr.Length - 1)];
    }

    private static void WriteTransform(float[] b, int o, Transform3D t)
    {
        var bs = t.Basis;
        b[o + 0] = bs.X.X; b[o + 1] = bs.Y.X; b[o + 2] = bs.Z.X; b[o + 3] = t.Origin.X;
        b[o + 4] = bs.X.Y; b[o + 5] = bs.Y.Y; b[o + 6] = bs.Z.Y; b[o + 7] = t.Origin.Y;
        b[o + 8] = bs.X.Z; b[o + 9] = bs.Y.Z; b[o + 10] = bs.Z.Z; b[o + 11] = t.Origin.Z;
    }

    private static void ApplyBand(VegCell cell, int band, float dist)
    {
        cell.Band = band;
        for (int k = 0; k < cell.Mmi.Length; k++)
        {
            if (cell.Mmi[k] == null) continue;
            var def = PropLibrary.Defs[k];
            bool show = dist <= def.MaxDistance;
            var shadow = band == 0 && def.CastShadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
            foreach (var mmi in cell.Mmi[k])
            {
                if (mmi == null) continue;
                mmi.Visible = show;
                mmi.CastShadow = shadow;
            }
        }
    }

    private void RemoveCell(VegCell cell)
    {
        if (cell.Job != null) cell.Job.Cancelled = true;
        if (cell.ByKind != null)
            foreach (var list in cell.ByKind)
                foreach (var inst in list)
                    if (inst.Node != null)
                    {
                        inst.Node.QueueFree();
                        inst.Node = null;
                        _promoted.Remove(inst);
                    }
        foreach (var st in cell.Stumps) st.QueueFree();
        cell.Stumps.Clear();
        if (cell.Mmi != null)
            foreach (var arr in cell.Mmi)
                if (arr != null) foreach (var m in arr) m?.QueueFree();
        _cells.Remove((cell.X, cell.Z));
    }

    // ------------------------------------------------------------ Forfremming

    private void UpdatePromotion()
    {
        var p = Game.Focus;
        float pr2 = PromoteRadius * PromoteRadius, dr2 = DemoteRadius * DemoteRadius;

        var demote = new List<PropInstance>();
        foreach (var inst in _promoted)
        {
            float dx = inst.Pos.X - p.X, dz = inst.Pos.Z - p.Z;
            float extra = PropLibrary.Defs[(int)inst.Kind].Role == PropRole.Obstacle ? 30f : 0f;
            float r = DemoteRadius + extra;
            if (dx * dx + dz * dz > r * r) demote.Add(inst);
        }
        foreach (var inst in demote) Demote(inst);

        int pcx = Mathf.FloorToInt(p.X / CellSize), pcz = Mathf.FloorToInt(p.Z / CellSize);
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (!_cells.TryGetValue((pcx + dx, pcz + dz), out var cell) || !cell.Ready) continue;
            if (CellDistance(cell.X, cell.Z, p) > PromoteRadius + 30f) continue;
            for (int k = 0; k < cell.ByKind.Length; k++)
            {
                var role = PropLibrary.Defs[k].Role;
                if (role == PropRole.Decor) continue;
                float r = PromoteRadius + (role == PropRole.Obstacle ? 30f : 0f);
                foreach (var inst in cell.ByKind[k])
                {
                    if (inst.Removed || inst.Node != null) continue;
                    float ix = inst.Pos.X - p.X, iz = inst.Pos.Z - p.Z;
                    if (ix * ix + iz * iz < r * r) Promote(cell, inst);
                }
            }
        }
    }

    private void Promote(VegCell cell, PropInstance inst)
    {
        var node = Interactables.CreateForProp(PropLibrary.Get(inst.Kind), inst, this);
        AddChild(node);
        inst.Node = node;
        _promoted.Add(inst);
        SlotFor(cell, inst)?.Multimesh.SetInstanceTransform(inst.MmIndex, Hidden);
    }

    private void Demote(PropInstance inst)
    {
        _promoted.Remove(inst);
        if (inst.Node != null)
        {
            inst.Node.QueueFree();
            inst.Node = null;
        }
        if (!inst.Removed && inst.Cell is VegCell cell && cell.Mmi != null)
            SlotFor(cell, inst)?.Multimesh.SetInstanceTransform(inst.MmIndex, inst.Transform);
    }

    /// <summary>Kalles når et objekt er høstet/plukket. Noden fjerner seg selv.</summary>
    private void AddStump(VegCell cell, PropInstance inst)
    {
        var mesh = inst.Cold ? PropLibrary.StumpSnowMesh : PropLibrary.StumpMesh;
        if (mesh == null) return;
        var def = PropLibrary.Defs[(int)inst.Kind];
        float r = def.TrunkRadius * inst.Scale * 1.25f;
        var body = new StaticBody3D
        {
            Position = inst.Pos + new Vector3(0, def.Sink * inst.Scale * 0.5f, 0),
            Rotation = new Vector3(0, inst.Yaw, 0),
            CollisionLayer = Game.LayerWorld,
            CollisionMask = 0,
        };
        body.AddChild(new MeshInstance3D { Mesh = mesh, Scale = Vector3.One * (r / 0.6f), VisibilityRangeEnd = 250f });
        body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = r, Height = 0.6f * r / 0.6f }, Position = new Vector3(0, 0.3f * r / 0.6f, 0) });
        AddChild(body);
        cell.Stumps.Add(body);
    }

    public void OnHarvested(PropInstance inst)
    {
        if (PropLibrary.Defs[(int)inst.Kind].Role == PropRole.Tree && inst.Cell is VegCell vc) AddStump(vc, inst);
        inst.Removed = true;
        inst.Node = null;
        _promoted.Remove(inst);
        WorldState.Removed.Add(inst.Id);
    }
}
