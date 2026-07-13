/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Robust.Shared.GameStates;
using Robust.Shared.Utility;

namespace Content.Shared._CE.Planets.Descent;

/// <summary>
/// A descent pseudo-map: the transient, bare map that carries a ship while it descends
/// toward a planet (Stage 1–1.5 of the sequence). Deliberately OUTSIDE the z machinery —
/// no <c>CEZTransitMapComponent</c>, no network membership — so the client pass builder
/// degrades it to a single own-map pass with parallax on.
///
/// Everything the client renderer needs lives here, on the map entity (always networked),
/// so no PVS gymnastics are required for the planet or the origin map.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CEDescentMapComponent : Component
{
    /// <summary>
    /// The map the ship departed from. Bystanders on this map render the pseudo-map as a
    /// synthetic below-pass — the ship shrinking away beneath them.
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public EntityUid? OriginMap;

    /// <summary>The lead grid riding this pseudo-map.</summary>
    [ViewVariables, AutoNetworkedField]
    public EntityUid? Grid;

    /// <summary>Mirror of the lead grid's <see cref="CEDescentComponent.Stage"/>.</summary>
    [ViewVariables, AutoNetworkedField]
    public CEDescentStage Stage = CEDescentStage.Descending;

    /// <summary>Server curtime when <see cref="Stage"/> was entered.</summary>
    [ViewVariables, AutoNetworkedField]
    public TimeSpan StageStart;

    /// <summary>
    /// Snapshot of the target planet's sky sprite, copied at descent start. A snapshot
    /// rather than an entity reference: the live planet entity sits on the origin map and
    /// may leave the riders' PVS once the ship warps off it. Null = no planet visual
    /// (network-only descent).
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public SpriteSpecifier? PlanetSprite;

    /// <summary>Snapshot of the planet's visual spin rate (rad/s).</summary>
    [ViewVariables, AutoNetworkedField]
    public float PlanetSpinRate;

    /// <summary>
    /// Snapshot of the planet's <c>MaxScale</c>. Descent can only start inside the
    /// planet's zone, where it already renders screen-centred at MaxScale — so drawing at
    /// this scale on the pseudo-map is seamless across the map swap.
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public float PlanetScale = 2f;
}
