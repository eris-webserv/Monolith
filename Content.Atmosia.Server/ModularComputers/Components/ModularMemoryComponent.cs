using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Atmosia.Server.ModularComputers.Components;

[RegisterComponent]
public sealed partial class ModularMemoryComponent : Component
{
    [DataField(required: true)]
    public uint Capacity;
}
