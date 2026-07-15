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
using Content.Shared.Damage;
using Content.Shared.Mobs.Components;
using Content.Shared.Parallax;
using Content.Shared.Stunnable;
using Content.Server.Shuttles.Components;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Random;

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
    [Dependency] private readonly ThrusterSystem _thruster = default!;
    [Dependency] private readonly PvsOverrideSystem _pvsOverride = default!;
    [Dependency] private readonly CEZLevelsSystem _zLevels = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CEDescentComponent, ComponentShutdown>(OnDescentShutdown);
        SubscribeLocalEvent<CEDescentSpinupComponent, ComponentStartup>(OnSpinupStartup);
        SubscribeLocalEvent<CEDescentSpinupComponent, ComponentShutdown>(OnSpinupShutdown);
        SubscribeLocalEvent<CEDescentStunnedComponent, ComponentShutdown>(OnStunnedShutdown);
        SubscribeLocalEvent<ShuttleConsoleComponent, CEDescentRequestMessage>(OnDescentRequest);
        SubscribeLocalEvent<ThrusterComponent, DamageChangedEvent>(OnThrusterDamaged);
    }

    /// <summary>
    /// The drive swapped to charging: kick everything docked off the grid, same as
    /// expedition FTL (<see cref="DockingSystem.UndockDocks"/>). A charging grid
    /// always flies solo, so the brake, the discharge stun and the descent handoff
    /// never have to reason about a docked chain.
    /// </summary>
    private void OnSpinupStartup(Entity<CEDescentSpinupComponent> ent, ref ComponentStartup args)
    {
        _dockSystem.UndockDocks(ent.Owner);
    }

    /// <summary>
    /// An engine took a hit. If the charging grid owns it, the charge aborts
    /// violently: the spinup is stripped, the drive is stunned into a respool and the
    /// grid stays pilot-locked with cold thrusters until it ends (see
    /// <see cref="AbortCharge"/>).
    /// </summary>
    private void OnThrusterDamaged(Entity<ThrusterComponent> ent, ref DamageChangedEvent args)
    {
        // Only actual damage matters — healing a thruster mid-charge is fine.
        if (!args.DamageIncreased || args.DamageDelta == null)
            return;

        // A wrecked, switched-off engine taking further hits isn't news to the drive.
        if (!ent.Comp.Enabled)
            return;

        if (Transform(ent).GridUid is not { } thrusterGrid)
            return;

        // Charging grids fly solo (everything undocks at spinup start), so only the
        // charging grid's own engines matter.
        if (HasComp<CEDescentSpinupComponent>(thrusterGrid))
            AbortCharge(thrusterGrid);
    }

    /// <summary>
    /// Kills a running charge and discharges the drive violently. The grid gets a
    /// <see cref="CEDescentStunnedComponent"/> blocking re-requests until
    /// <see cref="CESharedDescentSystem.DriveRespoolTime"/> elapses (and driving
    /// the client shake/buzz + the console's red countdown), and everyone aboard is
    /// knocked flat for <see cref="CESharedDescentSystem.DischargeStunTime"/>.
    /// The ship stays dead in space for the whole respool: the stun takes over the
    /// spinup's pilot lock (so the spinup's shutdown doesn't hand it back early)
    /// and <see cref="Update"/> keeps the thrusters/gyros forced cold.
    /// Only the charging grid itself is hit — everything docked was already kicked
    /// off when the charge began (<see cref="OnSpinupStartup"/>).
    /// </summary>
    public void AbortCharge(EntityUid uid)
    {
        if (!TryComp<CEDescentSpinupComponent>(uid, out var spinup))
            return;

        if (!TerminatingOrDeleted(uid))
        {
            var stunned = EnsureComp<CEDescentStunnedComponent>(uid);
            stunned.Start = Timing.CurTime;
            stunned.End = Timing.CurTime + DriveRespoolTime;
            Dirty(uid, stunned);

            // Keep the grid pilot-locked for the respool: adopt the lock the spinup
            // added (removing it from the spinup's set BEFORE the spinup is stripped,
            // so its shutdown doesn't return it), or add our own if the grid somehow
            // wasn't locked yet. Grids locked by someone else (e.g. arrivals) are
            // left alone — their owner returns the lock, not us.
            if (spinup.PilotLocked.Remove(uid))
                stunned.PilotLocked = true;
            else if (!HasComp<PreventPilotComponent>(uid))
            {
                AddComp<PreventPilotComponent>(uid);
                stunned.PilotLocked = true;
            }
        }

        RemComp<CEDescentSpinupComponent>(uid);

        // Everyone standing on the grid gets thrown off their feet.
        var mobs = EntityQueryEnumerator<MobStateComponent, TransformComponent>();
        while (mobs.MoveNext(out var mobUid, out _, out var xform))
        {
            if (xform.GridUid == uid)
                _stun.TryParalyze(mobUid, DischargeStunTime, true);
        }
    }

    /// <summary>
    /// A pilot confirmed a descent on the shuttle console. Validation, spinup start and
    /// the refusal popup live in the shared, predicted handler (the client runs the same
    /// code the moment the pilot clicks); the descent proper starts when the spinup
    /// elapses (see <see cref="Update"/>).
    /// </summary>
    private void OnDescentRequest(EntityUid uid, ShuttleConsoleComponent component, CEDescentRequestMessage args)
        => OnDescentRequest(uid, args);

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

        // Console spinup theatre → hand over to the descent proper once it elapses.
        var spinups = EntityQueryEnumerator<CEDescentSpinupComponent, MapGridComponent>();
        while (spinups.MoveNext(out var uid, out var spinup, out var grid))
        {
            // Engines charging: the ship is committed, so hold it still — pilot input
            // locked out, thrusters cold, and any residual drift braked hard to zero.
            ChargingBrake(uid, spinup, frameTime);

            if (Timing.CurTime < spinup.End)
                continue;

            var planetUid = spinup.Planet;
            RemCompDeferred<CEDescentSpinupComponent>(uid);

            if (TryComp<CEPlanetComponent>(planetUid, out var planet))
                TryStartDescent((uid, grid), (planetUid, planet));
        }

        // Respool stuns tick down and clear themselves. Until they do, the drive is
        // dead: pilot input is locked (PreventPilot, owned by the stun) and the
        // thrusters/gyros are re-forced cold every tick in case anything — mapping
        // tools, other systems, a docked console — switched them back on.
        var stunnedQuery = EntityQueryEnumerator<CEDescentStunnedComponent>();
        while (stunnedQuery.MoveNext(out var uid, out var stunned))
        {
            if (Timing.CurTime >= stunned.End)
            {
                RemCompDeferred<CEDescentStunnedComponent>(uid);
                continue;
            }

            if (TryComp<ShuttleComponent>(uid, out var shuttle))
            {
                _thruster.DisableLinearThrusters(shuttle);
                _thruster.SetAngularThrust(shuttle, false);
            }
        }

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
        // z-stack. Keep the set's relative layout, drop the lead grid at a random point
        // inside the planet's landing area — done on the pseudo-map, where there's
        // nothing to collide with.
        RecenterSetOn(ent.Comp.GridSet, ent.Owner, PickLandingPoint(ent));

        if (!_zLevels.InsertSetIntoTransitAbove(ent.Owner, ent.Comp.GridSet, topMap.Value))
        {
            Abort(ent);
            return;
        }

        DeleteDescentMap(ent);
        SetStage(ent, CEDescentStage.Arriving);
    }

    /// <summary>
    /// Where the set comes out of the warp: a random point inside the planet's landing
    /// disc (uniform over the area, not the radius), or the stack origin when the planet
    /// has no landing radius. The point is rolled fresh per descent, so consecutive
    /// arrivals scatter instead of stacking on 0,0.
    /// </summary>
    private Vector2 PickLandingPoint(Entity<CEDescentComponent> ent)
    {
        if (!TryComp<CEPlanetComponent>(ent.Comp.Planet, out var planet) ||
            planet.LandingRadius <= 0f)
        {
            return Vector2.Zero;
        }

        // sqrt for a uniform distribution over the disc rather than clustering at the centre.
        var distance = planet.LandingRadius * MathF.Sqrt(_random.NextFloat());
        return _random.NextAngle().ToVec() * distance;
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

    /// <summary>
    /// While the engines are charging (spinup), the ship is committed: pilot input is
    /// locked out, thrusters are forced cold and any residual velocity is rapidly
    /// braked to a standstill so the descent starts from a clean, stationary pose.
    /// Only the charging grid itself — anything docked was undocked at spinup start.
    /// </summary>
    private void ChargingBrake(EntityUid uid, CEDescentSpinupComponent spinup, float frameTime)
    {
        // Lock out piloting — but only remember grids WE locked, so a grid that
        // was already pilot-locked (e.g. arrivals) keeps its lock afterwards.
        if (!HasComp<PreventPilotComponent>(uid) && spinup.PilotLocked.Add(uid))
            AddComp<PreventPilotComponent>(uid);

        if (TryComp<ShuttleComponent>(uid, out var shuttle))
        {
            _thruster.DisableLinearThrusters(shuttle);
            _thruster.SetAngularThrust(shuttle, false);
        }

        if (!TryComp<PhysicsComponent>(uid, out var body))
            return;

        // Exponential brake: ~99.9% of the velocity is gone within half a second.
        var damp = MathF.Pow(1e-6f, frameTime);

        var linVel = body.LinearVelocity * damp;
        var angVel = body.AngularVelocity * damp;

        // Snap the tail to a hard zero instead of chasing denormals.
        if (linVel.LengthSquared() < 0.01f)
            linVel = Vector2.Zero;
        if (MathF.Abs(angVel) < 0.01f)
            angVel = 0f;

        if (linVel != body.LinearVelocity)
            _physics.SetLinearVelocity(uid, linVel, body: body);
        if (angVel != body.AngularVelocity)
            _physics.SetAngularVelocity(uid, angVel, body: body);
    }

    /// <summary>
    /// Spinup over (descent started, console-aborted or grid deleted): give back the
    /// pilot locks we added during <see cref="ChargingBrake"/>.
    /// </summary>
    private void OnSpinupShutdown(Entity<CEDescentSpinupComponent> ent, ref ComponentShutdown args)
    {
        foreach (var member in ent.Comp.PilotLocked)
        {
            if (!TerminatingOrDeleted(member))
                RemCompDeferred<PreventPilotComponent>(member);
        }

        ent.Comp.PilotLocked.Clear();
    }

    /// <summary>
    /// Respool over (timer elapsed or grid deleted): hand back the pilot lock the stun
    /// owned, if any (see <see cref="CEDescentStunnedComponent.PilotLocked"/>).
    /// </summary>
    private void OnStunnedShutdown(Entity<CEDescentStunnedComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.PilotLocked && !TerminatingOrDeleted(ent.Owner))
            RemCompDeferred<PreventPilotComponent>(ent.Owner);
    }
}
