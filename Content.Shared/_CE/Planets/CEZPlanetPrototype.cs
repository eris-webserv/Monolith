/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._DV.Planet;
using Robust.Shared.Prototypes;

namespace Content.Shared._CE.Planets;

/// <summary>
/// A fully runtime-generated planet z-stack: the ground layer (depth 0) is biome-generated from
/// <see cref="Planet"/>, and the remaining <see cref="Layers"/> are empty sky maps created above
/// it, with the clouds layer at depth <see cref="CloudsIndex"/>. <see cref="NetworkComponents"/>
/// are shared by every map in the z-network, including transit maps created at runtime.
/// Used by the <c>cezspawnplanet</c> toolshed command.
/// </summary>
[Prototype("cezPlanet")]
public sealed partial class CEZPlanetPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// The biome planet prototype used to generate the ground layer.
    /// </summary>
    [DataField(required: true)]
    public ProtoId<PlanetPrototype> Planet;

    /// <summary>
    /// Total number of z-levels, including the ground layer.
    /// </summary>
    [DataField]
    public int Layers = 5;

    /// <summary>
    /// Depth of the clouds layer (ground is 0). Out of range means no clouds layer.
    /// </summary>
    [DataField]
    public int CloudsIndex = 2;

    /// <summary>
    /// Shared components applied to every map in the z-network, including runtime transit maps.
    /// </summary>
    [DataField]
    public ComponentRegistry NetworkComponents = new();
}
