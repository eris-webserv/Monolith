using System.Numerics;
using Content.Shared._ErisWS.DEM;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Client._ErisWS.DEM;

public sealed partial class DEMCoreVisualsSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private DEMCoreOverlay _overlay = default!;

    public override void Initialize()
    {
        base.Initialize();
        _overlay = new DEMCoreOverlay(EntityManager, _prototypes);
        _overlays.AddOverlay(_overlay);
        SubscribeLocalEvent<DEMComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<DEMComponent, ComponentShutdown>(OnShutdown);
    }

    public override void Shutdown()
    {
        _overlays.RemoveOverlay<DEMCoreOverlay>();
        _overlay.Dispose();
        base.Shutdown();
    }

    private void OnInit(Entity<DEMComponent> ent, ref ComponentInit args)
    {
        _overlay.Add(ent.Owner);
    }

    private void OnShutdown(Entity<DEMComponent> ent, ref ComponentShutdown args)
    {
        _overlay.Remove(ent.Owner);
    }

    private sealed class DEMCoreOverlay : Overlay
    {
        private const float EffectRadius = 6f;

        private readonly IEntityManager _entities;
        private readonly SharedTransformSystem _transform;
        private readonly ShaderPrototype _prototype;
        private readonly Dictionary<EntityUid, ShaderInstance> _shaders = new();

        public override OverlaySpace Space => OverlaySpace.WorldSpace;

        public DEMCoreOverlay(IEntityManager entities, IPrototypeManager prototypes)
        {
            _entities = entities;
            _transform = entities.System<SharedTransformSystem>();
            _prototype = prototypes.Index<ShaderPrototype>("DEMCore");
        }

        protected override void Draw(in OverlayDrawArgs args)
        {
            if (args.MapId == MapId.Nullspace)
                return;

            var handle = args.WorldHandle;
            var query = _entities.EntityQueryEnumerator<DEMComponent, TransformComponent>();

            while (query.MoveNext(out var uid, out var dem, out var xform))
            {
                if (xform.MapID != args.MapId ||
                    dem.State.CurrentPhase == DEMPhase.OFFLINE ||
                    dem.State.CurrentPhase == DEMPhase.STARTING && !dem.State.CoreVisible)
                    continue;

                var position = _transform.GetWorldPosition(xform);
                var bounds = Box2.CenteredAround(position, new Vector2(EffectRadius * 2f));
                if (!args.WorldAABB.Intersects(bounds))
                    continue;

                var shader = GetShader(uid);
                shader.SetParameter("diskTemperature", (float) dem.State.AccretionDiskTemperature);
                shader.SetParameter("diskSpin", (float) dem.State.AccretionDiskSpin);
                shader.SetParameter("diskSaturation", dem.State.AccretionDiskSaturation);

                handle.UseShader(shader);
                handle.DrawRect(bounds, Color.White);
                handle.UseShader(null);
            }
        }

        private ShaderInstance GetShader(EntityUid uid)
        {
            if (_shaders.TryGetValue(uid, out var shader))
                return shader;

            Add(uid);
            return _shaders[uid];
        }

        public void Add(EntityUid uid)
        {
            if (!_shaders.ContainsKey(uid))
                _shaders.Add(uid, _prototype.InstanceUnique());
        }

        public void Remove(EntityUid uid)
        {
            if (_shaders.Remove(uid, out var shader))
                shader.Dispose();
        }

        public void Dispose()
        {
            foreach (var shader in _shaders.Values)
                shader.Dispose();

            _shaders.Clear();
        }
    }
}
