using System.Numerics;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Movement.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Shared._ErisWS.DEM;

public sealed class DEMIngestionSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly INetManager _net = default!;

    private readonly HashSet<EntityUid> _nearby = new();

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(SharedMoverController));
        UpdatesBefore.Add(typeof(SharedPhysicsSystem));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var xformQuery = GetEntityQuery<TransformComponent>();
        var physicsQuery = GetEntityQuery<PhysicsComponent>();
        var query = EntityQueryEnumerator<DEMComponent, DEMMachineComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var dem, out var machine, out var xform))
        {
            if (!machine.Constructed || !IsActive(dem.State))
                continue;

            var center = _transform.GetMapCoordinates(uid, xform: xform);
            if (center.MapId == MapId.Nullspace)
                continue;

            _nearby.Clear();
            _lookup.GetEntitiesInRange(center.MapId,
                center.Position,
                dem.IngestionRadius,
                _nearby,
                LookupFlags.Dynamic | LookupFlags.Sundries);

            var stateChanged = false;
            foreach (var target in _nearby)
            {
                if (!CanIngest(target, uid, xformQuery, physicsQuery, out var targetXform, out var body))
                    continue;

                var offset = _transform.GetWorldPosition(targetXform, xformQuery) - center.Position;
                var distance = offset.Length();
                if (distance > dem.IngestionRadius || distance < 0.001f)
                    continue;

                if (_net.IsServer && distance <= dem.ConsumptionRadius)
                {
                    var consumed = new DEMConsumeEntityEvent(target);
                    RaiseLocalEvent(uid, ref consumed);
                    if (!consumed.Handled)
                        continue;

                    dem.State.CoreMass += Math.Max(1L, (long) MathF.Ceiling(body.Mass));
                    dem.State.AccretionDiskSaturation = Math.Clamp(
                        dem.State.AccretionDiskSaturation + Math.Max(0.25f, body.Mass * 0.01f),
                        0f,
                        100f);
                    stateChanged = true;
                    continue;
                }

                Move(target, body, offset, distance, dem);

                if (!_net.IsServer)
                    continue;

                Damage(target, dem, frameTime);
                var spinScale = Math.Clamp(Math.Abs((float) dem.State.AccretionDiskSpin) / 60f, 0f, 1f);
                if (spinScale > 0f)
                {
                    dem.State.AccretionDiskSaturation = Math.Clamp(
                        dem.State.AccretionDiskSaturation + dem.DiskSaturationPerSecond * spinScale * frameTime,
                        0f,
                        100f);
                    stateChanged = true;
                }
            }

            if (_net.IsServer && stateChanged)
                Dirty(uid, dem);
        }
    }

    private void Move(EntityUid uid, PhysicsComponent body, Vector2 offset, float distance, DEMComponent dem)
    {
        var outward = offset / distance;
        var tangent = new Vector2(-outward.Y, outward.X);
        var angularSpeed = (float) dem.State.AccretionDiskSpin * MathF.Tau / 60f;
        var orbitSpeed = Math.Clamp(angularSpeed * distance, -dem.MaxOrbitSpeed, dem.MaxOrbitSpeed);
        var depth = 1f - distance / dem.IngestionRadius;
        var desired = tangent * orbitSpeed - outward * dem.PullSpeed * (1f + depth * 2f);

        _physics.SetLinearVelocity(uid, desired, dirty: _net.IsServer, body: body);
        _physics.SetAngularVelocity(uid,
            Math.Clamp(angularSpeed, -dem.MaxOrbitSpeed, dem.MaxOrbitSpeed),
            dirty: _net.IsServer,
            body: body);
    }

    private void Damage(EntityUid uid, DEMComponent dem, float frameTime)
    {
        if (!HasComp<DamageableComponent>(uid))
            return;

        var temperatureScale = Math.Clamp((float) dem.State.AccretionDiskTemperature / 1000f, 0.25f, 4f);
        var damage = new DamageSpecifier
        {
            DamageDict = new Dictionary<string, FixedPoint2>
            {
                ["Heat"] = FixedPoint2.New(dem.HeatDamagePerSecond * temperatureScale * frameTime),
                ["Blunt"] = FixedPoint2.New(dem.BluntDamagePerSecond * frameTime)
            }
        };
        _damageable.TryChangeDamage(uid, damage, interruptsDoAfters: false);
    }

    private bool CanIngest(
        EntityUid uid,
        EntityUid control,
        EntityQuery<TransformComponent> xformQuery,
        EntityQuery<PhysicsComponent> physicsQuery,
        out TransformComponent xform,
        out PhysicsComponent body)
    {
        xform = null!;
        body = null!;

        if (uid == control ||
            HasComp<DEMComponent>(uid) ||
            HasComp<DEMAssemblyComponent>(uid) ||
            HasComp<DEMScrubberPartComponent>(uid) ||
            HasComp<DEMLaserComponent>(uid) ||
            HasComp<MapGridComponent>(uid))
            return false;

        if (!xformQuery.TryGetComponent(uid, out var foundXform) ||
            foundXform == null ||
            foundXform.Anchored ||
            !physicsQuery.TryGetComponent(uid, out var foundBody) ||
            foundBody == null)
            return false;

        xform = foundXform;
        body = foundBody;

        return body.BodyType is not BodyType.Static and not BodyType.Kinematic;
    }

    private static bool IsActive(DEMState state)
    {
        return state.CurrentPhase switch
        {
            DEMPhase.OFFLINE => false,
            DEMPhase.STARTING => state.CoreVisible,
            _ => true
        };
    }
}
