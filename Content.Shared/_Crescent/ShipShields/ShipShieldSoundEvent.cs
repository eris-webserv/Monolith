using Content.Shared.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.ShipShields;

/// <summary>
/// pzn: sounds played by the shield emitter; we literally just send this so you can turn it down a bit (they are LOUD)
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipShieldSoundEvent : GlobalSoundEvent
{
    public ShipShieldSoundEvent(ResolvedSoundSpecifier specifier, AudioParams? audioParams = null)
        : base(specifier, audioParams)
    {
    }
}
