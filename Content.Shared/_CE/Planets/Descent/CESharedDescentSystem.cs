/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._CE.ZLevels.Core.EntitySystems;
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

    public static TimeSpan StageDuration(CEDescentStage stage)
    {
        return stage switch
        {
            CEDescentStage.Descending => DescendTime,
            CEDescentStage.Vanishing => VanishTime,
            CEDescentStage.Arriving => ArriveTime,
            _ => TimeSpan.Zero,
        };
    }

    /// <summary>Fraction of <paramref name="stage"/> elapsed since <paramref name="stageStart"/>, clamped.</summary>
    public float GetStageProgress(CEDescentStage stage, TimeSpan stageStart)
    {
        var duration = StageDuration(stage);
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
                var p = GetStageProgress(CEDescentStage.Descending, map.StageStart);
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
