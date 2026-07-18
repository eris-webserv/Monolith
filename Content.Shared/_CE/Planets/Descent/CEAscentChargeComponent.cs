using Robust.Shared.GameStates;

namespace Content.Shared._CE.Planets.Descent;

/// <summary>
/// The breach charge: a pilot is holding the climb against the open sky at the top of a
/// planet's z-stack and the drive is charging for the jump to orbit. Lives on the lead
/// grid from the first clamped push until either the charge completes (handing over to
/// <see cref="CEAscentWarningComponent"/>) or the pilot lets go. Networked so the shuttle
/// console counts the charge down client-side off the timestamps, exactly like a descent
/// spinup — no per-tick countdown traffic.
///
/// This is NOT the interdiction window: engines shot during the charge don't discharge
/// anything — the drive is still winding up, and losing thrust already means losing the
/// climb. Only the launch telegraph that follows is abortable
/// (see CEDescentSystem.AbortAscentWarning).
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CEAscentChargeComponent : Component
{
    /// <summary>The planet whose sky the ship is straining against.</summary>
    [ViewVariables, AutoNetworkedField]
    public EntityUid Planet;

    /// <summary>Server curtime when the charge began.</summary>
    [ViewVariables, AutoNetworkedField]
    public TimeSpan Start;

    /// <summary>Server curtime when the charge completes and the launch telegraph begins.</summary>
    [ViewVariables, AutoNetworkedField]
    public TimeSpan End;

    /// <summary>
    /// Server curtime of the last climb push that fed this charge. The climb event stops
    /// arriving the moment input drops, so a stale push expires the charge from Update.
    /// Server bookkeeping only.
    /// </summary>
    [ViewVariables]
    public TimeSpan LastPush;
}
