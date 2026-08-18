using System.Numerics;
using Content.Shared._ErisWS.DEM;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Console;
using Robust.Shared.Enums;
using Robust.Shared.Map;

namespace Content.Client._ErisWS.DEM;

public sealed class GridParallaxDebugOverlay : Overlay
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IResourceCache _cache = default!;

    private readonly Font _font;
    private readonly SharedTransformSystem _transform;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public GridParallaxDebugOverlay()
    {
        IoCManager.InjectDependencies(this);
        _font = new VectorFont(_cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
        _transform = _entities.System<SharedTransformSystem>();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (args.MapId == MapId.Nullspace || args.Viewport.Eye == null)
            return;

        var eyePosition = args.Viewport.Eye.Position.Position;
        var query = _entities.EntityQueryEnumerator<GridParallaxRelayComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var parallax, out var xform))
        {
            if (xform.MapID != args.MapId)
                continue;

            var position = _transform.GetWorldPosition(xform);
            var distance = (position - eyePosition).Length();
            var near = MathF.Max(parallax.NearDistance, parallax.MinDistance);
            var rendering = GridParallaxSystem.TryProject(parallax,
                eyePosition,
                position,
                out var displayPosition,
                out var scale,
                out _);
            var markerPosition = rendering ? displayPosition : position;
            var screenPosition = args.ViewportControl?.WorldToScreen(markerPosition) ?? Vector2.Zero;
            if (screenPosition == Vector2.Zero)
                continue;

            var text = rendering
                ? $"Grid parallax {uid}: {position.X:0.0},{position.Y:0.0} -> {displayPosition.X:0.0},{displayPosition.Y:0.0} x{scale:0.00}"
                : distance > parallax.FarDistance
                    ? $"Grid parallax {uid}: culled by far ({distance:0.0} > {parallax.FarDistance:0.0})"
                    : $"Grid parallax {uid}: culled by near ({distance:0.0} <= {near:0.0})";

            args.ScreenHandle.DrawString(_font, screenPosition, text, rendering ? Color.Lime : Color.Orange);
        }
    }
}

public sealed class ShowGridParallaxDebugCommand : LocalizedCommands
{
    [Dependency] private IOverlayManager _overlays = default!;

    public override string Command => "showgridparallaxdebug";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (_overlays.HasOverlay<GridParallaxDebugOverlay>())
            _overlays.RemoveOverlay<GridParallaxDebugOverlay>();
        else
            _overlays.AddOverlay(new GridParallaxDebugOverlay());
    }
}
