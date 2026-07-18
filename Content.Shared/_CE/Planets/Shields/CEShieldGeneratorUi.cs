using Robust.Shared.Serialization;

namespace Content.Shared._CE.Planets.Shields;

/// <seealso cref="CEShieldGeneratorComponent"/>
[Serializable, NetSerializable]
public enum CEShieldGeneratorUiKey : byte
{
    Key,
}

/// <summary>
/// Server → client snapshot for the generator status window. Read-only: the generator
/// has no power switch, it just runs — the window only reports where the cycle is.
/// </summary>
[Serializable, NetSerializable]
public sealed class CEShieldGeneratorBuiState : BoundUserInterfaceState
{
    public CEShieldGeneratorStage Stage;

    /// <summary>Joules banked / buffer size.</summary>
    public float Charge;
    public float MaxCharge;

    /// <summary>Watts actually arriving vs. watts currently wanted from the grid.</summary>
    public float ReceivedPower;
    public float WantedPower;

    /// <summary>
    /// Seconds until the next lifecycle event (buffer full / beam fire / field collapse /
    /// cooldown end), or negative when nothing is pending.
    /// </summary>
    public float EtaSeconds = -1f;

    /// <summary>
    /// Whether the generator is standing on a map that belongs to a planet's z-network.
    /// False means the whole cycle is locked out: no draw, no charging, no beam.
    /// </summary>
    public bool OnPlanet = true;
}
