using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Atmosia.Server.ModularComputers.Components;

[RegisterComponent]
public sealed partial class ModularMotherboardComponent : Component
{
    [DataField]
    public string CpuSlot = "cpu";

    [DataField]
    public List<string> MemorySlots = ["memory_0", "memory_1"];
}
