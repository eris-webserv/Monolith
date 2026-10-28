using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Atmosia.Server.ModularComputers.Components;

[RegisterComponent]
public sealed partial class ModularServerComponent : Component
{
    [DataField]
    public string MotherboardSlot = "motherboard";

    [DataField]
    public List<string> DiskSlots = ["disk_0", "disk_1"];
}
