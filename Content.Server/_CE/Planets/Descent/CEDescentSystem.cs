/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Content.Server._CE.ZLevels.Core;
using Content.Server.Shuttles.Systems;
using Content.Shared._CE.Planets;
using Content.Shared._CE.Planets.Descent;
using Content.Shared._CE.ZLevels.Core.Components;
using Content.Shared.Parallax;
using Robust.Server.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server._CE.Planets.Descent;

/// <summary>
/// Runs the planet descent sequence. Any chargeup/spinup theatre is the
/// shuttle console's business and happens BEFORE this system is invoked; the sequence
/// starts already falling:
/// Descending (2s, ship rides a bare pseudo-map; riders zoom out, bystanders watch it shrink) →
/// Vanishing (3s, whiteout/fade) →
/// WARP (one tick: leaves the pseudo-map for the target z-network) →
/// Arriving (2s, fade back in) → done.
///
/// The pseudo-map is deliberately OUTSIDE the z machinery (no CEZTransitMapComponent, no
/// network membership): the client pass builder degrades it to a single own-map pass with
/// parallax, and the descent visuals hang off <see cref="CEDescentMapComponent"/> alone.
/// </summary>
public sealed class CEDescentSystem : CESharedDescentSystem
{
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly ShuttleConsoleSystem _console = default!;
    [Dependency] private readonly DockingSystem _dockSystem = default!;
    [Dependency] private readonly PvsOverrideSystem _pvsOverride = default!;
    [Dependency] private readonly CEZLevelsSystem _zLevels = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CEDescentComponent, ComponentShutdown>(OnDescentShutdown);
    }

    /// <summary>
    /// Starts the descent sequence for <paramref name="grid"/> onto
    /// <paramref name="planet"/>. Descents only target planets: the z-stack is the
    /// planet's own network, and the planet feeds the sky visuals. Fails if the
    /// planet has no landable z-network.
    /// </summary>
    public bool TryStartDescent(
        Entity<MapGridComponent> grid,
        Entity<CEPlanetComponent> planet)
    {
        if (planet.Comp.Network is not { } networkUid ||
            !HasComp<CEZMapNetworkComponent>(networkUid))
        {
            return false;
        }

        if (HasComp<CEDescentComponent>(grid))
            return false;

        if (Transform(grid).MapUid is not { } sourceMap)
            return false;

        // No descents from mid-gap or from another descent's pseudo-map.
        if (HasComp<CEZTransitMapComponent>(sourceMap) || HasComp<CEDescentMapComponent>(sourceMap))
            return false;

        // The whole docked set rides along, same as transit entry.
        var gridSet = new HashSet<EntityUid>();
        _shuttle.GetAllDockedShuttles(grid, gridSet);
        gridSet.Add(grid);
        gridSet.RemoveWhere(uid => Transform(uid).MapUid != sourceMap);

        var descent = AddComp<CEDescentComponent>(grid);
        descent.StageStart = Timing.CurTime;
        descent.Planet = planet.Owner;
        descent.Network = networkUid;
        descent.GridSet = gridSet;
        Dirty(grid, descent);

        foreach (var member in gridSet)
        {
            // The ship is disabled the moment the sequence starts.
            _shuttle.Disable(member);

            // Everyone gets to watch the departure, not just PVS neighbours (the
            // pseudo-map pass renders it for the whole origin map).
            _pvsOverride.AddGlobalOverride(member);
        }

        // No chargeup stage — the console handled any spinup before calling us —
        // so the ship starts falling on the same tick the sequence begins.
        BeginDescending((grid.Owner, descent));
        return true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<CEDescentComponent>();
        while (query.MoveNext(out var uid, out var descent))
        {
            if (Timing.CurTime < descent.StageStart + StageDuration(descent.Stage))
                continue;

            AdvanceStage((uid, descent));
        }
    }

    private void AdvanceStage(Entity<CEDescentComponent> ent)
    {
        switch (ent.Comp.Stage)
        {
            case CEDescentStage.Descending:
                // Stage 1 → 1.5: purely a client visual change (fade), stage bump only.
                SetStage(ent, CEDescentStage.Vanishing);
                break;

            case CEDescentStage.Vanishing:
                Warp(ent);
                break;

            case CEDescentStage.Arriving:
                Finish(ent);
                break;
        }
    }

    /// <summary>
    /// Stage 1 entry: build the pseudo-map and move the docked set onto it in place.
    /// From here the origin map renders the set as a shrinking below-pass, and riders
    /// get the zoom-out + snapshot planet below.
    /// </summary>
    private void BeginDescending(Entity<CEDescentComponent> ent)
    {
        if (Transform(ent).MapUid is not { } originMap)
        {
            Abort(ent);
            return;
        }

        var mapUid = _map.CreateMap(out _);

        var descentMap = AddComp<CEDescentMapComponent>(mapUid);
        descentMap.OriginMap = originMap;
        descentMap.Grid = ent;

        // Sky visuals: snapshot, not a reference — the live planet entity stays on the
        // origin map and may leave the riders' PVS after the move.
        if (TryComp<CEPlanetComponent>(ent.Comp.Planet, out var planet))
        {
            descentMap.PlanetSprite = planet.Sprite;
            descentMap.PlanetSpinRate = planet.SpinRate;
            descentMap.PlanetScale = planet.MaxScale;
        }

        // Same environment as the target network's levels (atmosphere etc.), mirroring
        // CreateTransitMap: the crew is already in the planet's air column.
        if (TryComp<CEZMapNetworkComponent>(ent.Comp.Network, out var network) &&
            network.Components.Count > 0)
        {
            EntityManager.AddComponents(mapUid, network.Components, removeExisting: false);
        }

        // Lit like the sky the ship just left; the vanish whiteout owns any blending.
        var light = EnsureComp<MapLightComponent>(mapUid);
        if (TryComp<MapLightComponent>(originMap, out var originLight))
            light.AmbientLightColor = originLight.AmbientLightColor;
        Dirty(mapUid, light);

        // Once the ship hops over, this map is the deepest pass on bystanders'
        // screens and owns the skybox their own pass no longer repaints — same
        // sky as home, or the backdrop pops to black for the whole descent.
        if (TryComp<ParallaxComponent>(originMap, out var originParallax))
        {
            var parallax = EnsureComp<ParallaxComponent>(mapUid);
            parallax.Parallax = originParallax.Parallax;
            Dirty(mapUid, parallax);
        }

        _meta.SetEntityName(mapUid, $"Descent of {MetaData(ent).EntityName}");

        ent.Comp.DescentMap = mapUid;
        MoveGridSet(ent.Comp.GridSet, mapUid);

        SetStage(ent, CEDescentStage.Descending);
    }

    /// <summary>
    /// STAGE 2, "WARP!" — one tick, under the full whiteout. Inserts the set into REAL
    /// transit directly above the network's top level ("highest height possible"):
    /// LowerMap = the top level, open sky above. From there the normal z machinery owns
    /// it — the crew fades back in (Arriving) already airborne over the planet and flies
    /// the rest of the way down. Routed through the transit-style map-move events, so
    /// passenger z caches (CEZPhysicsComponent.CurrentZLevel) update.
    /// </summary>
    private void Warp(Entity<CEDescentComponent> ent)
    {
        EntityUid? topMap = null;
        if (TryComp<CEZMapNetworkComponent>(ent.Comp.Network, out var network))
        {
            // Enter at the very top of the stack — descending from orbit.
            for (var i = network.SortedZLevels.Count - 1; i >= 0; i--)
            {
                var level = network.SortedZLevels[i];
                if (HasComp<CEZMapComponent>(level))
                {
                    topMap = level;
                    break;
                }
            }
        }

        if (topMap == null)
        {
            // Network died mid-sequence: put the set back where it came from.
            Abort(ent);
            return;
        }

        // Re-anchor: origin-map (deep space) coordinates mean nothing in the planet's
        // z-stack. Keep the set's relative layout, centre the lead grid over the stack
        // origin — done on the pseudo-map, where there's nothing to collide with.
        // TODO(descent): designated arrival zones instead of 0,0.
        RecenterSetOn(ent.Comp.GridSet, ent.Owner, Vector2.Zero);

        if (!_zLevels.InsertSetIntoTransitAbove(ent.Owner, ent.Comp.GridSet, topMap.Value))
        {
            Abort(ent);
            return;
        }

        DeleteDescentMap(ent);
        SetStage(ent, CEDescentStage.Arriving);
    }

    /// <summary>
    /// Translates a docked set so <paramref name="lead"/> sits at <paramref name="target"/>,
    /// preserving the members' relative layout (and so their docks).
    /// </summary>
    private void RecenterSetOn(HashSet<EntityUid> grids, EntityUid lead, Vector2 target)
    {
        var delta = target - _transform.GetWorldPosition(lead);
        if (delta == Vector2.Zero)
            return;

        foreach (var gridUid in grids)
        {
            if (TerminatingOrDeleted(gridUid))
                continue;

            _transform.SetWorldPosition(gridUid, _transform.GetWorldPosition(gridUid) + delta);
        }
    }

    private void Finish(Entity<CEDescentComponent> ent)
    {
        foreach (var member in ent.Comp.GridSet)
        {
            if (TerminatingOrDeleted(member))
                continue;

            _shuttle.Enable(member);

            // Post-warp the set is usually still airborne in REAL transit, whose own
            // machinery added (and will remove) its global override on landing —
            // stripping it here would pop the ship out of spectators' view mid-air.
            // Aborts put the set back on the origin map, where it's ours to remove.
            if (!HasComp<CEZTransitMapComponent>(Transform(member).MapUid))
                _pvsOverride.RemoveGlobalOverride(member);
        }

        RemComp<CEDescentComponent>(ent);
    }

    /// <summary>
    /// Something went wrong mid-sequence: return the set to the origin map (if it still
    /// exists), clean up, and hand the ship back.
    /// </summary>
    private void Abort(Entity<CEDescentComponent> ent)
    {
        if (TryComp<CEDescentMapComponent>(ent.Comp.DescentMap, out var descentMap) &&
            descentMap.OriginMap is { } origin &&
            !TerminatingOrDeleted(origin))
        {
            MoveGridSet(ent.Comp.GridSet, origin);
        }

        DeleteDescentMap(ent);
        Finish(ent);
    }

    private void DeleteDescentMap(Entity<CEDescentComponent> ent)
    {
        if (ent.Comp.DescentMap is { } map && !TerminatingOrDeleted(map))
            QueueDel(map);

        ent.Comp.DescentMap = null;
        Dirty(ent);
    }

    /// <summary>
    /// Moves a docked set to another map preserving world position/rotation/velocity.
    /// Trimmed copy of CEZLevelsSystem.MoveGridSetToMap — deliberately WITHOUT the
    /// z-level move events: the pseudo-map is outside the z machinery and raising
    /// (offset, depth) events for it would poison passenger z caches.
    /// </summary>
    private void MoveGridSet(HashSet<EntityUid> grids, EntityUid targetMap)
    {
        foreach (var grid in grids)
        {
            if (TerminatingOrDeleted(grid))
                continue;

            var xform = Transform(grid);
            var worldPos = _transform.GetWorldPosition(xform);
            var worldRot = _transform.GetWorldRotation(xform);

            // The map change wipes joints and can reset momentum, so save and restore it.
            var linVel = Vector2.Zero;
            var angVel = 0f;
            TryComp<PhysicsComponent>(grid, out var body);
            if (body != null)
            {
                linVel = body.LinearVelocity;
                angVel = body.AngularVelocity;
            }

            _transform.SetCoordinates(grid, xform, new EntityCoordinates(targetMap, worldPos), rotation: worldRot);

            if (body != null)
            {
                _physics.SetLinearVelocity(grid, linVel, body: body);
                _physics.SetAngularVelocity(grid, angVel, body: body);
            }
        }

        foreach (var grid in grids)
        {
            if (TerminatingOrDeleted(grid))
                continue;

            _dockSystem.RedockDocks(grid);
            _console.RefreshShuttleConsoles(grid);
        }
    }

    private void SetStage(Entity<CEDescentComponent> ent, CEDescentStage stage)
    {
        ent.Comp.Stage = stage;
        ent.Comp.StageStart = Timing.CurTime;
        Dirty(ent);

        // Mirror onto the pseudo-map so the client render code reads one component.
        if (TryComp<CEDescentMapComponent>(ent.Comp.DescentMap, out var descentMap))
        {
            descentMap.Stage = stage;
            descentMap.StageStart = ent.Comp.StageStart;
            Dirty(ent.Comp.DescentMap.Value, descentMap);
        }
    }

    /// <summary>
    /// The descending grid got deleted out from under the sequence: don't leak the
    /// pseudo-map (its deletion takes any stranded passengers with it — acceptable for
    /// now, they were mid-fall).
    /// </summary>
    private void OnDescentShutdown(Entity<CEDescentComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.DescentMap is { } map && !TerminatingOrDeleted(map))
            QueueDel(map);
    }
}
