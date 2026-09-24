using System.Numerics;
using System.Linq;
using Content.Client._CE.Camera;
using Content.Shared._Mono.BidenBlast;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Timing;

namespace Content.Client._Mono.BidenBlast;

public sealed partial class BidenBlastSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IInputManager _input = default!;
    [Dependency] private IEyeManager _eye = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private CEScreenFlashSystem _flash = default!;
    [Dependency] private RadialShakeSystem _shake = default!;
    private readonly Dictionary<NetEntity, TimeSpan> _fireCues = new();
    private TimeSpan _nextAim;
    private EntityUid? _predicted;
    private TimeSpan _predictionDeadline;

    public override void Initialize()
    {
        _overlays.AddOverlay(new BidenBlastOverlay(EntityManager, _fireCues));
        SubscribeLocalEvent<BidenBlastComponent, BidenBlastActionEvent>(OnAction);
        SubscribeNetworkEvent<BidenBlastFireEvent>(OnFireCue);
    }

    private void OnFireCue(BidenBlastFireEvent ev)
    {
        _fireCues[ev.Shooter] = ev.EndsAt;
        _flash.Flash(ev.FlashDuration, Color.White);
        if (_players.LocalEntity is { } player)
            _shake.Shake(player, 4f, ev.ShakeAmplitude);
    }

    private void OnAction(Entity<BidenBlastComponent> ent, ref BidenBlastActionEvent args)
    {
        if (args.Handled || ent.Owner != _players.LocalEntity)
            return;
        args.Handled = true;
        if (!_timing.IsFirstTimePredicted || _predicted != null)
            return;
        var origin = _transform.GetMapCoordinates(ent.Owner);
        var target = _transform.ToMapCoordinates(args.Target);
        var delta = target.Position - origin.Position;
        if (origin.MapId != target.MapId || delta.LengthSquared() < 0.0001f)
            return;
        var uid = Spawn("EffectBidenBlast", origin);
        var effect = Comp<BidenBlastEffectComponent>(uid);
        effect.Shooter = GetNetEntity(ent.Owner);
        effect.Direction = Vector2.Normalize(delta);
        effect.Radius = ent.Comp.Radius;
        effect.Range = ent.Comp.Range;
        effect.StartedAt = _timing.CurTime;
        effect.FiresAt = effect.StartedAt + ent.Comp.ChargeTime;
        effect.EndsAt = effect.FiresAt + ent.Comp.FireTime;
        _predicted = uid;
        _predictionDeadline = _timing.CurTime + TimeSpan.FromSeconds(2);
    }
    public override void Shutdown() => _overlays.RemoveOverlay<BidenBlastOverlay>();

    public override void Update(float frameTime)
    {
        foreach (var (shooter, endsAt) in _fireCues.ToArray())
        {
            if (_timing.CurTime >= endsAt)
                _fireCues.Remove(shooter);
        }
        if (_predicted is { } predicted)
        {
            var confirmed = false;
            var effects = EntityQueryEnumerator<BidenBlastEffectComponent>();
            while (effects.MoveNext(out var uid, out var effect))
            {
                if (uid != predicted && _players.LocalEntity is { } player && effect.Shooter == GetNetEntity(player))
                    confirmed = true;
            }
            if (confirmed || _timing.CurTime >= _predictionDeadline || _players.LocalEntity == null)
            {
                QueueDel(predicted);
                _predicted = null;
            }
        }
        if (_timing.CurTime < _nextAim || _players.LocalEntity is not { } user
            || !_input.MouseScreenPosition.IsValid)
            return;
        _nextAim = _timing.CurTime + TimeSpan.FromSeconds(0.05);
        var query = EntityQueryEnumerator<BidenBlastEffectComponent>();
        while (query.MoveNext(out var beam))
        {
            if (beam.Shooter != GetNetEntity(user))
                continue;
            var target = _eye.PixelToMap(_input.MouseScreenPosition);
            if (target.MapId == Transform(user).MapID)
                RaiseNetworkEvent(new BidenBlastAimEvent(target.Position));
            break;
        }
    }
}
