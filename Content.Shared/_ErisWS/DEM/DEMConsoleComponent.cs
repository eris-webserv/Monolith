using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._ErisWS.DEM;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DEMConsoleComponent : Component
{
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public EntityUid? Machine;
}

[Serializable, NetSerializable]
public enum DEMConsoleUiKey : byte
{
    Key
}
