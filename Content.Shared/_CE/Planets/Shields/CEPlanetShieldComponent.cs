using Robust.Shared.GameStates;

namespace Content.Shared._CE.Planets.Shields;

/// <summary>
/// A planetary energy shield over a <see cref="CEPlanetComponent"/> entity. While
/// <see cref="Active"/>, descent requests onto the planet are refused at the shuttle
/// console (the field blocks entry; alternate landing methods required). Lives on the
/// planet entity itself and is networked so the client's predicted console validation
/// and the sky-side shield visuals both read the same flag.
/// </summary>
/// <remarks>
/// This is only the state. What raises and lowers it (the ground-side shield generator,
/// its charge/spinup cycle, the EMP detonation) is the generator system's business —
/// anything server-side may toggle it through <c>CEPlanetShieldSystem.SetShieldActive</c>.
/// </remarks>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CEPlanetShieldComponent : Component
{
    /// <summary>
    /// Whether the field is currently up and blocking descents. This is the only state
    /// that networks: the client formation/collapse visual animates locally off observed
    /// flips of this flag (see the shield overlay), so the effect is ping-independent.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Active;
}
