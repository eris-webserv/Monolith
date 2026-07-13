/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Content.Client._CE.ZLevels.Core;
using Content.Shared._CE.Planets.Descent;
using Content.Shared._CE.ZLevels.Core.Components;
using Content.Shared._CE.ZLevels.Core.EntitySystems;
using Content.Shared.Maps;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Client.Viewport;

public sealed partial class ScalingViewport
{
    [Dependency] private IMapManager _mapManager = default!;
    [Dependency] private IEyeManager _eyeManager = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private ITileDefinitionManager _tile = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;

    private CEClientZLevelsSystem? _zLevels;
    private CESharedDescentSystem? _descent;
    private SharedMapSystem? _mapSystem;

    private EntityQuery<TransformComponent>? _xformQuery;
    private EntityQuery<MapComponent>? _mapQuery;

    private IEye? _fallbackEye;

    /// <summary>
    /// We are looking for at least one empty tile on the screen.
    /// This is used to ensure that it makes sense to draw the z-planes and that they are visible.
    /// </summary>
    public bool TryFindEmptyTiles(EntityUid mapUid)
    {
        if (_xformQuery is null || !_xformQuery.Value.TryComp(mapUid, out var xform))
            return true;

        var drawBox = GetDrawBox();
        var mapId = xform.MapID;

        var corners = new[]
        {
            _eyeManager.ScreenToMap(drawBox.BottomLeft).Position,
            _eyeManager.ScreenToMap(drawBox.BottomRight).Position,
            _eyeManager.ScreenToMap(drawBox.TopLeft).Position,
            _eyeManager.ScreenToMap(drawBox.TopRight).Position
        };

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var c in corners)
        {
            if (c.X < minX)
                minX = c.X;
            if (c.Y < minY)
                minY = c.Y;
            if (c.X > maxX)
                maxX = c.X;
            if (c.Y > maxY)
                maxY = c.Y;
        }

        var mapCoordsBottomLeft = new MapCoordinates(new Vector2(minX, minY), mapId);
        var mapCoordsTopRight = new MapCoordinates(new Vector2(maxX, maxY), mapId);

        if (_mapSystem is null || !_mapManager.TryFindGridAt(mapUid, mapCoordsBottomLeft.Position, out var gridUid, out var grid))
            return true;

        var tileBottomLeft = _mapSystem.TileIndicesFor(gridUid, grid, mapCoordsBottomLeft);
        var tileTopRight = _mapSystem.TileIndicesFor(gridUid, grid, mapCoordsTopRight);

        for (var x = tileBottomLeft.X - 1; x <= tileTopRight.X + 1; x++)
        {
            for (var y = tileBottomLeft.Y - 1; y <= tileTopRight.Y + 1; y++)
            {
                var tile = _mapSystem.GetTileRef(gridUid, grid, new Vector2i(x, y));
                var tileDef = (ContentTileDefinition)_tile[tile.Tile.TypeId];
                if (tileDef.Transparent || tile.Tile.IsEmpty)
                    return true;
            }
        }

        return false;
    }

    private readonly List<(EntityUid MapUid, float Depth, bool AllowFov, bool Transit)> _zPasses = new();

    /// <summary>
    /// Secondary viewport for grids transiting ABOVE the observer: rendering them to a
    /// transparent target lets the composite apply haze and fade to just the ship,
    /// instead of fogging the whole already-drawn scene.
    /// </summary>
    private IClydeViewport? _transitViewport;
    private ShaderInstance? _transitBlitShader;
    private ShaderInstance? _cloudShader;

    private void RenderZLevels(IRenderHandle renderHandle, IClydeViewport viewport)
    {
        if (_eye is null)
            return;

        _fallbackEye = _eye;

        // Cache frequently accessed components/systems
        _xformQuery ??= _entityManager.GetEntityQuery<TransformComponent>();
        _mapQuery ??= _entityManager.GetEntityQuery<MapComponent>();

        // Cache systems and components
        _zLevels ??= _entityManager.System<CEClientZLevelsSystem>();
        _descent ??= _entityManager.System<CESharedDescentSystem>();
        _mapSystem ??= _entityManager.System<SharedMapSystem>();

        if (_player.LocalEntity is null)
            return;

        if (!_entityManager.TryGetComponent<CEZLevelViewerComponent>(_player.LocalEntity.Value, out var zLevelViewer))
            return;

        if (!_xformQuery.Value.TryComp(_player.LocalEntity, out var playerXform))
            return;

        if (playerXform.MapUid is null)
            return;

        var playerMap = playerXform.MapUid.Value;

        _zPasses.Clear();

        // Rider on a descent pseudo-map: their own deck renders at normal scale — the
        // fall shows in the world below, not in their own sprite. The bystander half —
        // the shrinking ship seen from the origin map — is the synthetic descent pass
        // added below.
        _entityManager.TryGetComponent(playerMap, out CEDescentMapComponent? riderDescent);

        // The departed world stays around a descending rider: the pseudo-map hop is an
        // implementation detail (it only exists so bystanders get a scalable pass) and
        // must be invisible from inside. The origin map joins the pass list pinned a
        // bare epsilon below the rider's own deck — that depth is ORDERING ONLY. The
        // zoom is overridden at eye-build time: a plunge reads as the world below
        // charging UP at the rider, not the world timidly stepping one z-level back —
        // shrink is the bystanders' half, not ours.
        //
        // Same single-clock principle as the bystander composite below: EVERYTHING
        // the rider sees — zoom curve and whiteout cover alike — keys off the one
        // Descending clock and is finished by its end. The cover starts midway
        // through the ride and is FULL white before the server flips to Vanishing, so
        // the late-replicating flip (and the warp's map move behind it) changes
        // nothing on screen — no Stage 1.5 seam, by construction. The zoom spends
        // riderDescentZoomLevels over the visible ride (smoothstep — the old depth
        // curve, unchanged) plus a riderVanishZoomLevels lunge backloaded (ride^4) so
        // its violent tail lands under the near-opaque fog: it only ever reads as
        // ground-rush, never as a legible map scale.
        const float riderDescentZoomLevels = 2f;
        const float riderVanishZoomLevels = 5f;
        const float riderCoverStartRide = 0.5f;
        var riderRide = 0f;
        if (riderDescent != null)
        {
            riderRide = riderDescent.Stage switch
            {
                CEDescentStage.Descending => _descent.GetStageProgress(
                    CEDescentStage.Descending, riderDescent.StageStart),
                CEDescentStage.Vanishing or CEDescentStage.Arriving => 1f,
                _ => 0f,
            };
        }

        var riderOriginPass = false;
        if (riderDescent?.OriginMap is { } riderOrigin && !_entityManager.Deleted(riderOrigin))
        {
            _zPasses.Add((riderOrigin, -0.0005f, false, false));
            riderOriginPass = true;
        }

        // When riding a grid between two levels, the world below starts a fractional
        // depth away instead of a whole one, and slides continuously as the grid moves.
        var frac = 0f;
        var ownDepth = 0f;
        EntityUid? belowChainStart = null;
        var belowChainStartDepth = -1f;
        EntityUid? aboveMap = null;
        var aboveDepth = 1f;

        if (_entityManager.TryGetComponent(playerMap, out CEZTransitMapComponent? riderTransit))
        {
            frac = GetTransitProgress(riderTransit);
            belowChainStart = riderTransit.LowerMap;
            belowChainStartDepth = -frac;
            aboveMap = riderTransit.UpperMap;
            aboveDepth = 1f - frac;
        }
        else
        {
            // A viewer that's airborne inside their own level (jetpack, jump, falling,
            // or standing on a climbing grid) hangs a fraction above their map's ground
            // plane, so the whole depth ladder shifts down by that amount — including
            // their own map, which shrinks under them just like a transit ride's lower
            // anchor does.
            frac = _zLevels.GetLocalAltitude(_player.LocalEntity.Value);
            ownDepth = -frac;
            belowChainStartDepth = -1f - frac;
            aboveDepth = 1f - frac;

            if (_zLevels.TryMapOffset(playerMap, -1, out var mapBelow))
                belowChainStart = mapBelow.Owner;
            if (_zLevels.TryMapUp(playerMap, out var mapAbove))
                aboveMap = mapAbove.Owner;
        }

        // Depth of the nearest layer below the observer that caps the view, if any, with the
        // band under it that still renders. A cloud deck keeps a dissolve band (ships sink
        // through the tops); a ground layer is a hard floor you land ON, never below, so
        // nothing under it should ever draw. Transit ships past this are occluded and skip
        // their render pass (see the transit cull below).
        float? occludeBelowDepth = null;
        var occludeBand = 0f;

        // Standing on such a layer caps the whole view below the observer at once.
        if (_entityManager.HasComponent<CEZCloudLayerComponent>(playerMap))
        {
            occludeBelowDepth = ownDepth;
            occludeBand = CloudDissolveBand;
        }
        else if (_entityManager.HasComponent<CEZGroundLayerComponent>(playerMap))
        {
            occludeBelowDepth = ownDepth;
        }
        // Otherwise walk downward while there are empty tiles to see through. A cloud or
        // ground layer ends the walk: nothing beneath it is visible.
        else if (TryFindEmptyTiles(playerMap))
        {
            var current = belowChainStart;
            var depthCursor = belowChainStartDepth;
            for (var i = 0; i < CESharedZLevelsSystem.MaxZLevelsBelowRendering && current != null; i++)
            {
                _zPasses.Add((current.Value, depthCursor, false, false));

                if (_entityManager.HasComponent<CEZCloudLayerComponent>(current.Value))
                {
                    occludeBelowDepth = depthCursor;
                    occludeBand = CloudDissolveBand;
                    break;
                }

                if (_entityManager.HasComponent<CEZGroundLayerComponent>(current.Value))
                {
                    occludeBelowDepth = depthCursor;
                    break;
                }

                if (!TryFindEmptyTiles(current.Value))
                    break;

                current = _zLevels.TryMapOffset(current.Value, -1, out var next) ? next.Owner : null;
                depthCursor -= 1f;
            }
        }

        // The player's own map always renders. On the ground that's depth 0 with the
        // real eye; an airborne viewer sees it a fraction below, scaled to match.
        _zPasses.Add((playerMap, ownDepth, true, false));

        if (riderTransit != null)
        {
            if (aboveMap != null && aboveDepth > 0.001f && TransitFade(aboveDepth) > 0.01f)
                _zPasses.Add((aboveMap.Value, aboveDepth, false, true));
        }
        else if (zLevelViewer.LookUp && aboveMap != null)
        {
            _zPasses.Add((aboveMap.Value, aboveDepth, true, false));
        }

        // Grids in transit render between the levels they're crossing. Selection is by
        // ABSOLUTE altitude difference, so ships in any gap of the observer's network
        // render — not just gaps bordering the maps already in the pass list.
        var altitudeAnchor = riderTransit?.LowerMap ?? playerMap;
        if (_entityManager.HasComponent<CEZMapComponent>(altitudeAnchor))
        {
            var observerAltitude = _zLevels.GetAbsoluteAltitude(_player.LocalEntity.Value);
            var hasObserverNetwork = _zLevels.TryGetMapNetwork(altitudeAnchor, out var observerNetwork);

            var transitQuery = _entityManager.EntityQueryEnumerator<CEZTransitMapComponent>();
            while (transitQuery.MoveNext(out var transitUid, out var transit))
            {
                if (transitUid == playerMap || transit.LowerMap is not { } lowerMap)
                    continue;

                if (!_entityManager.TryGetComponent(lowerMap, out CEZMapComponent? lowerZ))
                    continue;

                // Other z-networks are other worlds.
                if (hasObserverNetwork &&
                    (!_zLevels.TryGetMapNetwork(lowerMap, out var transitNetwork) ||
                     transitNetwork.Owner != observerNetwork.Owner))
                {
                    continue;
                }

                // Transit grid's absolute altitude (lower-anchor depth + gap progress, folded in by
                // the API) relative to the observer's. Fall back to the gap's lower plane if the map
                // has no primary grid to read.
                var transitAltitude = transit.PrimaryGrid is { } primaryGrid
                    ? _zLevels.GetAbsoluteAltitude(primaryGrid)
                    : lowerZ.Depth;
                var transitDepth = transitAltitude - observerAltitude;

                // Hidden under a layer between it and the observer: for a cloud, ships still
                // within the dissolve band show through (as sinking ghosts) so only deeper
                // ones are culled; for a ground layer the band is zero, so anything below the
                // floor is dropped outright. This is what lets a deck cheapen the scene
                // without hiding ships still crossing it.
                if (occludeBelowDepth is { } occludeDepth && transitDepth < occludeDepth - occludeBand)
                    continue;

                // Fully dissolved into the sky: not worth a render pass.
                if (transitDepth > 0f && TransitFade(transitDepth) <= 0.01f)
                    continue;

                // No FOV on these: a ship overhead is in open sky, not behind your walls.
                _zPasses.Add((transitUid, transitDepth, false, true));
            }
        }

        // Descent pseudo-maps NEVER join the pass list: deep enough to be the bottom
        // pass, a pseudo-map would own DrawParallax — and the parallax and planet
        // overlays would then render under its SCALED eye, shrinking the whole sky
        // along with the hull. They composite over the finished scene instead (see
        // the airborne descent section after the pass loop); the observer's own pass
        // stays the deepest real pass and keeps the backdrop unscaled.

        // Painter's algorithm.
        _zPasses.Sort(static (a, b) =>
        {
            var aUp = a.Depth > 0f;
            var bUp = b.Depth > 0f;
            if (aUp != bUp)
                return aUp ? 1 : -1;
            return aUp ? b.Depth.CompareTo(a.Depth) : a.Depth.CompareTo(b.Depth);
        });

        // A rider who has broken through the tops of a cloud layer overhead hangs
        // above the deck without being ON the layer map yet: the deck must cap the
        // world below them until the handoff, or the clouds vanish under their feet
        // between the whiteout and the landing. The switch hides under the whiteout
        // at CloudFullCoverDepth, so it can't pop.
        CEZCloudLayerComponent? riderDeck = null;
        if (aboveMap != null &&
            aboveDepth <= CloudFullCoverDepth &&
            _entityManager.TryGetComponent(aboveMap.Value, out CEZCloudLayerComponent? riderDeckComp))
        {
            riderDeck = riderDeckComp;
        }

        var lowestDepth = float.MaxValue;
        var highestDepth = float.MinValue;
        foreach (var pass in _zPasses)
        {
            lowestDepth = Math.Min(lowestDepth, pass.Depth);
            highestDepth = Math.Max(highestDepth, pass.Depth);
        }

        // Builds the perspective eye for a pass at an arbitrary depth. Factored out of the
        // main loop's inline construction so a ship that has sunk below a cloud deck can be
        // re-projected and re-drawn OVER the deck (see the cloudDeck branch).
        ZEye? MakeZEye(EntityUid targetMap, float d)
        {
            if (_fallbackEye is null || !_mapQuery.Value.TryComp(targetMap, out var mapComp))
                return null;

            Angle rot = _fallbackEye.Rotation * -1;
            var off = rot.ToWorldVec() * CEClientZLevelsSystem.ZLevelOffset * (d - ownDepth);
            var scale = MathF.Pow(CESharedZLevelsSystem.ZLevelViewShrink, -d);

            return new ZEye(lowestDepth, d, highestDepth)
            {
                Position = new MapCoordinates(_fallbackEye.Position.Position, mapComp.MapId),
                DrawFov = false,
                DrawLight = _fallbackEye.DrawLight,
                DrawParallax = false,
                Offset = _fallbackEye.Offset + off,
                Rotation = _fallbackEye.Rotation,
                Scale = _fallbackEye.Scale * scale,
            };
        }

        var first = true;

        foreach (var (mapUid, depth, allowFov, isTransit) in _zPasses)
        {
            // A cloud layer at or below the observer draws an opaque deck beneath
            // its own pass: deeper passes already rendered vanish under it, grids
            // parked on the layer draw crisp on top of it.
            CEZCloudLayerComponent? cloudDeck = null;
            if (depth <= 0.001f && !isTransit)
                _entityManager.TryGetComponent(mapUid, out cloudDeck);

            if (mapUid == playerMap && depth == 0f)
            {
                // The ZEye branch is forced whenever ANY pass rendered underneath
                // this one (rider origin pass, descent pseudo-map under a bystander,
                // below chain): the raw fallback eye draws parallax, which would
                // paint the skybox over everything already rendered. This is exactly
                // how a bystander's descent pseudo-pass used to vanish the instant
                // the ship hopped maps — their own pass repainted the sky over it.
                if (riderOriginPass || lowestDepth < 0f)
                {
                    // Own eye at normal scale (a descending rider's fall shows in
                    // the receding origin pass below, never in their own deck),
                    // wrapped in a ZEye so downstream
                    // gating (parallax, blur) sees a proper pass. Parallax draws
                    // here only when nothing renders underneath — otherwise the
                    // deepest pass owns the skybox, and repainting it here would
                    // erase those passes. (Descent pseudo-maps are not passes at
                    // all — they composite over the top — so a lone bystander never
                    // takes this branch and keeps an unscaled sky.)
                    viewport.Eye = new ZEye(lowestDepth, 0f, highestDepth)
                    {
                        Position = _fallbackEye.Position,
                        DrawFov = _fallbackEye.DrawFov,
                        DrawLight = _fallbackEye.DrawLight,
                        DrawParallax = lowestDepth == 0f,
                        Offset = _fallbackEye.Offset,
                        Rotation = _fallbackEye.Rotation,
                        Scale = _fallbackEye.Scale,
                    };
                }
                else
                {
                    viewport.Eye = _fallbackEye;
                }
            }
            else
            {
                if (!_mapQuery.Value.TryComp(mapUid, out var mapComp))
                    continue;

                Angle rotation = _fallbackEye.Rotation * -1;
                // The layer offset is relative to the viewer's own altitude plane, not
                // absolute depth: an airborne viewer's eye already slides up via
                // GetEyeOffset (localPosition * ZLevelOffset), so baking their frac
                // into every pass here would double the drift and shear the layers
                // apart. ownDepth is -frac when airborne, 0 otherwise.
                var offset = rotation.ToWorldVec() * CEClientZLevelsSystem.ZLevelOffset * (depth - ownDepth);

                // Perspective: levels below the eye plane shrink, levels above grow.
                // Depths in _zPasses are already fractional (offset by the transit
                // ride's progress), so this is continuous through a handoff — the
                // integer-depth version of this popped at the seam.
                var zScale = MathF.Pow(CESharedZLevelsSystem.ZLevelViewShrink, -depth);

                // A descending rider's world below charges IN, not away: a negative
                // exponent flips the shrink rule into magnification that grows with
                // the fall (1x at ride start — seamless with the pre-warp view). Same
                // single-clock principle as the bystander composite: the whole curve
                // is spent by the end of the Descending ride, so the flip to
                // Vanishing changes nothing on screen — no stage ever touches this
                // exponent. Base levels follow the ride's smoothstep (identical to
                // the old depth-driven curve); the lunge is backloaded (ride^4) so
                // its violent tail lands under the near-opaque cover. The pass depth
                // stays a bare epsilon purely so ordering keeps it under the ship.
                if (riderOriginPass && riderDescent != null && riderDescent.OriginMap == mapUid)
                {
                    var smooth = riderRide * riderRide * (3f - 2f * riderRide);
                    var zoomLevels = riderDescentZoomLevels * smooth
                        + riderVanishZoomLevels * MathF.Pow(riderRide, 4f);
                    zScale = MathF.Pow(CESharedZLevelsSystem.ZLevelViewShrink, -zoomLevels);
                }

                var zEye = new ZEye(lowestDepth, depth, highestDepth)
                {
                    Position = new MapCoordinates(_fallbackEye.Position.Position, mapComp.MapId),
                    // Not gated on depth >= 0: an airborne viewer's own map sits at a
                    // small negative depth but their walls still block sight.
                    DrawFov = _fallbackEye.DrawFov && allowFov,
                    DrawLight = _fallbackEye.DrawLight,
                    // A pass with a cloud deck never wants the skybox: the deck IS
                    // the backdrop, and parallax would paint over it.
                    DrawParallax = !isTransit && depth == lowestDepth && cloudDeck == null,
                    Offset = _fallbackEye.Offset + offset,
                    Rotation = _fallbackEye.Rotation,
                    // A descending rider's origin pass magnifies through the zScale
                    // override above — the one pass whose zoom is driven by fall
                    // progress instead of pass depth.
                    Scale = _fallbackEye.Scale * zScale,
                };

                // Ships overhead get their own transparent pass composited with
                // height-scaled haze and fade, so the fog touches only the ship.
                if (isTransit && depth > 0f)
                {
                    RenderTransitOverhead(renderHandle, viewport, mapUid, zEye, depth);
                    continue;
                }

                viewport.Eye = zEye;
            }

            // A bare deck fill reads as a flat backdrop under the ship: after the
            // pass renders, sparse cloud tops drift back OVER everything so puffs
            // overlap the hull and the deck reads as volume, not paint.
            Color? wispColor = null;

            // The deck under a rider draws right before their own grid's pass:
            // over the below-chain, under the ship. Wisps go here too — this pass
            // is the one carrying the player's FOV, so anything drawn after its
            // Render() would paint cloud tops over the view shadows. Under the
            // grid they lose the over-hull overlap but stay behind occlusion,
            // like the rest of the background.
            if (riderDeck != null && mapUid == playerMap && !isTransit && depth == ownDepth)
            {
                DrawCloudDeck(renderHandle, viewport, riderDeck.CloudColor, 1f);
                DrawCloudWisps(renderHandle, viewport, riderDeck.CloudColor);
                first = false;
            }

            // Mutually exclusive with the rider branch: docked on the layer, the
            // player's map IS the cloud map, and letting both run re-drew the
            // deck over the rider wisps and then queued a SECOND wisp pass after
            // Render() — cloud tops on top of the grid and the view shadows.
            else if (cloudDeck != null)
            {
                // Opaque fill: doubles as the clear when this is the first pass.
                DrawCloudDeck(renderHandle, viewport, cloudDeck.CloudColor, 1f);

                // Ships that have sunk just below this deck render deeper than it, so the
                // opaque fill above hid them the instant they crossed the plane — the pop.
                // Re-render each eye OVER the deck through the transit shader, cloud veil
                // rising and fade falling across CloudDissolveBand of descent, so they
                // sink into the clouds and dissolve instead of vanishing on the spot.
                foreach (var (sinkMap, sinkDepth, _, sinkTransit) in _zPasses)
                {
                    if (!sinkTransit)
                        continue;

                    var below = depth - sinkDepth; // how far this ship sits below the deck
                    if (below <= 0f || below >= CloudDissolveBand)
                        continue;

                    if (MakeZEye(sinkMap, sinkDepth) is { } sinkEye)
                    {
                        // Deeper below the deck = foggier (cloud up) and dimmer (fade down),
                        // so the real hull shows near the plane and dissolves into the clouds
                        // as it sinks, rather than flat-filling to the deck color at once.
                        var t = below / CloudDissolveBand;
                        BlitTransitPass(renderHandle, viewport, sinkEye,
                            hazeColor: Vector3.Zero,
                            strength: 0f,
                            cloudColor: new Vector3(cloudDeck.CloudColor.R, cloudDeck.CloudColor.G, cloudDeck.CloudColor.B),
                            cloud: Math.Clamp(t, 0f, 1f),
                            fade: 1f - t);
                    }
                }

                // LANDED on the layer: this is the player's own pass — the one
                // carrying their FOV — so wisps must go under it, not after it,
                // or they paint over grids and view shadows. (riderDeck only
                // covers the approach; it keys off aboveMap and is null once
                // the cloud map IS the player's map.)
                if (mapUid == playerMap && depth == 0f)
                    DrawCloudWisps(renderHandle, viewport, cloudDeck.CloudColor);
                // Tops only near the observer's plane: a deck a whole level down
                // renders shrunk, and screen-space wisps at full size over it
                // would read as the wrong altitude.
                else if (depth > -0.5f)
                    wispColor = cloudDeck.CloudColor;
                first = false;
            }

            viewport.ClearColor = first ? Color.Black : null;
            first = false;
            viewport.Render();

            if (wispColor != null)
                DrawCloudWisps(renderHandle, viewport, wispColor.Value);
        }

        // Airborne descent composite: every pseudo-map whose origin is the observer's
        // map draws OVER the finished scene, never as a world pass — a pass deep
        // enough to own the skybox would drag the parallax and planet overlays under
        // its scaled eye, shrinking the sky along with the hull. Here only the ship's
        // own pixels are touched: shrink from the scaled eye, haze toward the map's
        // altitude-lerped MapLight, and — once Stage 1.5 hits — a fade by stage
        // progress, so the ship has dissolved before the warp tick deletes the map
        // (no last-frame pop, no dependence on server timing, no ghost bookkeeping:
        // the pseudo-map is still live, so its own rendered eye is the source).
        // Riders skip this: their own map IS the pseudo-map.
        var descentBlitted = false;
        var descentQuery = _entityManager.EntityQueryEnumerator<CEDescentMapComponent>();
        while (descentQuery.MoveNext(out var descentUid, out var descentMap))
        {
            if (descentUid == playerMap || descentMap.OriginMap != playerMap)
                continue;

            // The entire bystander visual — plunge AND fadeout — keys off the one
            // Descending clock. The fadeout is not a stage of its own: it starts
            // midway through the drop and finishes with it, so there is no
            // client-visible handoff when the server flips to Vanishing (that flip
            // replicates late and used to stall the shrink for a beat while the
            // client sat clamped at the end of Descending). By the time Vanishing
            // actually starts the hull has already faded out, so the stage — and the
            // server deleting the map at its end — changes nothing on screen.
            const float fadeStartRide = 0.5f;
            var ride = descentMap.Stage switch
            {
                CEDescentStage.Descending => _descent.GetStageProgress(
                    CEDescentStage.Descending, descentMap.StageStart),
                CEDescentStage.Vanishing or CEDescentStage.Arriving => 1f,
                _ => 0f,
            };
            var fade = Math.Clamp((1f - ride) / (1f - fadeStartRide), 0f, 1f);
            if (fade <= 0.001f)
                continue;

            // Same epsilon the old world pass used: strictly below the observer even
            // at stage start. Same occlusion rule as transit ships, too: nothing
            // draws under a deck the observer can't see through.
            var descentDepth = -_descent.GetDescentDepth(descentMap) - 0.001f;
            if (occludeBelowDepth is { } occluded && descentDepth < occluded - occludeBand)
                continue;

            if (MakeZEye(descentUid, descentDepth) is { } descentEye)
            {
                // One z-level of shrink (ZLevelViewShrink) is nowhere near enough for
                // a ship dropping onto a planet, but zooming the EYE out to speck
                // scale makes the scratch render's view AABB grow like 1/scale² —
                // the renderer ends up walking a planet-sized area per frame by the
                // end of the drop. Instead the scratch render keeps the cheap
                // one-level perspective and the remaining plunge shrinks the blit's
                // destination rect, which is the same screen-space transform at
                // constant render cost. The same ride that drives the fade drives
                // the shrink, squared to backload it (exponential scale
                // interpolation front-loads the apparent size change, so a linear
                // exponent reads as an instant drop-off). Both run off the single
                // Descending clock, so the hull
                // is still visibly shrinking on its last visible frame — it drops
                // out of sight rather than parking and dissolving.
                var speckShrink = MathF.Pow(
                    CESharedDescentSystem.SpeckScale / CESharedZLevelsSystem.ZLevelViewShrink,
                    ride * ride);

                BlitTransitPass(renderHandle, viewport, descentEye,
                    hazeColor: MapHaze(descentUid),
                    // Depth is negative below the observer; -1 by the vanish.
                    strength: Math.Clamp(-descentDepth, 0f, 1f),
                    cloudColor: Vector3.One,
                    cloud: 0f,
                    fade: fade,
                    shrink: speckShrink);
                descentBlitted = true;
            }
        }

        // Those hulls landed on top of the observer's grid wherever they overlapped.
        // Re-render the player's own layer over them: transparent clear, unscaled
        // eye, DrawParallax off (which also gates the planet overlay), so ONLY grids
        // and entities come back — walls occlude the sky again and the backdrop stays
        // untouched. This is one scratch render of one map through the same blit
        // helper; it does NOT re-enter the pass builder, so it cannot recurse.
        if (descentBlitted && _fallbackEye != null)
        {
            var gridEye = new ZEye(lowestDepth, ownDepth, highestDepth)
            {
                Position = _fallbackEye.Position,
                DrawFov = _fallbackEye.DrawFov,
                DrawLight = _fallbackEye.DrawLight,
                DrawParallax = false,
                Offset = _fallbackEye.Offset,
                Rotation = _fallbackEye.Rotation,
                Scale = _fallbackEye.Scale,
            };
            BlitTransitPass(renderHandle, viewport, gridEye,
                hazeColor: Vector3.Zero,
                strength: 0f,
                cloudColor: Vector3.One,
                cloud: 0f,
                fade: 1f);
        }

        // Rider's half of the departure, same single-clock principle as the bystander
        // composite above: the whiteout starts midway through the Descending ride and
        // is FULL by its end, so the late-replicating flip to Vanishing — and the
        // warp's map move behind it — changes nothing on screen. riderRide holds at 1
        // through Vanishing/Arriving, so later stages just keep the cover up; the
        // planet-side arrival owns the fade back down.
        var riderCover = 0f;
        if (riderDescent != null)
        {
            var c = Math.Clamp(
                (riderRide - riderCoverStartRide) / (1f - riderCoverStartRide), 0f, 1f);
            // Smoothstepped: a linear ramp switching on mid-ride has a visible onset
            // crease — the exact pop this rework exists to kill.
            riderCover = c * c * (3f - 2f * c);
        }
        // After the warp the rider sits on the real planet map — no pseudo-map
        // component anymore. The descent state now lives on the grid itself:
        // Vanishing pins the cover full (covers any replication gap between the map
        // hop and the stage flip, in either order), and Arriving fades it back down
        // over the stage. Smoothstepped to match the fade-up, so the whole
        // departure→arrival cover is one continuous white curve with no corner at
        // the teleport.
        else if (playerXform.GridUid is { } riderGrid &&
                 _entityManager.TryGetComponent(riderGrid, out CEDescentComponent? gridDescent))
        {
            switch (gridDescent.Stage)
            {
                case CEDescentStage.Vanishing:
                    riderCover = 1f;
                    break;
                case CEDescentStage.Arriving:
                    var a = 1f - _descent.GetStageProgress(
                        CEDescentStage.Arriving, gridDescent.StageStart);
                    riderCover = a * a * (3f - 2f * a);
                    break;
            }
        }

        if (riderCover > 0.001f)
            DrawCloudDeck(renderHandle, viewport, Color.White, riderCover);

        // Climbing toward a cloud layer overhead: the deck swallows the rider's own
        // view, whiting out completely ~CloudFullCoverDepth below the layer, then
        // breaking through into clear air on top. Descents run the same curve in
        // reverse. aboveDepth is exactly the distance left to the layer.
        if (aboveMap != null &&
            _entityManager.TryGetComponent(aboveMap.Value, out CEZCloudLayerComponent? cloudAbove))
        {
            var coverage = CloudCoverage(aboveDepth);
            if (coverage > 0.001f)
                DrawCloudDeck(renderHandle, viewport, cloudAbove.CloudColor, coverage);
        }

        // Restore the Eye
        Eye = _fallbackEye;
        viewport.Eye = Eye;
    }

    private void RenderTransitOverhead(IRenderHandle renderHandle,
        IClydeViewport viewport,
        EntityUid transitMap,
        ZEye zEye,
        float depth)
    {
        zEye.DrawParallax = false;

        // Veil the ship's own pixels with a cloud deck's color as it nears the layer,
        // whichever side the cloud is on. A cloud ABOVE the gap (UpperMap) fogs the ship
        // as it climbs into the deck from below; a cloud BELOW the gap (LowerMap) fogs it
        // as it descends onto the tops from above — the case that previously had no veil,
        // so descending ships stayed crisp and popped through the cloud plane.
        // CloudCoverage is symmetric about the plane, so the distance to the layer feeds
        // the same curve either way and the fog is continuous through the crossing.
        var cloud = 0f;
        var cloudColor = Vector3.One;
        if (_entityManager.TryGetComponent(transitMap, out CEZTransitMapComponent? transit))
        {
            var progress = GetTransitProgress(transit);

            if (transit.UpperMap is { } upper &&
                _entityManager.TryGetComponent(upper, out CEZCloudLayerComponent? cloudAbove))
            {
                cloud = CloudCoverage(1f - progress);
                cloudColor = new Vector3(cloudAbove.CloudColor.R, cloudAbove.CloudColor.G, cloudAbove.CloudColor.B);
            }

            if (transit.LowerMap is { } lower &&
                _entityManager.TryGetComponent(lower, out CEZCloudLayerComponent? cloudBelow))
            {
                var belowCover = CloudCoverage(progress);
                if (belowCover > cloud)
                {
                    cloud = belowCover;
                    cloudColor = new Vector3(cloudBelow.CloudColor.R, cloudBelow.CloudColor.G, cloudBelow.CloudColor.B);
                }
            }
        }

        BlitTransitPass(renderHandle, viewport, zEye,
            hazeColor: MapHaze(transitMap),
            strength: Math.Clamp(depth, 0f, 1f),
            cloudColor: cloudColor,
            cloud: cloud,
            // Ships dissolve into the sky as they climb away from the observer and
            // materialize out of it on the way down.
            fade: TransitFade(depth));
    }

    /// <summary>
    /// The haze tint for a map at altitude: its own MapLight ambient, which the server
    /// already lerps with height. Transit maps and descent pseudo-maps both keep theirs
    /// current, so one lookup serves every airborne pass.
    /// </summary>
    private Vector3 MapHaze(EntityUid map)
    {
        if (_entityManager.TryGetComponent(map, out MapLightComponent? mapLight))
        {
            return new Vector3(
                mapLight.AmbientLightColor.R,
                mapLight.AmbientLightColor.G,
                mapLight.AmbientLightColor.B);
        }

        return new Vector3(0, 0, 1);
    }

    /// <summary>
    /// Renders a map to the scratch viewport under <paramref name="eye"/> and composites
    /// just its hull over the main target through the transit shader: hazed toward
    /// <paramref name="hazeColor"/> by <paramref name="strength"/>, veiled toward
    /// <paramref name="cloudColor"/> by <paramref name="cloud"/>, drawn at
    /// <paramref name="fade"/>. The single path for every airborne composite — ships
    /// overhead, hulls sinking below a cloud deck, vanishing descent pseudo-maps — so
    /// their gamma/tint treatment can't drift apart.
    /// </summary>
    private void BlitTransitPass(IRenderHandle renderHandle,
        IClydeViewport viewport,
        IEye eye,
        Vector3 hazeColor,
        float strength,
        Vector3 cloudColor,
        float cloud,
        float fade,
        float shrink = 1f)
    {
        if (fade <= 0.001f)
            return;

        if (_transitViewport == null || _transitViewport.Size != viewport.Size)
        {
            _transitViewport?.Dispose();
            _transitViewport = _clyde.CreateViewport(viewport.Size, nameof(_transitViewport));
            _transitViewport.RenderScale = viewport.RenderScale;
        }

        _transitBlitShader ??= _prototypeManager.Index<ShaderPrototype>("CEZBlurBlit").InstanceUnique();

        _transitViewport.Eye = eye;
        // NOT Color.Transparent: that's WHITE with zero alpha, and any blend or blur
        // leakage turns it into a white wash. Transparent black misbehaves invisibly.
        _transitViewport.ClearColor = new Color(0f, 0f, 0f, 0f);
        _transitViewport.Render();

        var screenHandle = renderHandle.DrawingHandleScreen;
        screenHandle.RenderInRenderTarget(viewport.RenderTarget, () =>
        {
            var texture = _transitViewport.RenderTarget.Texture;

            _transitBlitShader.SetParameter("BLUR_COLOR", hazeColor);
            _transitBlitShader.SetParameter("STRENGTH", Math.Clamp(strength, 0f, 1f));
            _transitBlitShader.SetParameter("CLOUD_COLOR", cloudColor);
            _transitBlitShader.SetParameter("CLOUD", Math.Clamp(cloud, 0f, 1f));
            _transitBlitShader.SetParameter("FADE", Math.Clamp(fade, 0f, 1f));

            screenHandle.UseShader(_transitBlitShader);
            // Scaling the destination rect about the viewport centre is exactly a
            // further eye zoom-out (both multiply screen-space displacements from
            // centre), but the world-space render stays one z-level wide — this is
            // how a descending ship reaches speck scale without the scratch pass
            // rendering a continent.
            var size = (Vector2)texture.Size;
            var half = size / 2f;
            var rect = shrink < 1f
                ? new UIBox2(half - half * shrink, half + half * shrink)
                : new UIBox2(Vector2.Zero, size);
            screenHandle.DrawTextureRect(texture, rect);
            screenHandle.UseShader(null);
        }, null);
    }

    /// <summary>
    /// Levels of descent over which a below-observer ship dissolves into a cloud deck
    /// beneath it: full whiteout at the plane, clear this far above.
    /// </summary>
    private const float CloudDissolveBand = 0.5f;

    /// <summary>
    /// How many z-levels of climb it takes for a transiting ship seen from below to
    /// fully dissolve into the sky.
    /// </summary>
    public const float TransitFadeDepth = 0.8f;

    private static float TransitFade(float depth)
    {
        return Math.Clamp(1f - depth / TransitFadeDepth, 0f, 1f);
    }

    /// <summary>
    /// Depth below a cloud layer at which the deck hits full force: a climbing grid
    /// whites out completely here, then breaks through into clear air just above.
    /// </summary>
    public const float CloudFullCoverDepth = 0.25f;

    /// <summary>
    /// Depth span the breakthrough takes. The falloff starts from a total whiteout,
    /// so the reveal reads as popping out of the cloud tops, not a discontinuity.
    /// </summary>
    private const float CloudBreakthroughBand = 0.1f;

    /// <summary>
    /// Cloud coverage over a grid by its depth below a cloud layer (0 = at the
    /// layer plane, 1 = a whole level under it). Rising: fog thickens from the gap
    /// floor to a full whiteout at <see cref="CloudFullCoverDepth"/>, then clears
    /// fast — above that, ships sit on top of the deck unobscured. Symmetric for
    /// descents.
    /// </summary>
    private static float CloudCoverage(float depthBelowLayer)
    {
        if (depthBelowLayer <= 0f)
            return 0f;

        if (depthBelowLayer >= CloudFullCoverDepth)
            return Math.Clamp((1f - depthBelowLayer) / (1f - CloudFullCoverDepth), 0f, 1f);

        return Math.Clamp(
            (depthBelowLayer - (CloudFullCoverDepth - CloudBreakthroughBand)) / CloudBreakthroughBand,
            0f,
            1f);
    }

    /// <summary>
    /// Fullscreen procedural cloud fill into the viewport's target. Coverage 1 is
    /// fully opaque (the deck under a cloud layer pass, also standing in for the
    /// clear); fractional coverage is the wispy veil over a climbing rider's view.
    /// </summary>
    private void DrawCloudDeck(IRenderHandle renderHandle, IClydeViewport viewport, Color color, float coverage)
    {
        _cloudShader ??= _prototypeManager.Index<ShaderPrototype>("CEZClouds").InstanceUnique();

        var screenHandle = renderHandle.DrawingHandleScreen;
        screenHandle.RenderInRenderTarget(viewport.RenderTarget, () =>
        {
            _cloudShader.SetParameter("CLOUD_COLOR", new Vector3(color.R, color.G, color.B));
            _cloudShader.SetParameter("COVERAGE", coverage);
            // Shared instance: a prior wisp draw leaves WISP set, which would
            // hijack the deck fill into tops mode.
            _cloudShader.SetParameter("WISP", 0f);

            screenHandle.UseShader(_cloudShader);
            screenHandle.DrawRect(new UIBox2(Vector2.Zero, viewport.RenderTarget.Texture.Size), Color.White);
            screenHandle.UseShader(null);
        }, null);
    }

    /// <summary>
    /// Sparse drifting cloud tops drawn back over an already-rendered pass, so a
    /// grid sitting on a deck gets overlapped by puffs instead of floating on a
    /// flat fill. Thresholded alpha in the shader keeps the gaps fully clear.
    /// </summary>
    private void DrawCloudWisps(IRenderHandle renderHandle, IClydeViewport viewport, Color color)
    {
        _cloudShader ??= _prototypeManager.Index<ShaderPrototype>("CEZClouds").InstanceUnique();

        var screenHandle = renderHandle.DrawingHandleScreen;
        screenHandle.RenderInRenderTarget(viewport.RenderTarget, () =>
        {
            _cloudShader.SetParameter("CLOUD_COLOR", new Vector3(color.R, color.G, color.B));
            _cloudShader.SetParameter("COVERAGE", 0f);
            _cloudShader.SetParameter("WISP", 0.85f);

            screenHandle.UseShader(_cloudShader);
            screenHandle.DrawRect(new UIBox2(Vector2.Zero, viewport.RenderTarget.Texture.Size), Color.White);
            screenHandle.UseShader(null);
        }, null);
    }

    private float GetTransitProgress(CEZTransitMapComponent transit)
    {
        if (transit.PrimaryGrid is { } grid &&
            _entityManager.TryGetComponent(grid, out CEZPhysicsComponent? zPhys))
        {
            return Math.Clamp(zPhys.LocalPosition, 0f, 1f);
        }

        return 0f;
    }

    public sealed class ZEye(float lowest, float depth, float high) : Robust.Shared.Graphics.Eye
    {
        public float LowestDepth = lowest;
        public float Depth = depth;
        public float HighestDepth = high;

        /// <summary>
        /// Whether the parallax may draw on this pass. Only the deepest LEVEL pass
        /// wants it; transit passes (mostly-empty maps holding a moving ship) must
        /// never paint the skybox over the already-rendered world.
        /// </summary>
        public bool DrawParallax = true;
    }
}
