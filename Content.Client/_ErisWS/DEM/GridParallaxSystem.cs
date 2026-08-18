using System.Numerics;
using Content.Shared._ErisWS.DEM;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.Client._ErisWS.DEM;

/// <summary>
/// Renders grid macrostate relay sprites on a hyperbolic approach curve.
/// </summary>
public sealed partial class GridParallaxSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;

    public override void Initialize()
    {
        _overlays.AddOverlay(new GridParallaxOverlay(EntityManager));
    }

    public override void Shutdown()
    {
        _overlays.RemoveOverlay<GridParallaxOverlay>();
    }

    public static bool TryProject(
        GridParallaxRelayComponent parallax,
        Vector2 eyePosition,
        Vector2 sourcePosition,
        out Vector2 displayPosition,
        out float scale,
        out float distance)
    {
        var offset = sourcePosition - eyePosition;
        distance = offset.Length();
        displayPosition = sourcePosition;
        scale = 1f;

        var near = MathF.Max(parallax.NearDistance, parallax.MinDistance);
        var far = MathF.Max(parallax.FarDistance, near + parallax.MinDistance);
        if (!parallax.Enabled || distance <= near || distance > far)
            return false;

        var depth = (distance - near) / (far - near);
        var axis = depth * 2f - 1f;
        var tightness = MathF.Max(parallax.Tightness, 0.01f);
        var displayDistance = near * MathF.Sqrt(1f + axis * axis / (tightness * tightness));

        displayPosition = eyePosition + offset / distance * displayDistance;
        scale = parallax.NearScale + (parallax.FarScale - parallax.NearScale) * depth;
        return true;
    }

    private sealed class GridParallaxOverlay : Overlay
    {
        private readonly IEntityManager _entities;
        private readonly SpriteSystem _sprites;
        private readonly SharedTransformSystem _transform;

        public override OverlaySpace Space => OverlaySpace.WorldSpace;

        public GridParallaxOverlay(IEntityManager entities)
        {
            _entities = entities;
            _sprites = entities.System<SpriteSystem>();
            _transform = entities.System<SharedTransformSystem>();
        }

        protected override void Draw(in OverlayDrawArgs args)
        {
            if (args.MapId == MapId.Nullspace || args.Viewport.Eye == null)
                return;

            var eye = args.Viewport.Eye;
            var eyePosition = eye.Position.Position;
            var eyeRotation = eye.Rotation;
            var handle = args.WorldHandle;
            var query = _entities.EntityQueryEnumerator<GridParallaxRelayComponent, SpriteComponent, TransformComponent>();

            while (query.MoveNext(out var uid, out var parallax, out var sprite, out var xform))
            {
                if (xform.MapID != args.MapId)
                    continue;

                var position = _transform.GetWorldPosition(xform);
                if (!TryProject(parallax, eyePosition, position, out var displayPosition, out var scale, out _))
                    continue;

                var originalScale = sprite.Scale;
                sprite.Scale *= scale;
                _sprites.RenderSprite((uid, sprite), handle, eyeRotation, _transform.GetWorldRotation(xform), displayPosition);
                sprite.Scale = originalScale;
            }
        }
    }
}
