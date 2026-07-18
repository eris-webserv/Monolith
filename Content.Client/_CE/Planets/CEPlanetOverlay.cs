/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Content.Client.Parallax;
using Content.Client.Viewport;
using Content.Shared._CE.Planets;
using Content.Shared._CE.Planets.Descent;
using Content.Shared._CE.Planets.Shields;
using Content.Shared._CE.ZLevels.Core.Components;
using Content.Shared._CE.ZLevels.Core.EntitySystems;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._CE.Planets;

/// <summary>
/// Draws <see cref="CEPlanetComponent"/> bodies as large, slowly spinning sprites in the parallax
/// background: same below-world space as the star skybox but a hair in front of it, so planets sit
/// behind every grid and entity yet over the stars. Rendered in the same contexts as the parallax
/// (bottom z-level only, never on transit maps) so a planet reads as part of the sky.
/// </summary>
public sealed partial class CEPlanetOverlay : Overlay
{
    [Dependency] private IEntityManager _entManager = null!;
    [Dependency] private IEyeManager _eyeManager = null!;
    [Dependency] private IGameTiming _timing = null!;
    [Dependency] private IOverlayManager _overlayMan = null!;

    private readonly SpriteSystem _sprite;
    private readonly SharedTransformSystem _transform;
    private readonly CESharedZLevelsSystem _zLevel;
    private readonly CESharedDescentSystem _descent;

    // Planets are distant sky bodies, not lit surfaces — draw them fullbright so the world's
    // lighting/darkness never dims them (same reason the parallax skybox is unshaded).
    private readonly ShaderInstance _unshaded;

    // Planetary shield skin: a procedural hex dome (see shield_skin.swsl) drawn over the
    // disc while the planet's shield is up. InstanceUnique because the formation progress
    // is a per-planet, per-frame parameter.
    private readonly ShaderInstance _shieldSkin;

    /// <summary>How long the field visually crawls across the disc after activation
    /// (and, played in reverse, dissolves off it after deactivation).</summary>
    private static readonly TimeSpan ShieldFormationTime = TimeSpan.FromSeconds(2.5);

    /// <summary>Field colour; alpha scales the whole effect. Kept deep and dim — the
    /// field should read as a dark energy skin over the art, not a bright hologram.</summary>
    private static readonly Color ShieldColor = Color.FromHex("#2e8fb8").WithAlpha(0.7f);

    /// <summary>
    /// Per-planet local animation state for the shield skin: formation progress in
    /// [0, 1] plus the local realtime it was last advanced. Entirely clientside — the
    /// clock starts when *this client* observes <see cref="CEPlanetShieldComponent.Active"/>
    /// flip, not when the server stamped it, so the crawl always plays out in full and
    /// at the right speed regardless of ping. Planets first seen with the field already
    /// up snap straight to formed (no replay every time one enters PVS).
    /// </summary>
    private readonly Dictionary<EntityUid, (float Progress, TimeSpan Last)> _shieldAnim = new();

    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowWorld;

    public CEPlanetOverlay()
    {
        // Just above the parallax skybox, still below the world pass (grids/entities).
        ZIndex = ParallaxSystem.ParallaxZIndex + 1;
        IoCManager.InjectDependencies(this);
        _sprite = _entManager.System<SpriteSystem>();
        _transform = _entManager.System<SharedTransformSystem>();
        _zLevel = _entManager.System<CESharedZLevelsSystem>();
        _descent = _entManager.System<CESharedDescentSystem>();
        var protoMan = IoCManager.Resolve<IPrototypeManager>();
        _unshaded = protoMan.Index<ShaderPrototype>("unshaded").Instance();
        _shieldSkin = protoMan.Index<ShaderPrototype>("CEPlanetShieldSkin").InstanceUnique();
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        if (args.MapId == MapId.Nullspace)
            return false;

        // Mirror the parallax's z-level gating: only the lowest level shows the sky, and transit
        // maps are mostly-empty carriers we must never paint over.
        if (args.Viewport.Eye is ScalingViewport.ZEye zEye)
            return zEye.DrawParallax;

        if (_entManager.HasComponent<CEZTransitMapComponent>(args.MapUid))
            return false;

        return !_zLevel.TryMapDown(args.MapUid, out _);
    }

    /// <summary>Keeps the radius-edge planet a hair inside the screen edge rather than clipped off it.</summary>
    private const float EdgeMargin = 0.9f;

    protected override void Draw(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;
        var time = (float) _timing.RealTime.TotalSeconds;

        // Fullbright: unaffected by world lighting.
        handle.UseShader(_unshaded);

        // Work in the viewport's LOCAL (render-target pixel) space — WorldToLocal/LocalToWorld carry
        // the whole transform (eye rotation, zoom, z-level scaling). But [0, Size] is NOT the visible
        // area: with vertical-fit widescreen the ScalingViewport's draw box deliberately overflows the
        // monitor horizontally and the blit crops it to the control. Pull the control's on-screen
        // pixel rect back through its (public) local→screen matrix to get the sub-rect of local space
        // that actually survives the blit; plain viewports fall back to [0, Size].
        var vp = args.Viewport;
        var visRect = new UIBox2(Vector2.Zero, vp.Size);
        if (_eyeManager.MainViewport is ScalingViewport svp &&
            svp.ViewportSize * svp.CurrentRenderScale == vp.Size && // it's this control's viewport
            Matrix3x2.Invert(svp.GetLocalToScreenMatrix(), out var screenToLocal))
        {
            var global = (Vector2) svp.GlobalPixelPosition;
            var tl = Vector2.Transform(global, screenToLocal);
            var br = Vector2.Transform(global + svp.PixelSize, screenToLocal);
            visRect = new UIBox2(Vector2.Max(tl, Vector2.Zero), Vector2.Min(br, vp.Size));
        }

        // The view CENTRE in local space is wherever the eye centre projects — NOT viewSize/2, once
        // the eye has an offset/rotation. Take it from the transform directly so the clamp box is
        // centred correctly (assuming otherwise let the planet slip past a straight edge).
        var worldCentre = args.WorldAABB.Center;
        var centreLocal = vp.WorldToLocal(worldCentre);

        // Local pixels per world metre (for insetting by the sprite's size in the correct units).
        var pxPerWorld = (vp.WorldToLocal(worldCentre + Vector2.UnitX) - centreLocal).Length();

        var query = _entManager.EntityQueryEnumerator<CEPlanetComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var planet, out var xform))
        {
            if (xform.MapID != args.MapId)
                continue;

            var worldPos = _transform.GetWorldPosition(xform);
            var dist = (worldPos - worldCentre).Length();

            // A planet on this map is ALWAYS drawn. Inside the YML-defined zone (ZoneRadius) the
            // planet is AT you: pinned screen-centred at MaxScale, t = 0 across the whole zone.
            // Past the zone edge, t eases 0 → 1 over the remaining band out to ApproachRadius and
            // clamps there beyond — so it gradually moves toward the screen edge/corner as you
            // pull away, then holds at the edge, MinScale, tracking bearing like a distant body.
            //
            // The band mapping is smoothstep (ease-in-out, zero slope at both ends): the planet
            // eases GRADUALLY away from centre as you leave the zone instead of whipping out, and
            // also decelerates onto the edge rather than slamming into the clamp.
            var zone = Math.Clamp(planet.ZoneRadius, 0f, planet.ApproachRadius - 1e-3f);
            var band = planet.ApproachRadius - zone;
            var lin = dist <= zone ? 0f : MathF.Min((dist - zone) / band, 1f);
            var t = lin * lin * (3f - 2f * lin);

            // Boundary visuals (debug only — gated on the planet debug overlay being active): the
            // inner ring is the full-size zone (you're "at" the planet inside it), the outer, dimmer
            // ring is where the sprite has shrunk to MinScale. Drawn here rather than in the debug
            // overlay because they're world-space circles and that overlay is screen-space.
            if (_overlayMan.HasOverlay<CEPlanetDebugOverlay>())
            {
                if (zone > 0f)
                    handle.DrawCircle(worldPos, zone, planet.ZoneColor, false);
                var minScaleRing = MathF.Max(planet.MinScaleRadius, zone + 1e-3f);
                handle.DrawCircle(worldPos, minScaleRing, planet.ZoneColor.WithAlpha(planet.ZoneColor.A * 0.5f), false);
            }

            // Size runs on its OWN distance mapping, independent of the position compression above:
            // MaxScale inside the zone, easing (smoothstep, flat slope both ends) down to MinScale
            // out at MinScaleRadius, held at min beyond. MinScaleRadius is typically far larger than
            // ApproachRadius, so the shrink keeps going gradually long after the planet has slid to
            // the screen edge.
            var tex = _sprite.Frame0(planet.Sprite);
            var minScaleR = MathF.Max(planet.MinScaleRadius, zone + 1e-3f);
            var scaleLin = dist <= zone ? 0f : MathF.Min((dist - zone) / (minScaleR - zone), 1f);
            var scaleEased = scaleLin * scaleLin * (3f - 2f * scaleLin);   // smoothstep
            var scale = planet.MaxScale + (planet.MinScale - planet.MaxScale) * scaleEased;
            var size = tex.Size / (float) EyeManager.PixelsPerMeter * scale;

            // Screen-compressed position, in local pixels: slide from the view centre (t=0) toward
            // the edge (t=1) along the planet's on-screen bearing. The target edge is the true
            // intersection of that ray with the VISIBLE rect (inset by the sprite's half-diagonal
            // and the margin) cast FROM the actual centre — so it reaches the correct edges and
            // corners regardless of aspect, blit cropping, or an off-centre eye.
            //
            // The inset keeps the whole sprite on-screen when it's pinned at the edge, so it tracks
            // the sprite's CURRENT half-diagonal. The 40% cap is the safety net that stops the old
            // snap bug: a huge near-zone sprite could otherwise make the inset outgrow the
            // half-viewport, invert the inset rect, and collapse the edge-ray to zero (teleporting
            // the planet to dead centre). Capped, the ray always has room; and near the zone the
            // planet's t is ~0 so it sits near centre regardless, where the inset barely matters.
            var spriteHalfLocal = size.Length() * 0.5f * pxPerWorld;   // half-diagonal, local px
            var margin = visRect.Size * 0.5f * (1f - EdgeMargin);      // extra gap from screen edge
            var inset = spriteHalfLocal + MathF.Max(margin.X, margin.Y);
            inset = MathF.Min(inset, MathF.Min(visRect.Size.X, visRect.Size.Y) * 0.5f * 0.4f);

            var dirLocal = vp.WorldToLocal(worldPos) - centreLocal;
            var distLocal = dirLocal.Length();

            Vector2 targetLocal;
            if (distLocal > 1e-3f)
            {
                var nLocal = dirLocal / distLocal;

                // Ray/box distances to each bounding plane of the visible rect.
                var edgeX = nLocal.X > 0f ? (visRect.Right - inset - centreLocal.X) / nLocal.X
                          : nLocal.X < 0f ? (visRect.Left + inset - centreLocal.X) / nLocal.X
                          : float.MaxValue;
                var edgeY = nLocal.Y > 0f ? (visRect.Bottom - inset - centreLocal.Y) / nLocal.Y
                          : nLocal.Y < 0f ? (visRect.Top + inset - centreLocal.Y) / nLocal.Y
                          : float.MaxValue;

                // Smooth-min instead of hard min: a hard min clamps to a sharp-cornered rectangle,
                // and the radial distance to a rectangle kinks at the diagonals — sweeping the
                // bearing through 45° whips the target around the corner and the planet lurches.
                // The p-norm blend rounds the corners (still reaching most of the way into them)
                // so the target glides smoothly from edge to edge. Higher k = squarer.
                const float k = 6f;
                // A centre outside the inset rect (extreme zoom/tiny window) makes a plane distance
                // negative, which the p-norm can't take — floor at ~0 so it degrades to "pinned at
                // the centre" instead of NaN.
                edgeX = MathF.Max(edgeX, 1e-3f);
                edgeY = MathF.Max(edgeY, 1e-3f);
                float edgeDist;
                if (edgeX >= float.MaxValue)
                    edgeDist = edgeY;
                else if (edgeY >= float.MaxValue)
                    edgeDist = edgeX;
                else
                    edgeDist = MathF.Pow(MathF.Pow(edgeX, -k) + MathF.Pow(edgeY, -k), -1f / k);

                targetLocal = centreLocal + nLocal * (edgeDist * t);
            }
            else
            {
                targetLocal = centreLocal;
            }

            var drawPos = vp.LocalToWorld(targetLocal).Position;

            // Spin: rotating the quad rotates the mapped texture with it.
            var angle = new Angle(time * planet.SpinRate);
            var box = Box2.CenteredAround(drawPos, size);
            handle.DrawTextureRect(tex, new Box2Rotated(box, angle, drawPos));

            // Shield skin: procedural hex dome (shader-only — see shield_skin.swsl) hugging
            // the disc while the field is up. Drawn unrotated: it's a field, not terrain, so
            // it must not spin with the surface. The quad matches the sprite exactly so the
            // field sits at the planet's radius, and the sprite's texel width is fed to the
            // shader so the effect renders on the same pixel grid as the art beneath it.
            // Formation/collapse progress is a purely local clock advanced toward the
            // networked Active flag each frame: crawl in when it flips on, the exact same
            // crawl in reverse when it flips off. A flip mid-animation just reverses from
            // wherever the front currently is. First observation always seeds at 0 so a
            // freshly added (= freshly activated) shield plays its formation crawl.
            if (_entManager.TryGetComponent(uid, out CEPlanetShieldComponent? shield))
            {
                var now = _timing.RealTime;
                if (!_shieldAnim.TryGetValue(uid, out var anim))
                    anim = (0f, now);

                var step = (float) ((now - anim.Last).TotalSeconds / ShieldFormationTime.TotalSeconds);
                var formed = Math.Clamp(anim.Progress + (shield.Active ? step : -step), 0f, 1f);
                _shieldAnim[uid] = (formed, now);

                if (formed > 0f)
                {
                    _shieldSkin.SetParameter("progress", formed);
                    _shieldSkin.SetParameter("skin_color", ShieldColor);
                    _shieldSkin.SetParameter("pixel_grid", (float) tex.Width);
                    handle.UseShader(_shieldSkin);
                    handle.DrawTextureRect(Texture.White, Box2.CenteredAround(drawPos, size));
                    handle.UseShader(_unshaded);
                }
            }
        }

        // Descent pseudo-map: the ship is falling toward a planet that lives on ANOTHER
        // map, so the same-map query above can't see it. The pseudo-map carries a
        // snapshot of the planet's render fields instead — drawn screen-centred, the
        // same way a t = 0 zone planet is. Descent can only begin inside the zone,
        // where the planet was already centred at MaxScale, so the map swap is
        // seamless. It then swells slightly as the ship sinks, selling the fall until
        // the whiteout takes over.
        if (_entManager.TryGetComponent(args.MapUid, out CEDescentMapComponent? descent) &&
            descent.PlanetSprite is { } snapshot)
        {
            var tex = _sprite.Frame0(snapshot);
            // Descent: swells as the ship sinks. Ascent runs it backwards — swollen at
            // the breach, relaxing to the parked in-zone scale by the warp, so the
            // Arriving fade-in over the real planet matches seamlessly.
            var depth = _descent.GetDescentDepth(descent);
            var swell = descent.Ascent ? 1f + 0.15f * (1f - depth) : 1f + 0.15f * depth;
            var size = tex.Size / (float) EyeManager.PixelsPerMeter * descent.PlanetScale * swell;

            var angle = new Angle(time * descent.PlanetSpinRate);
            var box = Box2.CenteredAround(worldCentre, size);
            handle.DrawTextureRect(tex, new Box2Rotated(box, angle, worldCentre));
        }

        handle.UseShader(null);
    }
}
