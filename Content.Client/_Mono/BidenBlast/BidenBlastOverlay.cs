using System.Numerics;
using System.Linq;
using Content.Shared._Mono.BidenBlast;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._Mono.BidenBlast;

public sealed class BidenBlastOverlay(IEntityManager entities, Dictionary<NetEntity, TimeSpan> fireCues) : Overlay
{
    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    private readonly IGameTiming _timing = IoCManager.Resolve<IGameTiming>();
    private readonly IPlayerManager _players = IoCManager.Resolve<IPlayerManager>();
    private readonly IInputManager _input = IoCManager.Resolve<IInputManager>();
    private readonly IEyeManager _eye = IoCManager.Resolve<IEyeManager>();
    private readonly Dictionary<EntityUid, BlastBeam> _beams = new();
    private readonly Vector2[] _vertices = new Vector2[4];

    protected override void Draw(in OverlayDrawArgs args)
    {
        var transform = entities.System<SharedTransformSystem>();
        var time = (float) _timing.CurTime.TotalSeconds;
        foreach (var uid in _beams.Keys.ToArray())
        {
            if (entities.HasComponent<BidenBlastEffectComponent>(uid))
                continue;
            _beams[uid].DisposeVisual();
            _beams.Remove(uid);
        }
        var query = entities.EntityQueryEnumerator<BidenBlastEffectComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var effect, out var xform))
        {
            if (xform.MapID != args.MapId || _timing.CurTime >= effect.EndsAt)
                continue;
            if (!_beams.TryGetValue(uid, out var beam))
            {
                beam = new BlastBeam
                {
                    Direction = effect.Direction,
                    UpdatedAt = _timing.RealTime,
                    Firing = effect.Firing,
                };
                _beams.Add(uid, beam);
            }
            var direction = effect.Direction;
            var start = transform.GetWorldPosition(uid);
            if (entities.TryGetEntity(effect.Shooter, out var shooter) && shooter is { } owner)
            {
                var origin = transform.GetWorldPosition(owner);
                if (_players.LocalEntity == owner && _input.MouseScreenPosition.IsValid)
                {
                    var mouse = _eye.PixelToMap(_input.MouseScreenPosition);
                    var delta = mouse.Position - origin;
                    if (mouse.MapId == xform.MapID && delta.LengthSquared() > 0.0001f)
                        direction = Vector2.Normalize(delta);
                    beam.Direction = direction;
                }
                else
                {
                    var elapsed = (float) (_timing.RealTime - beam.UpdatedAt).TotalSeconds;
                    var angle = MathF.Atan2(beam.Direction.Y, beam.Direction.X);
                    var difference = (float) Math.IEEERemainder(MathF.Atan2(direction.Y, direction.X) - angle, Math.Tau);
                    angle += difference * (1f - MathF.Exp(-20f * elapsed));
                    beam.Direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                }
                start = origin + beam.Direction * 0.7f;
            }
            beam.UpdatedAt = _timing.RealTime;
            beam.Firing |= fireCues.ContainsKey(effect.Shooter);
            if (!beam.Firing)
            {
                var duration = Math.Max((effect.FiresAt - effect.StartedAt).TotalSeconds, 0.001);
                var progress = (float) Math.Clamp((_timing.CurTime - effect.StartedAt).TotalSeconds / duration, 0, 1);
                DrawCharge(args.DrawingHandle, beam.ChargeShader, start, Math.Max(effect.Radius, 0.1f), progress, time);
                continue;
            }
            var remaining = (float) (effect.EndsAt - _timing.CurTime).TotalSeconds;
            var taper = Math.Clamp(remaining / 0.5f, 0f, 1f);
            beam.Radius = effect.Radius * taper * taper * (3f - 2f * taper);
            beam.Render(args.DrawingHandle, start, start + beam.Direction * effect.Range, time);
        }
    }

    private void DrawCharge(DrawingHandleBase handle, ShaderInstance shader, Vector2 center,
        float radius, float progress, float time)
    {
        shader.SetParameter("CENTER", center);
        shader.SetParameter("RADIUS", radius);
        shader.SetParameter("PROGRESS", progress);
        shader.SetParameter("CLOCK", time);
        var extent = radius * 3f;
        _vertices[0] = center + new Vector2(-extent, -extent);
        _vertices[1] = center + new Vector2(extent, -extent);
        _vertices[2] = center + new Vector2(extent, extent);
        _vertices[3] = center + new Vector2(-extent, extent);
        handle.UseShader(shader);
        try
        {
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, _vertices, Color.White);
        }
        finally
        {
            handle.UseShader(null);
        }
    }

    protected override void DisposeBehavior()
    {
        foreach (var beam in _beams.Values)
            beam.DisposeVisual();
        _beams.Clear();
        base.DisposeBehavior();
    }

    private sealed class BlastBeam : IDisposable
    {
        public float Radius;
        public bool Firing;
        public Vector2 Direction;
        public TimeSpan UpdatedAt;
        public readonly ShaderInstance ChargeShader = IoCManager.Resolve<IPrototypeManager>()
            .Index<ShaderPrototype>("BidenBlastCharge").InstanceUnique();
        private readonly ShaderInstance _shader = IoCManager.Resolve<IPrototypeManager>()
            .Index<ShaderPrototype>("BidenBlastBeam").InstanceUnique();
        private readonly Vector2[] _vertices = new Vector2[4];

        public void Render(DrawingHandleBase handle, Vector2 start, Vector2 end, float time)
        {
            var delta = end - start;
            if (delta.LengthSquared() < 0.001f)
                return;
            var direction = Vector2.Normalize(delta);
            var normal = new Vector2(-direction.Y, direction.X);
            var radius = MathF.Max(Radius, 0.001f);
            _shader.SetParameter("BEAM_START", start);
            _shader.SetParameter("BEAM_CAP_START", start);
            _shader.SetParameter("BEAM_CAP_END", end);
            _shader.SetParameter("BEAM_DIRECTION", direction);
            _shader.SetParameter("BEAM_RADIUS", radius);
            _shader.SetParameter("BEAM_TIP_FADE", 0.5f);
            _shader.SetParameter("BEAM_TIME", time * 22f);
            _shader.SetParameter("BEAM_PHASE", 0f);
            _shader.SetParameter("BEAM_FLOW_OFFSET", 0f);
            _shader.SetParameter("BEAM_CLOUD_DECK", Vector3.Zero);
            _shader.SetParameter("BEAM_CLOUD_VEIL", Vector3.Zero);
            _shader.SetParameter("BEAM_COLOR", new Vector3(0.35f, 0.7f, 1f));
            var width = normal * radius * 4f;
            var from = start - direction * radius * 4f;
            var to = end + direction * 0.125f;
            _vertices[0] = from - width;
            _vertices[1] = to - width;
            _vertices[2] = to + width;
            _vertices[3] = from + width;
            handle.UseShader(_shader);
            try
            {
                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, _vertices, Color.White);
            }
            finally
            {
                handle.UseShader(null);
            }
        }

        public void DisposeVisual()
        {
            ChargeShader.Dispose();
            _shader.Dispose();
        }

        public void Dispose() => DisposeVisual();
    }
}
