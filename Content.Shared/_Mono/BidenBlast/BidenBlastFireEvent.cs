using Robust.Shared.Serialization;

namespace Content.Shared._Mono.BidenBlast;

[Serializable, NetSerializable]
public sealed class BidenBlastFireEvent(NetEntity shooter, TimeSpan endsAt, float flashDuration, float shakeAmplitude) : EntityEventArgs
{
    public NetEntity Shooter = shooter;
    public TimeSpan EndsAt = endsAt;
    public float FlashDuration = flashDuration;
    public float ShakeAmplitude = shakeAmplitude;
}
