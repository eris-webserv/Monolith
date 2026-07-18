/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Content.Server._CE.ZLevels.Core;
using Content.Server._CE.ZLevels.Core.Components;
using Content.Shared._CE.Planets;
using Content.Shared._CE.Planets.Descent;
using Content.Shared._CE.ZLevels.Core.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._CE.Planets.Descent;

/// <summary>
/// The ascent half of the theatre: leaving a planet by flying up. There is no console
/// button for this — a pilot simply keeps climbing. When the transit integrator clamps
/// a convoy against the open sky at the top of a planet's z-stack it raises
/// <see cref="CEZOpenSkyClimbEvent"/> every tick the push lasts; holding the climb for
/// <see cref="BreachSpoolTime"/> breaches orbit and runs the descent sequence in
/// reverse: pseudo-map ride (world falls away) → whiteout → warp to a random clear
/// point inside the planet's zone on its space map → fade-in, parked in space.
///
/// A planetary shield is deliberately never consulted on this path: the dome gates
/// entry, not exit. Ships grounded behind a raised shield can always leave.
/// </summary>
public sealed partial class CEDescentSystem
{
    [Dependency] private IMapManager _mapManager = default!;

    /// <summary>
    /// How long a pilot must hold the climb against the open sky before orbit breaks.
    /// Long enough to make it a decision, short enough to feel like straining engines
    /// rather than a menu.
    /// </summary>
    private static readonly TimeSpan BreachSpoolTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The climb event stops arriving the moment input drops; anything longer than
    /// this without a push resets the spool. Generous enough to survive prediction
    /// hiccups and key rollover without letting pilots feather the throttle.
    /// </summary>
    private static readonly TimeSpan BreachInputGap = TimeSpan.FromSeconds(0.25);

    /// <summary>
    /// Once the spool completes the drive telegraphs the launch: the ship hangs at
    /// the ceiling with a warning ring on every nav screen in range for this long
    /// before the ascent actually starts. This is the interdiction window — an
    /// engine shot during the telegraph aborts the launch violently
    /// (see <see cref="AbortAscentWarning"/>).
    /// </summary>
    public static readonly TimeSpan AscentWarningTime = TimeSpan.FromSeconds(10);

    /// <summary>Scratch list for expiring stale breach charges from Update.</summary>
    private readonly List<EntityUid> _staleCharges = new();

    /// <summary>
    /// Breaches that completed this tick. The climb event fires from inside the
    /// transit integrator, mid-move over the very grid involved — rearranging maps
    /// under it would be a disaster, so the actual ascent starts from Update.
    /// </summary>
    private readonly List<(EntityUid Grid, EntityUid Planet)> _pendingAscents = new();

    private readonly List<EntityUid> _endedWarnings = new();

    private void InitializeAscent()
    {
        SubscribeLocalEvent<MapGridComponent, CEZOpenSkyClimbEvent>(OnOpenSkyClimb);
    }

    /// <summary>
    /// A convoy is pushing up against the open sky at the top of a z-network and the
    /// integrator clamped it there. If that network belongs to a planet, feed the
    /// breach spool — the countdown goes out through the same console readout as a
    /// ground liftoff spool, so the crew sees "launch in N" either way.
    /// </summary>
    private void OnOpenSkyClimb(Entity<MapGridComponent> ent, ref CEZOpenSkyClimbEvent args)
    {
        // A convoy strung across several layers can't jump to orbit as one piece;
        // shed the trailing layers first.
        if (args.ConvoyLayers != 1)
            return;

        // Already mid-sequence (either direction), telegraphing a launch, or dead
        // in the water from a drive discharge.
        if (HasComp<CEDescentComponent>(ent) || HasComp<CEDescentSpinupComponent>(ent)
            || HasComp<CEAscentWarningComponent>(ent) || HasComp<CEDescentStunnedComponent>(ent))
            return;

        // Open sky over a non-planet network is just a ceiling.
        if (FindPlanetOfLevel(args.TopLevel) is not { } planetUid)
            return;

        var now = Timing.CurTime;

        // The drive charges while the pilot strains. Networked timestamps, so every
        // console renders the countdown client-side, exactly like a descent spinup —
        // no per-tick countdown traffic. Engines are deliberately fair game during
        // this phase: the charge only holds while the climb does, and a shot-out
        // thruster already costs the climb. Interdiction starts at the telegraph.
        var charge = EnsureComp<CEAscentChargeComponent>(ent.Owner);
        if (charge.End == TimeSpan.Zero
            || now - charge.LastPush > BreachInputGap
            || charge.Planet != planetUid)
        {
            charge.Planet = planetUid;
            charge.Start = now;
            charge.End = now + BreachSpoolTime;
            Dirty(ent.Owner, charge);
        }

        charge.LastPush = now;

        if (now < charge.End)
            return;

        RemComp<CEAscentChargeComponent>(ent.Owner);

        // Spool complete: the engines are charged, but nothing moves yet — the drive
        // telegraphs the launch for AscentWarningTime first. Networked so every nav
        // screen in range (crew and interdictors alike) draws the warning ring around
        // the ship itself; UpdateAscent fires the real ascent when it elapses.
        var warning = AddComp<CEAscentWarningComponent>(ent.Owner);
        warning.Planet = planetUid;
        warning.Start = now;
        warning.End = now + AscentWarningTime;
        Dirty(ent.Owner, warning);
    }

    /// <summary>
    /// Per-tick ascent housekeeping: expire breach spools whose pilot let go (clearing
    /// the console countdown), and start ascents that completed their spool last tick.
    /// </summary>
    private void UpdateAscent()
    {
        // Expire breach charges whose pilot let go (or whose planet died under them):
        // the climb event stops arriving with the input, so a stale LastPush ends the
        // charge. The charge restarts from zero on the next push.
        var now = Timing.CurTime;
        _staleCharges.Clear();
        var chargeQuery = EntityQueryEnumerator<CEAscentChargeComponent>();
        while (chargeQuery.MoveNext(out var chargeUid, out var charge))
        {
            if (TerminatingOrDeleted(chargeUid))
                continue;

            if (TerminatingOrDeleted(charge.Planet) || now - charge.LastPush > BreachInputGap)
                _staleCharges.Add(chargeUid);
        }

        foreach (var grid in _staleCharges)
            RemComp<CEAscentChargeComponent>(grid);

        // Tick launch telegraphs: keep the console countdown live, cancel if the
        // planet died under us, and fire the real ascent once the warning elapses.
        var curTime = Timing.CurTime;
        _endedWarnings.Clear();
        var warningQuery = EntityQueryEnumerator<CEAscentWarningComponent>();
        while (warningQuery.MoveNext(out var uid, out var warning))
        {
            if (TerminatingOrDeleted(uid))
                continue;

            if (TerminatingOrDeleted(warning.Planet))
            {
                _endedWarnings.Add(uid);
                continue;
            }

            // The console counts the telegraph down client-side off the networked
            // timestamps — no per-tick countdown traffic, same as the charge.
            if (warning.End > curTime)
                continue;

            _endedWarnings.Add(uid);
            _pendingAscents.Add((uid, warning.Planet));
        }

        // Strip the comps outside the query, and BEFORE the pending list runs below,
        // so the telegraph doesn't block its own TryStartAscent.
        foreach (var uid in _endedWarnings)
            RemComp<CEAscentWarningComponent>(uid);

        if (_pendingAscents.Count == 0)
            return;

        foreach (var (grid, planet) in _pendingAscents)
        {
            if (TerminatingOrDeleted(grid) || TerminatingOrDeleted(planet))
                continue;

            if (TryComp<MapGridComponent>(grid, out var mapGrid) &&
                TryComp<CEPlanetComponent>(planet, out var planetComp))
            {
                TryStartAscent((grid, mapGrid), (planet, planetComp));
            }
        }

        _pendingAscents.Clear();
    }

    /// <summary>
    /// An engine was shot out mid-telegraph: the launch aborts and the charged drive
    /// discharges violently, exactly like a descent charge abort (respool stun, pilot
    /// lock, everyone knocked flat) — except this discharge also arcs into the gravity
    /// generators of the whole docked set. With the gravgens stunned nothing holds the
    /// set aloft: it drops out of the sky, and the ground-layer crash explosion is
    /// eaten only by an active shieldgen (see CEZLevelsSystem.CrashGrid).
    /// </summary>
    public void AbortAscentWarning(EntityUid uid)
    {
        if (!HasComp<CEAscentWarningComponent>(uid))
            return;

        RemComp<CEAscentWarningComponent>(uid);

        if (TerminatingOrDeleted(uid))
            return;

        var now = Timing.CurTime;

        var stunned = EnsureComp<CEDescentStunnedComponent>(uid);
        stunned.Start = now;
        stunned.End = now + DriveRespoolTime;
        Dirty(uid, stunned);

        // Unlike a spinup abort there is no lock to adopt — climbing to breach never
        // locked the pilot — so take one for the respool unless someone else
        // (e.g. arrivals) already holds it.
        if (!HasComp<PreventPilotComponent>(uid))
        {
            AddComp<PreventPilotComponent>(uid);
            stunned.PilotLocked = true;
        }

        // The whole docked set loses lift together: gravgen stun on every member, so
        // the gravity sweep pools zero capacity for the falling body and the set
        // plummets (see CEZLevelsSystem.Gravity).
        var gridSet = new HashSet<EntityUid>();
        _shuttle.GetAllDockedShuttles(uid, gridSet);
        gridSet.Add(uid);

        foreach (var member in gridSet)
        {
            var gravStun = EnsureComp<CEZGravgenStunnedComponent>(member);
            gravStun.End = now + DriveRespoolTime;
        }

        // Everyone standing anywhere on the set gets thrown off their feet.
        var mobs = EntityQueryEnumerator<MobStateComponent, TransformComponent>();
        while (mobs.MoveNext(out var mobUid, out _, out var xform))
        {
            if (xform.GridUid is { } mobGrid && gridSet.Contains(mobGrid))
                _stun.TryParalyze(mobUid, DischargeStunTime, true);
        }
    }

    /// <summary>The planet whose z-stack <paramref name="level"/> belongs to, if any.</summary>
    private EntityUid? FindPlanetOfLevel(EntityUid level)
    {
        if (!TryComp<CEZMapComponent>(level, out var zMap))
            return null;

        var query = EntityQueryEnumerator<CEPlanetComponent>();
        while (query.MoveNext(out var uid, out var planet))
        {
            if (planet.Network == zMap.NetworkUid)
                return uid;
        }

        return null;
    }

    /// <summary>
    /// Starts the ascent sequence: <paramref name="grid"/>'s set leaves the top of
    /// <paramref name="planet"/>'s z-stack for a pseudo-map ride and warps out to the
    /// planet's space map. The same staged theatre as descent, run in reverse.
    /// Deliberately no shield check — shields gate entry, not exit.
    /// </summary>
    public bool TryStartAscent(Entity<MapGridComponent> grid, Entity<CEPlanetComponent> planet)
    {
        if (HasComp<CEDescentComponent>(grid) || HasComp<CEDescentSpinupComponent>(grid)
            || HasComp<CEAscentWarningComponent>(grid) || HasComp<CEDescentStunnedComponent>(grid))
            return false;

        // Only from real transit at the planet's ceiling. Two shapes qualify: the
        // open-sky gap over the top level (descent arrivals climbing back out live
        // there), or the gap capped BY the top level — TryEnterTransit never builds
        // a gap with nothing above it, so a genuine liftoff clamps here. In both the
        // ship sits at the top level's plane; either way, the level it's straining
        // against must be the true top of the stack.
        if (Transform(grid).MapUid is not { } transitMap ||
            !TryComp<CEZTransitMapComponent>(transitMap, out var transit) ||
            (transit.UpperMap ?? transit.LowerMap) is not { } topLevel ||
            _zLevels.TryMapUp(topLevel, out _))
        {
            return false;
        }

        // ... and it has to be THIS planet's stack.
        if (planet.Comp.Network is not { } networkUid ||
            !TryComp<CEZMapComponent>(topLevel, out var zMap) ||
            zMap.NetworkUid != networkUid)
        {
            return false;
        }

        // There has to be a space to come out into, and it must be a real space map —
        // not itself transit or somebody's pseudo-map.
        if (Transform(planet).MapUid is not { } spaceMap ||
            TerminatingOrDeleted(spaceMap) ||
            HasComp<CEZTransitMapComponent>(spaceMap) ||
            HasComp<CEDescentMapComponent>(spaceMap))
        {
            return false;
        }

        // The whole set on the transit map rides along — a transit map hosts exactly
        // one set, the same collection rule TryExitTransit uses.
        var gridSet = new HashSet<EntityUid>();
        var grids = EntityQueryEnumerator<MapGridComponent>();
        while (grids.MoveNext(out var uid, out _))
        {
            if (Transform(uid).MapUid == transitMap)
                gridSet.Add(uid);
        }

        var descent = AddComp<CEDescentComponent>(grid);
        descent.StageStart = Timing.CurTime;
        descent.Planet = planet.Owner;
        descent.Network = networkUid;
        descent.Ascent = true;
        descent.GridSet = gridSet;
        Dirty(grid, descent);

        foreach (var member in gridSet)
        {
            // Controls off for the theatre; the climb is scripted from here.
            _shuttle.Disable(member);

            // The z machinery hands the set over at rest: mirror TryExitTransit.
            if (TryComp<CEZPhysicsComponent>(member, out var zPhys))
            {
                _zLevels.SetZPosition((member, zPhys), 0f);
                _zLevels.SetZVelocity((member, zPhys), 0f);
            }

            if (TryComp<CEZGridFallerComponent>(member, out var faller))
                faller.Velocity = 0f;

            // Everyone below gets to watch the departure, not just PVS neighbours.
            _pvsOverride.AddGlobalOverride(member);
        }

        BeginAscending((grid.Owner, descent), topLevel, transitMap);
        return true;
    }

    /// <summary>
    /// Stage 1 entry, ascent flavour: build the pseudo-map and hop the set onto it.
    /// The "origin" is the top z-level whose sky the ship was straining against —
    /// bystanders there watch the hull shrink away upward while riders watch the world
    /// drop out from under them. The now-empty transit map is deleted, the same as any
    /// other transit exit. (The stage is still named Descending: it's "the ride".)
    /// </summary>
    private void BeginAscending(Entity<CEDescentComponent> ent, EntityUid topLevel, EntityUid transitMap)
    {
        ent.Comp.DescentMap = CreatePseudoMap(ent, topLevel, $"Ascent of {MetaData(ent).EntityName}");
        MoveGridSet(ent.Comp.GridSet, ent.Comp.DescentMap.Value);

        if (!TerminatingOrDeleted(transitMap))
            QueueDel(transitMap);

        SetStage(ent, CEDescentStage.Descending);
    }

    /// <summary>
    /// STAGE 2 of an ascent, "WARP!" — one tick, under the full whiteout. The set
    /// leaves the pseudo-map for the planet's space map, coming out at a random clear
    /// point inside the planet's zone: the reverse of the landing-disc scatter. From
    /// there the crew fades back in (Arriving) parked in space beside the planet,
    /// which is exactly the state a descent starts from.
    /// </summary>
    private void WarpAscent(Entity<CEDescentComponent> ent)
    {
        // The planet or its space died mid-sequence: put the set back on the sky
        // level it came from and let the z machinery sort it out.
        if (ent.Comp.Planet is not { } planetUid ||
            TerminatingOrDeleted(planetUid) ||
            Transform(planetUid).MapUid is not { } spaceMap ||
            TerminatingOrDeleted(spaceMap))
        {
            Abort(ent);
            return;
        }

        // Re-anchor on the pseudo-map first — there's nothing there to collide with —
        // then hop; the move preserves world position, so the set materializes
        // exactly on the picked spot.
        RecenterSetOn(ent.Comp.GridSet, ent.Owner, PickOrbitPoint(ent, planetUid, spaceMap));
        MoveGridSet(ent.Comp.GridSet, spaceMap);

        DeleteDescentMap(ent);
        SetStage(ent, CEDescentStage.Arriving);
    }

    /// <summary>
    /// Where the set comes out of the warp: a random point inside the planet's zone,
    /// uniform over the disc (the same sqrt trick as the landing scatter), re-rolled a
    /// few times if something is already parked there. When the attempts run out the
    /// last roll wins — in open space a bump beats a stuck sequence.
    /// </summary>
    private Vector2 PickOrbitPoint(Entity<CEDescentComponent> ent, EntityUid planetUid, EntityUid spaceMap)
    {
        var planetPos = _transform.GetWorldPosition(planetUid);

        if (!TryComp<CEPlanetComponent>(planetUid, out var planet) ||
            !TryComp<MapComponent>(spaceMap, out var map))
        {
            return planetPos;
        }

        // The set's extent around its lead, so the clearance check covers the whole
        // formation. Rotation is ignored; the margin soaks that up.
        var leadPos = _transform.GetWorldPosition(ent.Owner);
        var extent = Box2.CenteredAround(Vector2.Zero, Vector2.One);
        foreach (var member in ent.Comp.GridSet)
        {
            if (TerminatingOrDeleted(member) || !TryComp<MapGridComponent>(member, out var memberGrid))
                continue;

            extent = extent.Union(
                memberGrid.LocalAABB.Translated(_transform.GetWorldPosition(member) - leadPos));
        }

        extent = extent.Enlarged(2f);

        var target = planetPos;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var distance = planet.ZoneRadius * MathF.Sqrt(_random.NextFloat());
            target = planetPos + _random.NextAngle().ToVec() * distance;

            var clear = true;
            _clearanceGrids.Clear();
            _mapManager.FindGridsIntersecting(map.MapId, extent.Translated(target), ref _clearanceGrids);
            foreach (var other in _clearanceGrids)
            {
                if (!ent.Comp.GridSet.Contains(other.Owner))
                {
                    clear = false;
                    break;
                }
            }

            if (clear)
                break;
        }

        return target;
    }

    private List<Entity<MapGridComponent>> _clearanceGrids = new();
}
