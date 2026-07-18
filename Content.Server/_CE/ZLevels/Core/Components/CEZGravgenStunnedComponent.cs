/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

namespace Content.Server._CE.ZLevels.Core.Components;

/// <summary>
/// The grid's gravity generators are stunned: an aborted launch telegraph discharged
/// the descent drive through them (see CEDescentSystem.AbortAscentWarning). While
/// present the gravity sweep in CEZLevelsSystem.Gravity ignores every gravgen parented
/// to this grid when pooling lift capacity, so an airborne set carrying only stunned
/// generators falls — all the way to a ground-layer crash if nothing catches it.
/// The sweep removes the component once <see cref="End"/> passes.
/// </summary>
[RegisterComponent]
public sealed partial class CEZGravgenStunnedComponent : Component
{
    /// <summary>Server curtime when the generators come back online.</summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan End;
}
