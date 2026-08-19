using Robust.Shared.GameStates;

namespace Content.Shared._ErisWS.DEM;

/// <summary>
/// Gordon, must I remind you what you're doing?
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DEMComponent : Component
{
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public DEMState State = new();

    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public float IngestionRadius = 4.8f;

    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public float ConsumptionRadius = 0.55f;

    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public float PullSpeed = 0.7f;

    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public float MaxOrbitSpeed = 8.0f;

    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public float DiskSaturationPerSecond = 0.25f;

    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public float HeatDamagePerSecond = 8.0f;

    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public float BluntDamagePerSecond = 5.0f;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DEMScrubberPartComponent : Component
{
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public float Integrity = 100.0f;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DEMLaserComponent : Component
{
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public float Integrity = 100.0f;
}
