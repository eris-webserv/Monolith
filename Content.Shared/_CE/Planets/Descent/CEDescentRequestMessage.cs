using Robust.Shared.Serialization;

namespace Content.Shared._CE.Planets.Descent;

/// <summary>
/// Sent by the shuttle console BUI when the pilot confirms a descent onto a planet.
/// The server validates and runs the spinup theatre before handing over to the
/// descent sequence proper.
/// </summary>
[Serializable, NetSerializable]
public sealed class CEDescentRequestMessage : BoundUserInterfaceMessage
{
    public NetEntity Planet;

    public CEDescentRequestMessage(NetEntity planet)
    {
        Planet = planet;
    }
}
