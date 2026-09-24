using System.Numerics;
using Content.Shared.Actions;
using Content.Shared.Body.Part;
using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Mono.BidenBlast;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class BidenBlastComponent : Component
{
    [DataField] public EntProtoId Action = "ActionBidenBlast";
    [DataField] public EntityUid? ActionEntity;
    [DataField] public bool ContinueChargeSound;
    [DataField, AutoNetworkedField] public float Range = 30f;
    [DataField, AutoNetworkedField] public float Radius = 0.8f;
    [DataField, AutoNetworkedField] public TimeSpan ChargeTime = TimeSpan.FromSeconds(3);
    [DataField, AutoNetworkedField] public TimeSpan FireTime = TimeSpan.FromSeconds(5);
    [DataField] public float TremorRadius = 25f;
    [DataField] public float ShakeAmplitude = 1.5f;
    [DataField] public float FlashRadius = 12f;
    [DataField] public float FlashDuration = 3f;
    [DataField] public SoundSpecifier ChargeSound = new SoundPathSpecifier("/Audio/_Mono/Items/BidenBlast/lasercharge.ogg");
    [DataField] public SoundSpecifier FireSound = new SoundPathSpecifier("/Audio/_Mono/Items/BidenBlast/shieldgenfire.ogg");
    [DataField] public DamageSpecifier Damage = new() { DamageDict = new() { ["Heat"] = 200, ["Structural"] = 1000 } };
    [DataField] public float PierceFalloff = 0.75f;
    [DataField] public float StructuralPierceFalloff = 0.5f;
    [DataField] public DamageSpecifier ArmDamage = new() { DamageDict = new() { ["Heat"] = 300, ["Blunt"] = 300 } };
    [DataField] public List<BidenBlastLimbEffect> LimbEffects = new()
    {

    };
}

[DataDefinition]
public sealed partial class BidenBlastLimbEffect
{
    [DataField(required: true)] public BodyPartType Part;
    [DataField] public BodyPartSymmetry? Symmetry;
    [DataField] public bool Vaporize;
    [DataField] public DamageSpecifier? Damage;
}

public sealed partial class BidenBlastActionEvent : WorldTargetActionEvent;

[Serializable, NetSerializable]
public sealed class BidenBlastAimEvent(Vector2 position) : EntityEventArgs
{
    public Vector2 Position = position;
}
