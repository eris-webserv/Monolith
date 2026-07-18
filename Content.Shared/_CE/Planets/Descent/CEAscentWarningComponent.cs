/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Robust.Shared.GameStates;

namespace Content.Shared._CE.Planets.Descent;

/// <summary>
/// The launch telegraph between a completed breach spool and the actual ascent: the
/// engines are charged and the ship hangs at the planet's ceiling while the drive
/// winds up for the jump to orbit. Lives on the lead grid. Networked so every nav
/// screen in range — the crew's and any would-be interdictor's — can render the
/// warning ring around the ship itself, the same ring a descent spinup draws around
/// its target planet.
///
/// This is the interdiction window: an engine shot during the telegraph aborts the
/// launch violently (see CEDescentSystem.AbortAscentWarning) — the drive discharge
/// stuns the thrusters AND arcs into the gravity generators, dropping the ship out
/// of the sky.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CEAscentWarningComponent : Component
{
    /// <summary>The planet the ship is about to leave.</summary>
    [ViewVariables, AutoNetworkedField]
    public EntityUid Planet;

    /// <summary>Server curtime when the telegraph began (ring progress runs from here).</summary>
    [ViewVariables, AutoNetworkedField]
    public TimeSpan Start;

    /// <summary>Server curtime when the ascent actually starts.</summary>
    [ViewVariables, AutoNetworkedField]
    public TimeSpan End;
}
