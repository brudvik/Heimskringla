using System;
using Godot;
using Heimskringla.Core;

namespace Heimskringla.UI;

/// <summary>World map generated from the coarse map. Click to teleport.</summary>
public partial class MapView : Control
{
    public Texture2D Texture;
    public Func<Vector3> PlayerPosition;
    public Func<float> PlayerYaw;
    public Action<float, float> OnTeleport;
    public int Seed;

    private Rect2 MapRect
    {
        get
        {
            float s = Mathf.Min(Size.X, Size.Y) * 0.9f;
            return new Rect2((Size - new Vector2(s, s)) * 0.5f, new Vector2(s, s));
        }
    }

    public override void _Process(double delta)
    {
        if (Visible) QueueRedraw();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.55f));
        if (Texture == null) return;
        var r = MapRect;
        DrawTextureRect(Texture, r, false);
        DrawRect(r, new Color(0.1f, 0.08f, 0.05f), false, 3f);
        var font = ThemeDB.FallbackFont;
        DrawString(font, r.Position + new Vector2(0, -10), Loc.F("MAP_TITLE", Seed),
            HorizontalAlignment.Left, -1, 18, new Color(1, 0.97f, 0.9f));
        DrawString(font, r.Position + new Vector2(r.Size.X * 0.5f - 6, 22), Loc.T("MAP_NORTH"), HorizontalAlignment.Left, -1, 22, Colors.White);

        if (PlayerPosition == null) return;
        var p = PlayerPosition();
        var mp = WorldToMap(p.X, p.Z, r);
        float yaw = PlayerYaw?.Invoke() ?? 0f;
        var fwd = new Vector2(-Mathf.Sin(yaw), -Mathf.Cos(yaw));
        var side = new Vector2(-fwd.Y, fwd.X);
        var pts = new[] { mp + fwd * 12f, mp - fwd * 7f + side * 7f, mp - fwd * 7f - side * 7f };
        DrawColoredPolygon(pts, new Color(0.9f, 0.1f, 0.1f));
        DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[0] }, Colors.White, 1.5f);
    }

    private static Vector2 WorldToMap(float x, float z, Rect2 r)
    {
        float u = (x + World.WorldSettings.HalfSize) / World.WorldSettings.WorldSize;
        float v = (z + World.WorldSettings.HalfSize) / World.WorldSettings.WorldSize;
        return r.Position + new Vector2(u, v) * r.Size;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
        {
            var r = MapRect;
            if (!r.HasPoint(mb.Position)) return;
            var uv = (mb.Position - r.Position) / r.Size;
            float x = -World.WorldSettings.HalfSize + uv.X * World.WorldSettings.WorldSize;
            float z = -World.WorldSettings.HalfSize + uv.Y * World.WorldSettings.WorldSize;
            OnTeleport?.Invoke(x, z);
            Hud.Instance?.ToggleMap();
            AcceptEvent();
        }
    }
}
