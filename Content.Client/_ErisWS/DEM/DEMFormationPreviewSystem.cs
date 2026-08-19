using Content.Shared._ErisWS.DEM;
using Robust.Client.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Timing;

namespace Content.Client._ErisWS.DEM;

public sealed partial class DEMFormationPreviewSystem : EntitySystem
{
    private const float Lifetime = 5f;
    private const float FadeTime = 2f;

    [Dependency] private SpriteSystem _sprite = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly Dictionary<EntityUid, TimeSpan> _ghosts = [];
    private readonly HashSet<EntityUid> _expired = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<DEMFormationPreviewEvent>(OnPreview);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _expired.Clear();

        foreach (var (uid, start) in _ghosts)
        {
            var elapsed = (float) (_timing.CurTime - start).TotalSeconds;
            if (!Exists(uid) || elapsed >= Lifetime)
            {
                if (Exists(uid))
                    QueueDel(uid);

                _expired.Add(uid);
                continue;
            }

            if (!TryComp<SpriteComponent>(uid, out var sprite))
                continue;

            var fade = Math.Clamp((Lifetime - elapsed) / FadeTime, 0f, 1f);
            _sprite.SetColor((uid, sprite), Color.Red.WithAlpha(0.85f * fade));
        }

        foreach (var uid in _expired)
            _ghosts.Remove(uid);
    }

    private void OnPreview(DEMFormationPreviewEvent message)
    {
        foreach (var uid in _ghosts.Keys)
        {
            if (Exists(uid))
                QueueDel(uid);
        }

        _ghosts.Clear();
        foreach (var part in message.Parts)
        {
            var prototype = _prototypes.Index<EntityPrototype>(part.Prototype);
            if (!prototype.Components.TryGetComponent("Sprite", out var component) || component is not SpriteComponent prototypeSprite)
                continue;

            var ghost = Spawn("MultipartMachineGhost", part.Coordinates);
            _transform.SetLocalRotationNoLerp(ghost, part.Rotation);
            var sprite = EnsureComp<SpriteComponent>(ghost);
            _serialization.CopyTo(prototypeSprite, ref sprite, notNullableOverride: true);
            _sprite.SetColor((ghost, sprite), Color.Red.WithAlpha(0.85f));
            _ghosts[ghost] = _timing.CurTime;
        }
    }
}
