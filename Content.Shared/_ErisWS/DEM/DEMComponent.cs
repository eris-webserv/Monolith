using Content.Shared._ErisWS.DEM;
using Robust.Shared.GameStates;

/// <summary>
/// Gordon, must I remind you what you're doing?
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DEMComponent : Component
{
    [AutoNetworkedField]
    public DEMState State = new();
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DEMScrubberPartComponent : Component
{
    [AutoNetworkedField]
    public float Integrity = 100.0f;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DEMLaserComponent : Component
{
    [AutoNetworkedField]
    public float Integrity = 100.0f;
}
