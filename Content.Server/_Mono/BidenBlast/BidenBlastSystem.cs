using System.Numerics;
using Content.Shared._Mono.BidenBlast;
using Content.Shared.Mobs.Systems;
using Content.Shared.Actions;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Mono.BidenBlast;

public sealed partial class BidenBlastSystem : EntitySystem
{
    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SharedBodySystem _body = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<EntityUid, EntityUid> _active = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<BidenBlastComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<BidenBlastComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<BidenBlastComponent, BidenBlastActionEvent>(OnFire);
        SubscribeLocalEvent<BidenBlastEffectComponent, ComponentShutdown>(OnEffectShutdown);
        SubscribeNetworkEvent<BidenBlastAimEvent>(OnAim);
    }

    private void OnStartup(Entity<BidenBlastComponent> ent, ref ComponentStartup args)
    {
        _actions.AddAction(ent, ref ent.Comp.ActionEntity, ent.Comp.Action);
    }

    private void OnShutdown(Entity<BidenBlastComponent> ent, ref ComponentShutdown args)
    {
        if (_active.TryGetValue(ent.Owner, out var effect)
            && TryComp<BidenBlastEffectComponent>(effect, out var beam) && !beam.Firing)
            QueueDel(effect);
        _actions.RemoveAction(ent.Owner, ent.Comp.ActionEntity);
    }

    private void OnEffectShutdown(Entity<BidenBlastEffectComponent> ent, ref ComponentShutdown args)
    {
        _audio.Stop(ent.Comp.ChargeStream);
        _audio.Stop(ent.Comp.FireStream);
        if (TryGetEntity(ent.Comp.Shooter, out var shooter) && shooter is { } uid)
            _active.Remove(uid);
    }

    private void OnFire(Entity<BidenBlastComponent> ent, ref BidenBlastActionEvent args)
    {
        if (args.Handled || _active.ContainsKey(ent.Owner))
            return;
        var start = _transform.GetMapCoordinates(ent.Owner);
        var target = _transform.ToMapCoordinates(args.Target);
        var delta = target.Position - start.Position;
        if (start.MapId != target.MapId || delta.LengthSquared() < 0.0001f)
            return;
        args.Handled = true;
        var effect = Spawn("EffectBidenBlast", start);
        var beam = Comp<BidenBlastEffectComponent>(effect);
        beam.Shooter = GetNetEntity(ent.Owner);
        beam.Direction = Vector2.Normalize(delta);
        beam.Range = ent.Comp.Range;
        beam.Radius = ent.Comp.Radius;
        beam.StartedAt = _timing.CurTime;
        beam.FiresAt = beam.StartedAt + ent.Comp.ChargeTime;
        beam.EndsAt = beam.FiresAt + ent.Comp.FireTime;
        beam.Settings = ent.Comp;
        if (_audio.PlayPvs(ent.Comp.ChargeSound, ent.Owner) is { } charge)
        {
            beam.ChargeStream = charge.Entity;
            beam.ChargeGain = SharedAudioSystem.VolumeToGain(charge.Component.Params.Volume);
        }
        _active.Add(ent.Owner, effect);
        Dirty(effect, beam);
    }

    private void OnAim(BidenBlastAimEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } user
            || !_active.TryGetValue(user, out var effect) || !TryComp<BidenBlastEffectComponent>(effect, out var beam)
            || _timing.CurTime < beam.NextAim || !float.IsFinite(ev.Position.X) || !float.IsFinite(ev.Position.Y))
            return;
        beam.NextAim = _timing.CurTime + TimeSpan.FromSeconds(0.05);
        var delta = ev.Position - _transform.GetWorldPosition(user);
        if (delta.LengthSquared() < 0.0001f || !float.IsFinite(delta.LengthSquared()))
            return;
        beam.Direction = Vector2.Normalize(delta);
        Dirty(effect, beam);
    }

    private void AffectViewers(BidenBlastEffectComponent beam, BidenBlastComponent settings,
        TransformComponent xform, Vector2 start)
    {
        var viewers = EntityQueryEnumerator<ActorComponent, TransformComponent>();
        while (viewers.MoveNext(out var uid, out var actor, out var viewer))
        {
            if (viewer.MapID != xform.MapID)
                continue;
            var position = _transform.GetWorldPosition(uid);
            var along = Math.Clamp(Vector2.Dot(position - start, beam.Direction), 0f, beam.Range);
            var distance = Math.Max(0f, Vector2.Distance(position, start + beam.Direction * along) - beam.Radius);
            var filter = Filter.Empty().AddPlayer(actor.PlayerSession);
            var shake = distance < settings.TremorRadius
                ? settings.ShakeAmplitude * (1f - distance / settings.TremorRadius) : 0f;
            var flash = distance < settings.FlashRadius
                ? settings.FlashDuration * (1f - distance / settings.FlashRadius) : 0f;
            RaiseNetworkEvent(new BidenBlastFireEvent(beam.Shooter, beam.EndsAt, flash, shake), filter);
        }
    }

    private void FadeStream(EntityUid? stream, float gain, float fade)
    {
        if (stream is { } sound && TryComp<AudioComponent>(sound, out var audio))
            _audio.SetGain(sound, gain * fade, audio);
    }

    public override void Update(float frameTime)
    {
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<BidenBlastEffectComponent, TransformComponent>();
        while (query.MoveNext(out var effect, out var beam, out var xform))
        {
            if (now >= beam.EndsAt)
            {
                foreach (var part in beam.VaporizeParts)
                {
                    if (!TerminatingOrDeleted(part) && !EntityManager.IsQueuedForDeletion(part))
                        QueueDel(part);
                }
                QueueDel(effect);
                continue;
            }
            if (beam.Settings is not { } settings
                || !TryGetEntity(beam.Shooter, out var shooter) || shooter is not { } uid
                || TerminatingOrDeleted(uid) || Transform(uid).MapID != xform.MapID)
            {
                QueueDel(effect);
                continue;
            }
            var start = _transform.GetWorldPosition(uid) + beam.Direction * 0.7f;
            _transform.SetWorldPosition(effect, start);
            if (now < beam.FiresAt)
                continue;
            if (!beam.Firing)
            {
                beam.Firing = true;
                Dirty(effect, beam);
                if (!settings.ContinueChargeSound)
                    _audio.Stop(beam.ChargeStream);
                if (_audio.PlayPvs(settings.FireSound, uid) is { } fire)
                {
                    beam.FireStream = fire.Entity;
                    beam.FireGain = SharedAudioSystem.VolumeToGain(fire.Component.Params.Volume);
                }
                AffectViewers(beam, settings, xform, start);
                foreach (var limb in settings.LimbEffects)
                {
                    foreach (var part in _body.GetBodyChildrenOfType(uid, limb.Part, symmetry: limb.Symmetry))
                    {
                        if (limb.Vaporize && !beam.VaporizeParts.Contains(part.Id))
                            beam.VaporizeParts.Add(part.Id);
                        if (limb.Damage != null || !limb.Vaporize)
                            _damage.TryChangeDamage(part.Id, limb.Damage ?? settings.ArmDamage, origin: uid, canSever: false);
                    }
                }
            }
            var taper = Math.Clamp((float) (beam.EndsAt - now).TotalSeconds / 0.5f, 0f, 1f);
            var fade = taper * taper * (3f - 2f * taper);
            if (taper < 1f)
            {
                FadeStream(beam.FireStream, beam.FireGain, fade);
                if (settings.ContinueChargeSound)
                    FadeStream(beam.ChargeStream, beam.ChargeGain, fade);
            }
            if (now < beam.NextDamage)
                continue;
            beam.NextDamage = now + TimeSpan.FromSeconds(0.1);
            var radius = beam.Radius * fade;
            var bounds = new Box2Rotated(
                new Box2(start.X, start.Y - radius, start.X + beam.Range, start.Y + radius),
                new Angle(Math.Atan2(beam.Direction.Y, beam.Direction.X)), start);
            var targets = new List<(EntityUid Uid, float Distance)>();
            foreach (var victim in _lookup.GetEntitiesIntersecting(xform.MapID, bounds, LookupFlags.Uncontained))
            {
                if (victim == uid || victim == effect || !HasComp<DamageableComponent>(victim)
                    || TerminatingOrDeleted(victim) || EntityManager.IsQueuedForDeletion(victim))
                    continue;
                var distance = Vector2.Dot(_transform.GetWorldPosition(victim) - start, beam.Direction);
                targets.Add((victim, distance));
            }
            targets.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            var power = 1f;
            foreach (var (victim, _) in targets)
            {
                if (power < 0.01f)
                    break;
                if (TerminatingOrDeleted(victim) || EntityManager.IsQueuedForDeletion(victim)
                    || !TryComp<DamageableComponent>(victim, out var damageable))
                    continue;
                var wasAlive = _mobState.IsAlive(victim);
                var dealt = _damage.TryChangeDamage(victim, settings.Damage * (0.1f * power), origin: uid);
                if (wasAlive && !TerminatingOrDeleted(victim)
                    && (_mobState.IsCritical(victim) || _mobState.IsDead(victim)))
                {
                    Spawn("Ash", Transform(victim).Coordinates);
                    QueueDel(victim);
                }
                if (dealt?.AnyPositive() == true)
                    power *= Math.Clamp(damageable.DamageContainerID == "StructuralInorganic"
                        ? settings.StructuralPierceFalloff : settings.PierceFalloff, 0f, 1f);
            }
        }
    }
}
