/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._CE.Planets;
using Content.Shared._CE.Planets.Shields;
using Content.Shared._CE.ZLevels.Core.Components;
using Content.Shared._CE.ZLevels.Core.EntitySystems;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Shared._CE.Planets.Descent;

/// <summary>
/// Shared half of the planet descent sequence: stage timings and the visual progress
/// curves. The server state machine advances stages on these clocks; the client renderer
/// (pass builder zoom/shrink, planet overlay swell) reads the same curves off the
/// networked stage + start time, so both sides always agree without per-tick traffic.
/// </summary>
public abstract partial class CESharedDescentSystem : EntitySystem
{
    [Dependency] protected IGameTiming Timing = default!;
    [Dependency] private SharedTransformSystem _xform = default!;

    // Stage 2 (the warp) is the Vanishing → Arriving
    // transition itself: one tick, no duration entry. There is no chargeup entry
    // either: spinup theatre is the shuttle console's business, and the sequence
    // starts already falling.
    //
    // The bystander fadeout is NOT the Vanishing stage: it starts midway through
    // Descending and completes with it (see ScalingViewport.CEZLevels.cs), so the
    // whole observable drop lives on the one Descending clock and a late-replicating
    // stage flip can't stall it. Vanishing is just the rider's whiteout cover and
    // the server's grace period for the warp + pseudo-map teardown, so it can be
    // short; Descending carries the visual and gets the bulk of the time.
    public static readonly TimeSpan DescendTime = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan VanishTime = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ArriveTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The ascent leg's Descending stage (the world falling away below a climbing ship)
    /// runs much longer than the 4s descent drop: a launch reads as sustained engine
    /// strain, not a bomb drop in reverse, and the ground receding is the whole show.
    /// Vanish/arrive covers are shared with descent.
    /// </summary>
    public static readonly TimeSpan AscendTime = TimeSpan.FromSeconds(10);

    /// <summary>How long the console spinup theatre runs before the descent proper begins.</summary>
    public static readonly TimeSpan SpinupTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the drive respools after a mid-charge discharge (an engine damaged
    /// during spinup) before another descent can be requested.
    /// </summary>
    public static readonly TimeSpan DriveRespoolTime = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the discharge itself lasts: everyone aboard is paralyzed for this long,
    /// and the client's radial screenshake tapers off over the same window.
    /// </summary>
    public static readonly TimeSpan DischargeStunTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A pilot confirmed a descent on the shuttle console. Runs on both sides of the
    /// predicted <see cref="CEDescentRequestMessage"/> (each side subscribes with its
    /// own console component type), so the spinup ring and any refusal popup land the
    /// instant the pilot clicks instead of a server round-trip later.
    /// </summary>
    protected void OnDescentRequest(EntityUid uid, CEDescentRequestMessage args)
    {
        if (Transform(uid).GridUid is not { } gridUid)
            return;

        if (!TryBeginDescent(gridUid, GetEntity(args.Planet), out var denyReason) && denyReason != null)
            DescentDenied(uid, denyReason);
    }

    /// <summary>
    /// A console refused a descent request. The server override makes the console
    /// speak <paramref name="denyReason"/> out loud in local IC chat — the console
    /// says why, like a person would. The client does nothing here (chat is
    /// server-authoritative), so the predicted run stays silent.
    /// </summary>
    protected virtual void DescentDenied(EntityUid console, string denyReason)
    {
    }

    /// <summary>
    /// Validates a descent request and starts the console spinup theatre on
    /// <paramref name="gridUid"/> toward <paramref name="planetUid"/>: the grid gets a
    /// <see cref="CEDescentSpinupComponent"/> and the server hands over to the descent
    /// proper when its clock elapses. Shared so the validation (and the spinup state the
    /// console UI counts down from) lives in one place; only networked components are
    /// touched.
    /// </summary>
    /// <param name="denyReason">
    /// Loc id explaining a player-facing refusal (wrong map, out of range, no landable network), or
    /// null when the request was silently ignored (already descending, bad arguments).
    /// </param>
    public bool TryBeginDescent(EntityUid gridUid, EntityUid planetUid, out string? denyReason)
    {
        denyReason = null;

        if (!HasComp<MapGridComponent>(gridUid))
            return false;

        // Already spinning up or mid-descent — ignore.
        if (HasComp<CEDescentSpinupComponent>(gridUid) || HasComp<CEDescentComponent>(gridUid))
            return false;

        // Drive discharged mid-charge: it has to respool before another attempt.
        if (HasComp<CEDescentStunnedComponent>(gridUid))
        {
            denyReason = "ce-descent-request-respooling";
            return false;
        }

        if (!TryComp<CEPlanetComponent>(planetUid, out var planet))
            return false;

        // Must share a map with the planet; no descents from mid-gap or pseudo-maps.
        if (Transform(gridUid).MapUid is not { } mapUid
            || Transform(planetUid).MapUid != mapUid
            || HasComp<CEZTransitMapComponent>(mapUid)
            || HasComp<CEDescentMapComponent>(mapUid))
        {
            denyReason = "ce-descent-request-denied";
            return false;
        }

        // Must be parked inside the planet's zone band — the same radius the nav
        // screen draws — so no cross-map descent sniping from a distant blip.
        if ((_xform.GetWorldPosition(gridUid) - _xform.GetWorldPosition(planetUid)).Length() > planet.ZoneRadius)
        {
            denyReason = "ce-descent-request-out-of-range";
            return false;
        }

        if (planet.Network is not { } networkUid || !HasComp<CEZMapNetworkComponent>(networkUid))
        {
            denyReason = "ce-descent-request-no-network";
            return false;
        }

        // An active planetary shield blocks conventional entry outright. Networked flag,
        // so the client predicts the same refusal the moment the pilot clicks.
        if (TryComp<CEPlanetShieldComponent>(planetUid, out var shield) && shield.Active)
        {
            denyReason = "ce-descent-request-shielded";
            return false;
        }

        var spinup = AddComp<CEDescentSpinupComponent>(gridUid);
        spinup.Planet = planetUid;
        spinup.Start = Timing.CurTime;
        spinup.End = Timing.CurTime + SpinupTime;
        Dirty(gridUid, spinup);
        return true;
    }

    public static TimeSpan StageDuration(CEDescentStage stage, bool ascent = false)
    {
        return stage switch
        {
            CEDescentStage.Descending => ascent ? AscendTime : DescendTime,
            CEDescentStage.Vanishing => VanishTime,
            CEDescentStage.Arriving => ArriveTime,
            _ => TimeSpan.Zero,
        };
    }

    /// <summary>Fraction of <paramref name="stage"/> elapsed since <paramref name="stageStart"/>, clamped.</summary>
    public float GetStageProgress(CEDescentStage stage, TimeSpan stageStart, bool ascent = false)
    {
        var duration = StageDuration(stage, ascent);
        if (duration <= TimeSpan.Zero)
            return 1f;

        return Math.Clamp((float) ((Timing.CurTime - stageStart) / duration), 0f, 1f);
    }

    /// <summary>
    /// How far the descending ship has visually sunk below its origin plane, in z-levels:
    /// 0 at Stage 1 start, easing (smoothstep) to 1 by its end, held there through the
    /// vanish fade. This is the synthetic pass depth for bystanders (negated) and the
    /// zoom/swell driver for riders.
    /// </summary>
    public float GetDescentDepth(CEDescentMapComponent map)
    {
        switch (map.Stage)
        {
            case CEDescentStage.Descending:
                var p = GetStageProgress(CEDescentStage.Descending, map.StageStart, map.Ascent);
                return p * p * (3f - 2f * p); // smoothstep: no pop at either end
            case CEDescentStage.Vanishing:
            case CEDescentStage.Arriving:
                return 1f;
            default:
                return 0f;
        }
    }

    /// <summary>
    /// How small the hull ends up on bystanders' screens by the end of the drop,
    /// relative to its parked size. One z-level of shrink
    /// (<see cref="CESharedZLevelsSystem.ZLevelViewShrink"/>) is nowhere near enough
    /// for a ship dropping onto a planet — it keeps plunging through its own fadeout
    /// until it's a pixel-sized speck.
    /// </summary>
    public const float SpeckScale = 0.01f;
}
