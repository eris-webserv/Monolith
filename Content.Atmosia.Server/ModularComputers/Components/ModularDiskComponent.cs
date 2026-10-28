using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Utility;

namespace Content.Atmosia.Server.ModularComputers.Components;

[RegisterComponent]
public sealed partial class ModularDiskComponent : Component
{
    [DataField(required: true)]
    public ResPath Image;
}
