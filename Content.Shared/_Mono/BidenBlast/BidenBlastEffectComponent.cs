using System.Numerics;
using Robust.Shared.GameStates;

namespace Content.Shared._Mono.BidenBlast;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class BidenBlastEffectComponent : Component
{
    [DataField, AutoNetworkedField] public NetEntity Shooter;
    [DataField, AutoNetworkedField] public Vector2 Direction;
    [DataField, AutoNetworkedField] public float Range = 30f;
    [DataField, AutoNetworkedField] public float Radius = 0.8f;
    [DataField, AutoNetworkedField] public bool Firing;
    [DataField, AutoNetworkedField] public TimeSpan StartedAt;
    [DataField, AutoNetworkedField] public TimeSpan FiresAt;
    [DataField, AutoNetworkedField] public TimeSpan EndsAt;
    public BidenBlastComponent? Settings;
    public readonly List<EntityUid> VaporizeParts = new();
    public EntityUid? ChargeStream;
    public float ChargeGain;
    public EntityUid? FireStream;
    public float FireGain;
    public TimeSpan NextDamage;
    public TimeSpan NextAim;
}
