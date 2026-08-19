using Robust.Shared.Serialization;

namespace Content.Shared._ErisWS.DEM;

[Serializable, NetSerializable]

public enum DEMPhase
{
    OFFLINE,
    STARTING,
    ONLINE,
    MELTDOWN_P1,
    MELTDOWN_P2
}

[Serializable, NetSerializable]
[DataDefinition]
public sealed partial class DEMState
{
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public DEMPhase CurrentPhase = DEMPhase.OFFLINE;

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public bool CoreVisible;

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float AccretionDiskSaturation = 0.0f;

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public double AccretionDiskSpin = 0.0f;

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public double AccretionDiskTemperature = 0.0f;

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float ShieldStress = 0.0f;

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float ShieldIntegrity = 100.0f; // below 0 = OH NO

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public long CoreElectronFlux = 0;

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public long CoreMass = 10000;

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float CoreSpin = 0.0f;

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public long CoreCharge = 0;

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public int LaserOffsetGoal = 0; // CLAMP: 0 - (manipulator rating * 4400) (Clamps occur to the LOWEST RATED LASER, in case you're about to ask)

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public int LaserOffset = 0; // millimeters

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public int LaserPowerGoal = 0; // CLAMP: 0 - (capacitor rating * 380)

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public int LaserPower = 0;
}
