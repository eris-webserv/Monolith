using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._ErisWS.DEM;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DEMMachineComponent : Component
{
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public bool Constructed;

    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public bool ScrubberBreached;

    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public EntityUid? Console;

    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public EntityUid? Origin;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DEMAssemblyComponent : Component
{
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public EntityUid? Control;

    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public EntityUid? Console;
}

[RegisterComponent, NetworkedComponent]
public sealed partial class EternalDamnationMapComponent : Component;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class EternalDamnationComponent : Component
{
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public EntityUid Source;

    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public EntityCoordinates ReturnCoordinates;
}

[Serializable, NetSerializable]
public enum DEMMachinePart : byte
{
    Scrubber0,
    Scrubber1,
    Scrubber2,
    Scrubber3,
    Scrubber4,
    Scrubber5,
    Scrubber6,
    Scrubber7,
    Scrubber8,
    Scrubber9,
    Scrubber10,
    Scrubber11,
    Scrubber12,
    Scrubber13,
    Scrubber14,
    Scrubber15,
    Laser0,
    Laser1,
    Laser2,
    Laser3
}

[ByRefEvent]
public record struct DEMScrubberBreachEvent(EntityUid Section);

[ByRefEvent]
public record struct DEMConsumeEntityEvent(EntityUid Target)
{
    public bool Handled;
}

[Serializable, NetSerializable]
public sealed class DEMFormationPreviewEvent(DEMFormationPreviewPart[] parts) : EntityEventArgs
{
    public DEMFormationPreviewPart[] Parts { get; } = parts;
}

[Serializable, NetSerializable]
public readonly struct DEMFormationPreviewPart(string prototype, MapCoordinates coordinates, Angle rotation)
{
    public readonly string Prototype = prototype;
    public readonly MapCoordinates Coordinates = coordinates;
    public readonly Angle Rotation = rotation;
}
