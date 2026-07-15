using Content.Shared._Crescent.ShipShields;
using Content.Server.Power.Components;
using Content.Shared.Projectiles;
using Robust.Shared.Physics.Components;
using Content.Server.Emp;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Station.Systems;
using Robust.Shared.Audio.Systems;
using Content.Shared.Examine;
using Content.Server.Explosion.Components;
using Content.Shared.Explosion;
using Content.Shared.Explosion.Components;
using Content.Shared._CE.Camera;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.ShipShields;

public partial class ShipShieldsSystem
{
    private const float MAX_EMP_DAMAGE = 10000f;
    [Dependency] private TriggerSystem _trigger = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    public void InitializeEmitters()
    {
        SubscribeLocalEvent<ShipShieldEmitterComponent, ShieldDeflectedEvent>(OnShieldDeflected);
        SubscribeLocalEvent<ShipShieldEmitterComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<ShipShieldEmitterComponent, ComponentRemove>(OnRemoved);
    }


    private void OnRemoved(Entity<ShipShieldEmitterComponent> owner, ref ComponentRemove remove)
    {
        var parent = Transform(owner.Owner).GridUid;
        if (parent is null)
            return;
        UnshieldEntity(parent.Value, null);
    }

    private void OnShieldDeflected(EntityUid uid, ShipShieldEmitterComponent component, ShieldDeflectedEvent args)
    {
        if (TryComp<EmpOnTriggerComponent>(args.Deflected, out var emp))
        {
            component.Damage += Math.Clamp(emp.EnergyConsumption, 0f, MAX_EMP_DAMAGE);
            _trigger.Trigger(args.Deflected);
        }

        if (TryComp<ExplosiveComponent>(args.Deflected, out var exp) && _prototypeManager.TryIndex(exp.ExplosionType, out var type))
        {
            component.Damage += exp.TotalIntensity * (float)type.DamagePerIntensity.GetTotal();
        }

        component.Damage += (float)args.Projectile.Damage.GetTotal();
        args.Projectile.ProjectileSpent = true;

        QueueDel(args.Deflected);
    }

    /// <summary>
    /// pzn: lets an active ship shield soak a ground-crash impact the same way it soaks
    /// projectiles — the blast intensity converts to emitter damage through the default
    /// explosion prototype, mirroring the <see cref="ExplosiveComponent"/> path above.
    /// Returns true when a shield was up: the hull is protected either way and the
    /// caller should skip its crash explosions. If the hit lands past
    /// <see cref="ShipShieldEmitterComponent.DamageLimit"/> the shield still blocks
    /// this crash, but drops with <see cref="ShipShieldEmitterComponent.CrashShutdownSound"/>
    /// and respools for <see cref="ShipShieldEmitterComponent.CrashRespoolTime"/> seconds.
    /// </summary>
    public bool TryAbsorbCrash(EntityUid grid, float totalIntensity)
    {
        if (!TryComp<ShipShieldedComponent>(grid, out var shielded)
            || shielded.Source is not { } source
            || !TryComp<ShipShieldEmitterComponent>(source, out var emitter)
            || emitter.Shield is null)
            return false;

        var damage = totalIntensity;
        if (_prototypeManager.TryIndex<ExplosionPrototype>(ExplosionSystem.DefaultExplosionPrototypeId, out var proto))
            damage *= (float)proto.DamagePerIntensity.GetTotal();

        emitter.Damage += damage;
        AdjustEmitterLoad(source, emitter);

        if (emitter.Damage <= emitter.DamageLimit)
            return true;

        // Too much for the emitter: this crash is still blocked, but the shield
        // drops immediately and respools. Nulling Shield/Shielded here keeps the
        // Update loop from also playing the generic power-down sound.
        emitter.OverloadAccumulator = MathF.Max(emitter.OverloadAccumulator, emitter.CrashRespoolTime);
        emitter.CrashRespooling = true;

        // pzn: GetInOwningStation is empty for free-flying shuttles (they aren't
        // stations), which is why the shutdown was silent — address everyone on
        // the grid directly instead.
        var filter = Filter.BroadcastGrid(grid);

        if (emitter.CrashShutdownSound is { } sound)
            _audio.PlayGlobal(sound, filter, true, sound.Params);

        // Rattle everyone aboard so the drop reads even over the crash noise:
        // the sustained radial shake (same feel as a thruster blowing), driven
        // client-side by RadialShakeSystem off this event.
        RaiseNetworkEvent(new RadialShakeEvent
        {
            Duration = 1.5f,
            Amplitude = Math.Clamp(totalIntensity / 1500f, 0.6f, 1.2f),
        }, filter);

        UnshieldEntity(grid);
        emitter.Shield = null;
        emitter.Shielded = null;

        return true;
    }

    private void OnExamined(EntityUid uid, ShipShieldEmitterComponent component, ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        args.PushMarkup(Loc.GetString("shield-emitter-examine", ("basedraw", component.BaseDraw), ("additional", CalculateLoadDamage(component))));

        if (component.CrashRespooling && component.OverloadAccumulator > 0f)
            args.PushMarkup(Loc.GetString("shield-emitter-examine-crash-respool", ("seconds", MathF.Ceiling(component.OverloadAccumulator))));
    }

    private static float CalculateLoadDamage(ShipShieldEmitterComponent emitter)
    {
        return (float)Math.Clamp(Math.Pow(emitter.Damage, emitter.DamageExp) * emitter.PowerModifier, 0f, emitter.MaxDraw);
    }

    private void AdjustEmitterLoad(EntityUid uid, ShipShieldEmitterComponent? emitter = null, ApcPowerReceiverComponent? receiver = null)
    {
        if (!Resolve(uid, ref emitter, ref receiver))
            return;

        receiver.Load = emitter.BaseDraw + CalculateLoadDamage(emitter);
    }
}
