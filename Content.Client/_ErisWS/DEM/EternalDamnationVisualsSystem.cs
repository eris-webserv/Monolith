using Content.Shared._ErisWS.DEM;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Client._ErisWS.DEM;

public sealed class EternalDamnationVisualsSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IOverlayManager _overlays = default!;

    private ShaderInstance _outline = default!;
    private EternalDamnationBackgroundOverlay _background = default!;

    public override void Initialize()
    {
        base.Initialize();
        _outline = _prototypes.Index<ShaderPrototype>("EternalDamnationOutline").InstanceUnique();
        _background = new EternalDamnationBackgroundOverlay(EntityManager);
        _overlays.AddOverlay(_background);
        SubscribeLocalEvent<EternalDamnationComponent, ComponentStartup>(OnDamnedStartup);
        SubscribeLocalEvent<EternalDamnationComponent, ComponentShutdown>(OnDamnedShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<EternalDamnationComponent, SpriteComponent>();
        while (query.MoveNext(out _, out _, out var sprite))
        {
            if (sprite.PostShader != _outline)
                sprite.PostShader = _outline;
        }
    }

    public override void Shutdown()
    {
        _overlays.RemoveOverlay<EternalDamnationBackgroundOverlay>();
        _background.Dispose();
        _outline.Dispose();
        base.Shutdown();
    }

    private void OnDamnedStartup(Entity<EternalDamnationComponent> ent, ref ComponentStartup args)
    {
        SetOutline(ent.Owner, true);
    }

    private void OnDamnedShutdown(Entity<EternalDamnationComponent> ent, ref ComponentShutdown args)
    {
        SetOutline(ent.Owner, false);
    }

    private void SetOutline(EntityUid uid, bool enabled, SpriteComponent? sprite = null)
    {
        if (!Resolve(uid, ref sprite, false))
            return;

        if (enabled)
            sprite.PostShader = _outline;
        else if (sprite.PostShader == _outline)
            sprite.PostShader = null;
    }

    private sealed class EternalDamnationBackgroundOverlay(IEntityManager entities) : Overlay
    {
        public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowWorld;

        protected override bool BeforeDraw(in OverlayDrawArgs args)
        {
            return entities.HasComponent<EternalDamnationMapComponent>(args.MapUid);
        }

        protected override void Draw(in OverlayDrawArgs args)
        {
            args.WorldHandle.DrawRect(args.WorldBounds, Color.Black);
        }
    }
}
