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
public sealed class DEMState
{
    public DEMPhase CurrentPhase = DEMPhase.OFFLINE;

    public float AccretionDiskSaturation = 0.0f;
    public double AccretionDiskSpin = 0.0f;
    public double AccretionDiskTemperature = 0.0f;

    public float ShieldStress = 0.0f;
    public float ShieldIntegrity = 100.0f; // below 0 = OH NO

    public long CoreElectronFlux = 0;
    public long CoreMass = 10000;
    public float CoreSpin = 0.0f;
    public long CoreCharge = 0;

    public int LaserOffsetGoal = 0; // CLAMP: 0 - (manipulator rating * 4400) (Clamps occur to the LOWEST RATED LASER, in case you're about to ask)
    public int LaserOffset = 0; // millimeters
    public int LaserPowerGoal = 0; // CLAMP: 0 - (capacitor rating * 380)
    public int LaserPower = 0;
}
