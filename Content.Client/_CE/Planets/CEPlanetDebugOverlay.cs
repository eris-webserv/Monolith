/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Content.Client.Viewport;
using Content.Shared._CE.Planets;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Shared.Console;
using Robust.Shared.Enums;

namespace Content.Client._CE.Planets;

/// <summary>
/// Debug overlay for the planet renderer: draws a line from the screen centre to each planet, and
/// prints the numbers the real overlay works from — world distance, projected screen position, and
/// the viewport-local coords vs the viewport size — so the edge-clamping can be diagnosed live.
/// Toggle with <c>ceplanetdebug</c>.
/// </summary>
public sealed partial class CEPlanetDebugOverlay : Overlay
{
    [Dependency] private IEntityManager _ent = null!;
    [Dependency] private IResourceCache _cache = null!;
    [Dependency] private IPlayerManager _player = null!;

    private readonly SharedTransformSystem _transform;
    private readonly Font _font;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public CEPlanetDebugOverlay()
    {
        IoCManager.InjectDependencies(this);
        _transform = _ent.System<SharedTransformSystem>();
        _font = new VectorFont(_cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (args.ViewportControl is not { } vpc)
            return;

        var handle = args.ScreenHandle;
        var vp = args.Viewport;

        // Mark the four corners of ViewportBounds with big filled squares — if these don't sit at
        // the monitor corners, the "viewport bounds" aren't the visible area (the ScalingViewport
        // renders a bigger buffer than it shows), which is the whole edge-clamp problem.
        var b = args.ViewportBounds;
        void Corner(float x, float y) => handle.DrawRect(new UIBox2(x - 10, y - 10, x + 10, y + 10), Color.Yellow);
        Corner(b.Left, b.Top);
        Corner(b.Right, b.Top);
        Corner(b.Left, b.Bottom);
        Corner(b.Right, b.Bottom);

        var centre = new Vector2((b.Left + b.Right) * 0.5f, (b.Top + b.Bottom) * 0.5f);
        var viewSize = (Vector2) vp.Size;
        var worldCentre = args.WorldAABB.Center;

        // The visible sub-rect of local space, exactly as the real overlay derives it: the draw box
        // may overflow the monitor (vertical-fit crops the blit), so pull the control's on-screen
        // rect back through its local→screen matrix. MAGENTA outline = the clamp box, on screen.
        var visRect = new UIBox2(Vector2.Zero, viewSize);
        if (vpc is ScalingViewport svp && Matrix3x2.Invert(svp.GetLocalToScreenMatrix(), out var screenToLocal))
        {
            var global = (Vector2) svp.GlobalPixelPosition;
            var tl = Vector2.Transform(global, screenToLocal);
            var br = Vector2.Transform(global + svp.PixelSize, screenToLocal);
            visRect = new UIBox2(Vector2.Max(tl, Vector2.Zero), Vector2.Min(br, viewSize));

            var toScreen = svp.GetLocalToScreenMatrix();
            // Inset the drawn outline by half a pixel: a 1px line at exactly x=0/y=0 covers
            // [-0.5, 0.5) and misses the first pixel-centre row/column entirely (GL half-open
            // coverage), so the top/left edges vanish while bottom/right survive. The clamp
            // itself is unaffected — this is purely so all four edges are visible.
            var sTl = Vector2.Transform(visRect.TopLeft, toScreen) + new Vector2(0.5f, 0.5f);
            var sBr = Vector2.Transform(visRect.BottomRight, toScreen) - new Vector2(0.5f, 0.5f);
            handle.DrawLine(sTl, new Vector2(sBr.X, sTl.Y), Color.Magenta);
            handle.DrawLine(new Vector2(sBr.X, sTl.Y), sBr, Color.Magenta);
            handle.DrawLine(sBr, new Vector2(sTl.X, sBr.Y), Color.Magenta);
            handle.DrawLine(new Vector2(sTl.X, sBr.Y), sTl, Color.Magenta);
        }

        Vector2? playerWorld = null;
        if (_player.LocalEntity is { } p && _ent.TryGetComponent<TransformComponent>(p, out var px))
            playerWorld = _transform.GetWorldPosition(px);

        var query = _ent.EntityQueryEnumerator<CEPlanetComponent, TransformComponent>();
        while (query.MoveNext(out _, out var planet, out var xform))
        {
            if (xform.MapID != args.MapId)
                continue;

            var worldPos = _transform.GetWorldPosition(xform);
            var dist = playerWorld is { } pw ? (worldPos - pw).Length() : -1f;
            // Mirror the real overlay's zone-then-band eased mapping (t=0 inside ZoneRadius,
            // smoothstep across the band out to ApproachRadius) so the green marker matches.
            var zone = Math.Clamp(planet.ZoneRadius, 0f, planet.ApproachRadius - 1e-3f);
            var band = MathF.Max(planet.ApproachRadius - zone, 1e-4f);
            var lin = dist <= zone ? 0f : MathF.Min((dist - zone) / band, 1f);
            var t = lin * lin * (3f - 2f * lin);

            var screenPos = vpc.WorldToScreen(worldPos);   // what ViewportControl projects
            var localPos = vp.WorldToLocal(worldPos);      // what the overlay clamps against

            // Reproduce the real overlay's clamp (ray/box from the true local centre, inset margin)
            // and project it back to screen — GREEN marker = where the planet actually draws.
            var centreLocal = vp.WorldToLocal(worldCentre);
            var inset = visRect.Width * 0.5f * 0.1f; // rough margin, debug only
            var dirLocal = localPos - centreLocal;
            var dl = dirLocal.Length();
            var clampWorld = worldPos;
            if (dl > 1e-3f)
            {
                var nL = dirLocal / dl;
                var edgeX = nL.X > 0f ? (visRect.Right - inset - centreLocal.X) / nL.X
                          : nL.X < 0f ? (visRect.Left + inset - centreLocal.X) / nL.X : float.MaxValue;
                var edgeY = nL.Y > 0f ? (visRect.Bottom - inset - centreLocal.Y) / nL.Y
                          : nL.Y < 0f ? (visRect.Top + inset - centreLocal.Y) / nL.Y : float.MaxValue;
                var edge = MathF.Max(MathF.Min(edgeX, edgeY), 0f);
                clampWorld = vp.LocalToWorld(centreLocal + nL * (edge * t)).Position;
            }
            var clampScreen = vpc.WorldToScreen(clampWorld);

            // Line + markers: cyan line to true position (red), green = actual clamped draw position.
            handle.DrawLine(centre, screenPos, Color.Cyan);
            handle.DrawRect(new UIBox2(screenPos.X - 3, screenPos.Y - 3, screenPos.X + 3, screenPos.Y + 3), Color.Red);
            handle.DrawRect(new UIBox2(clampScreen.X - 5, clampScreen.Y - 5, clampScreen.X + 5, clampScreen.Y + 5), Color.Lime);

            // Text anchored at the (confirmed-visible) marker, in two colours so it's readable on
            // any background.
            var tp = screenPos + new Vector2(6, -20);
            var line1 = $"dist={dist:0.0} t={t:0.00} zone={zone:0} R={planet.ApproachRadius:0}";
            var line2 = $"screen=({screenPos.X:0},{screenPos.Y:0}) local=({localPos.X:0},{localPos.Y:0}) vp={viewSize.X:0}x{viewSize.Y:0}";
            handle.DrawString(_font, tp + new Vector2(1, 1), line1, Color.Black);
            handle.DrawString(_font, tp, line1, Color.White);
            handle.DrawString(_font, tp + new Vector2(1, 15), line2, Color.Black);
            handle.DrawString(_font, tp + new Vector2(0, 14), line2, Color.White);
        }
    }
}

public sealed partial class CEShowPlanetDebugCommand : LocalizedCommands
{
    [Dependency] private IOverlayManager _overlayManager = null!;

    public override string Command => "ceplanetdebug";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (_overlayManager.HasOverlay<CEPlanetDebugOverlay>())
        {
            _overlayManager.RemoveOverlay<CEPlanetDebugOverlay>();
            return;
        }

        _overlayManager.AddOverlay(new CEPlanetDebugOverlay());
    }
}
