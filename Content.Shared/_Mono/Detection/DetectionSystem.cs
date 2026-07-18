using Content.Shared._CE.ZLevels.Core.Components;
using Content.Shared._CE.ZLevels.Core.EntitySystems;
using Content.Shared._Mono.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using System;

namespace Content.Shared._Mono.Detection;

/// <summary>
///     Handles the logic for grid and entity detection.
/// </summary>
public sealed partial class DetectionSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private CESharedZLevelsSystem _zLevels = default!;

    private float _thermalMul;
    private float _visualMul;
    private float _mediumMass = 300;
    private float _largeMass = 600;
    private float _hugeMass = 1000;
    private float _supermassiveMass = 2000;

    public override void Initialize()
    {
        base.Initialize();

        Subs.CVar(_cfg, MonoCVars.ThermalDetectionMultiplier, value => _thermalMul = value, true);
        Subs.CVar(_cfg, MonoCVars.VisualDetectionMultiplier, value => _visualMul = value, true);
    }

    public DetectionLevel IsGridDetected(Entity<MapGridComponent?> grid, EntityUid byUid)
    {
        if (!Resolve(grid, ref grid.Comp))
            return DetectionLevel.Undetected;

        var comp = EnsureComp<DetectionRangeMultiplierComponent>(byUid);

        if (comp.AlwaysDetect)
            return DetectionLevel.Detected;

        var gridAABB = grid.Comp.LocalAABB;
        var gridDiagonal = MathF.Sqrt(gridAABB.Width * gridAABB.Width + gridAABB.Height * gridAABB.Height);
        var visualSig = gridDiagonal;
        var visualRadius = visualSig * comp.VisualMultiplier * _visualMul;

        var thermalSig = TryComp<ThermalSignatureComponent>(grid, out var sigComp) ? MathF.Max(sigComp.TotalHeat, 0f) : 0f;
        var thermalRadius = MathF.Sqrt(thermalSig) * comp.InfraredMultiplier * _thermalMul;

        if (TryComp<DetectedAtRangeMultiplierComponent>(grid, out var compAt))
        {
            visualRadius *= compAt.VisualMultiplier;
            thermalRadius *= compAt.InfraredMultiplier;
            visualRadius += compAt.VisualBias;
        }

        var outlineRadius = thermalRadius * comp.InfraredOutlinePortion;
        outlineRadius = MathF.Max(outlineRadius, visualRadius);

        var level = DetectionLevel.Undetected;

        var xform = Transform(grid);
        var byXform = Transform(byUid);
        if (TryGetDetectionDistance(xform, byXform, out var distance))
        {
            if (distance <= outlineRadius) // accounts for visual radius
                level = DetectionLevel.Detected;
            else if (distance < thermalRadius)
                level = DetectionLevel.PartialDetected;
        }

        // maybe make this also take IFF being on into account?
        return level;
    }

    /// <summary>
    /// CE: Distance between two entities for detection purposes. Same-map distance is the usual
    /// planar distance. If the entities sit on different maps that belong to the same z-network
    /// (including transit-gap maps mid-ride), z-maps share world coordinates, so we can still
    /// measure the planar separation instead of silently failing — otherwise sensors would
    /// hard-drop a contact the instant it crossed onto an adjacent level.
    /// </summary>
    private bool TryGetDetectionDistance(TransformComponent xform, TransformComponent byXform, out float distance)
    {
        if (xform.Coordinates.TryDistance(EntityManager, byXform.Coordinates, out distance))
            return true;

        if (xform.MapUid is not { } mapA || byXform.MapUid is not { } mapB)
            return false;

        if (!TryGetZNetwork(mapA, out var netA) || !TryGetZNetwork(mapB, out var netB) || netA != netB)
            return false;

        distance = (_transform.GetWorldPosition(xform) - _transform.GetWorldPosition(byXform)).Length();
        return true;
    }

    /// <summary>
    /// CE: Resolves the z-network a map belongs to. Transit-gap maps aren't network members
    /// themselves, so route those through their lower anchor.
    /// </summary>
    private bool TryGetZNetwork(EntityUid mapUid, out EntityUid network)
    {
        network = default;

        if (_zLevels.TryGetMapNetwork(mapUid, out var net))
        {
            network = net.Owner;
            return true;
        }

        if (TryComp<CEZTransitMapComponent>(mapUid, out var transit)
            && transit.LowerMap is { } lower
            && _zLevels.TryGetMapNetwork(lower, out net))
        {
            network = net.Owner;
            return true;
        }

        return false;
    }

    public DetectionLevel IsGridDetected(Entity<MapGridComponent?> grid, IEnumerable<EntityUid> byUids)
    {
        var bestLevel = DetectionLevel.Undetected;
        foreach (var uid in byUids)
        {
            var level = IsGridDetected(grid, uid);
            if (level == DetectionLevel.Detected)
                return level;

            if ((int)level < (int)bestLevel)
                bestLevel = level;
        }
        return bestLevel;
    }

    public MassLevel CheckMass(Entity<MapGridComponent?> grid)
    {
        var physics = Comp<PhysicsComponent>(grid);

        if (physics.FixturesMass >= _supermassiveMass)
            return MassLevel.Supermassive;
        if (physics.FixturesMass >= _hugeMass)
            return MassLevel.Huge;
        if (physics.FixturesMass >= _largeMass)
            return MassLevel.Large;
        if (physics.FixturesMass >= _mediumMass)
            return MassLevel.Medium;
        if (physics.FixturesMass >= 0)
            return MassLevel.Small;
        return MassLevel.Unknown;
    }

    public string HandleUnknownMassLabel(Entity<MapGridComponent?> grid)
    {
        var massLevel = CheckMass(grid);
        var massLevelKey = massLevel.ToString().ToLowerInvariant();

        return Loc.GetString("shuttle-console-signature-unknown", ("mass", massLevelKey));
    }
}

public enum DetectionLevel : int
{
    Detected = 0,
    PartialDetected = 1,
    Undetected = 2
}

public enum MassLevel : int
{
    Unknown = 0,
    Small = 1,
    Medium = 2,
    Large = 3,
    Huge = 4,
    Supermassive = 5
}
