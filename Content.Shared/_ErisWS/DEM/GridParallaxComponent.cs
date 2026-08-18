using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._ErisWS.DEM;

/// <summary>
/// Adds a distant macrostate relay to a map grid.
/// </summary>
[RegisterComponent]
public sealed partial class GridParallaxComponent : Component
{
    [DataField]
    public EntProtoId RelayPrototype = "GridParallaxRelay";

    [ViewVariables]
    public EntityUid? Relay;
}

/// <summary>
/// A sprite relay rendered in place of a map grid at great distance.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class GridParallaxRelayComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool Enabled = true;

    [DataField, AutoNetworkedField]
    public float NearDistance = 50f;

    [DataField, AutoNetworkedField]
    public float FarDistance = 2000f;

    [DataField, AutoNetworkedField]
    public float Tightness = 1f;

    [DataField, AutoNetworkedField]
    public float NearScale = 8f;

    [DataField, AutoNetworkedField]
    public float FarScale = 0.2f;

    [DataField, AutoNetworkedField]
    public float MinDistance = 1f;
}
