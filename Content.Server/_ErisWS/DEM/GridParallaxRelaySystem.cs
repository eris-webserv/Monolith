using Content.Shared._ErisWS.DEM;
using Robust.Server.GameStates;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server._ErisWS.DEM;

/// <summary>
/// Spawns and synchronizes lightweight grid macrostate relays.
/// </summary>
public sealed partial class GridParallaxRelaySystem : EntitySystem
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private PvsOverrideSystem _pvs = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly Dictionary<EntityUid, HashSet<ICommonSession>> _overrides = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<GridParallaxComponent, ComponentStartup>(OnGridStartup);
        SubscribeLocalEvent<GridParallaxComponent, ComponentShutdown>(OnGridShutdown);
        SubscribeLocalEvent<GridParallaxRelayComponent, ComponentShutdown>(OnRelayShutdown);
    }

    public override void Update(float frameTime)
    {
        var gridQuery = EntityQueryEnumerator<GridParallaxComponent, TransformComponent>();
        while (gridQuery.MoveNext(out var grid, out var parallax, out var xform))
        {
            if (parallax.Relay == null || TerminatingOrDeleted(parallax.Relay.Value))
                continue;

            _transform.SetWorldPosition(parallax.Relay.Value, _transform.GetWorldPosition(xform));
            _transform.SetWorldRotation(parallax.Relay.Value, _transform.GetWorldRotation(xform));
        }

        var query = EntityQueryEnumerator<GridParallaxRelayComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var parallax, out var xform))
        {
            var active = _overrides.GetOrNew(uid);
            var position = _transform.GetWorldPosition(xform);

            foreach (var session in _players.Sessions)
            {
                var viewer = session.AttachedEntity;
                var visible = parallax.Enabled
                    && viewer != null
                    && TryComp<TransformComponent>(viewer, out var viewerXform)
                    && viewerXform.MapID == xform.MapID
                    && (_transform.GetWorldPosition(viewerXform) - position).LengthSquared() <= parallax.FarDistance * parallax.FarDistance;

                if (visible && active.Add(session))
                    _pvs.AddSessionOverride(uid, session);
                else if (!visible && active.Remove(session))
                    _pvs.RemoveSessionOverride(uid, session);
            }
        }
    }

    public override void Shutdown()
    {
        foreach (var (uid, sessions) in _overrides)
        {
            foreach (var session in sessions)
            {
                _pvs.RemoveSessionOverride(uid, session);
            }
        }

        _overrides.Clear();
        base.Shutdown();
    }

    private void OnGridStartup(Entity<GridParallaxComponent> ent, ref ComponentStartup args)
    {
        if (!HasComp<MapGridComponent>(ent))
            return;

        ent.Comp.Relay = SpawnAtPosition(ent.Comp.RelayPrototype, Transform(ent).Coordinates);
    }

    private void OnGridShutdown(Entity<GridParallaxComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.Relay != null)
            QueueDel(ent.Comp.Relay.Value);
    }

    private void OnRelayShutdown(Entity<GridParallaxRelayComponent> ent, ref ComponentShutdown args)
    {
        if (!_overrides.Remove(ent.Owner, out var sessions))
            return;

        foreach (var session in sessions)
        {
            _pvs.RemoveSessionOverride(ent.Owner, session);
        }
    }
}
