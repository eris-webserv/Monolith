using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Utility;

namespace Content.Atmosia.Server.ModularComputers.Components;

[RegisterComponent]
public sealed partial class ModularCpuComponent : Component
{
    [DataField(required: true)]
    public uint ClockRate;

    [DataField(required: true)]
    public ResPath BootRom;
}
