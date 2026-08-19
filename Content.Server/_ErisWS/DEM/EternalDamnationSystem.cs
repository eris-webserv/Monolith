using Content.Shared._ErisWS.DEM;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Random;

namespace Content.Server._ErisWS.DEM;

public sealed partial class EternalDamnationSystem : EntitySystem
{
    [Dependency] private readonly SharedMapSystem _maps = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MetaDataSystem _metadata = default!;

    private EntityUid? _map;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<DEMComponent, DEMConsumeEntityEvent>(OnConsume);
    }

    private void OnConsume(Entity<DEMComponent> dem, ref DEMConsumeEntityEvent args)
    {
        if (TerminatingOrDeleted(args.Target))
            return;

        var targetXform = Transform(args.Target);
        var damned = EnsureComp<EternalDamnationComponent>(args.Target);
        damned.Source = dem.Owner;
        damned.ReturnCoordinates = targetXform.Coordinates;
        Dirty(args.Target, damned);

        if (TryComp<PhysicsComponent>(args.Target, out var body))
        {
            _physics.SetLinearVelocity(args.Target, default, body: body);
            _physics.SetAngularVelocity(args.Target, 0f, body: body);
        }

        var map = EnsureMap();
        _transform.SetCoordinates(args.Target, targetXform, new EntityCoordinates(map, _random.NextVector2(10f, 100f)));
        args.Handled = true;
    }

    private EntityUid EnsureMap()
    {
        if (_map is { } existing && Exists(existing))
            return existing;

        var query = EntityQueryEnumerator<EternalDamnationMapComponent>();
        if (query.MoveNext(out var found, out _))
        {
            _map = found;
            return found;
        }

        var map = _maps.CreateMap(out _, runMapInit: false);
        AddComp<EternalDamnationMapComponent>(map);
        var light = EnsureComp<MapLightComponent>(map);
        light.AmbientLightColor = Color.FromSrgb(Color.Black);
        Dirty(map, light);
        _metadata.SetEntityName(map, "Eternal Damnation");
        _map = map;
        return map;
    }
}
