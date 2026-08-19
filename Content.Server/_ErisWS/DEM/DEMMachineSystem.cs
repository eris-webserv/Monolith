using Content.Server.Machines.EntitySystems;
using Content.Shared._ErisWS.DEM;
using Content.Shared._Mono.Pvs;
using Content.Shared.DeviceLinking.Events;
using Content.Shared.Machines.Components;
using Content.Shared.Machines.Events;
using Robust.Server.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using System.Text;

namespace Content.Server._ErisWS.DEM;

public sealed partial class DEMMachineSystem : EntitySystem
{
    private enum PartKind : byte
    {
        Cardinal,
        Diagonal,
        Laser
    }

    private readonly record struct LayoutPart(
        DEMMachinePart Key,
        Vector2i Position,
        PartKind Kind,
        string Prototype,
        Angle? Rotation);

    private static readonly LayoutPart[] Layout = BuildLayout();

    private static LayoutPart[] BuildLayout()
    {
        const double baseRotation = 180;
        (Vector2i Position, PartKind Kind, string Prototype, double RotationOffset)[] scrubberQuarter =
        [
            (new(-1, -5), PartKind.Cardinal, "StructureDEMScrubber", 0),
            (new(1, -5), PartKind.Cardinal, "StructureDEMScrubberFlipped", 0),
            (new(3, -4), PartKind.Diagonal, "StructureDEMScrubberDiagonalFlipped", 0),
            (new(4, -3), PartKind.Diagonal, "StructureDEMScrubberDiagonal", 90)
        ];

        List<LayoutPart> layout = [];
        for (var quarter = 0; quarter < 4; quarter++)
        {
            var turn = Angle.FromDegrees(quarter * 90);
            for (var index = 0; index < scrubberQuarter.Length; index++)
            {
                var part = scrubberQuarter[index];
                var key = (DEMMachinePart) ((int) DEMMachinePart.Scrubber0 + quarter * scrubberQuarter.Length + index);
                layout.Add(new(
                    key,
                    part.Position.Rotate(turn),
                    part.Kind,
                    part.Prototype,
                    Angle.FromDegrees(baseRotation + part.RotationOffset + quarter * 90)));
            }

            layout.Add(new(
                (DEMMachinePart) ((int) DEMMachinePart.Laser0 + quarter),
                new Vector2i(-8, -8).Rotate(turn),
                PartKind.Laser,
                "StructureDEMPowerLaser",
                Angle.FromDegrees(baseRotation - 90 + quarter * 90)));
        }

        return layout.ToArray();
    }

    [Dependency] private MapSystem _map = default!;
    [Dependency] private MultipartMachineSystem _multipart = default!;
    [Dependency] private TransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<DEMConsoleComponent, LinkAttemptEvent>(OnLinkAttempt);
        SubscribeLocalEvent<DEMConsoleComponent, NewLinkEvent>(OnLinked);
        SubscribeLocalEvent<DEMConsoleComponent, PortDisconnectedEvent>(OnDisconnected);
        SubscribeLocalEvent<DEMAssemblyComponent, MultipartMachineAssemblyStateChanged>(OnAssemblyChanged);
        SubscribeLocalEvent<DEMAssemblyComponent, MapInitEvent>(OnAssemblyMapInit);
        SubscribeLocalEvent<DEMMachineComponent, MapInitEvent>(OnControlMapInit);
    }

    private void OnAssemblyMapInit(Entity<DEMAssemblyComponent> ent, ref MapInitEvent args)
    {
        RehydrateAssembly(ent);
    }

    private void OnControlMapInit(Entity<DEMMachineComponent> ent, ref MapInitEvent args)
    {
        if (ent.Comp.Origin is not { } origin)
            return;

        var assembly = EnsureComp<DEMAssemblyComponent>(origin);
        assembly.Control = ent.Owner;
        if (ent.Comp.Console is { } console)
            assembly.Console = console;

        Dirty(origin, assembly);
        RehydrateAssembly((origin, assembly));
    }

    private void RehydrateAssembly(Entity<DEMAssemblyComponent> ent)
    {
        if (ent.Comp.Control is not { } control ||
            !Exists(control) ||
            !TryComp<DEMMachineComponent>(control, out var machine))
        {
            return;
        }

        machine.Origin = ent.Owner;
        machine.Console = ent.Comp.Console;

        if (ent.Comp.Console is { } console && TryComp<DEMConsoleComponent>(console, out var consoleComp))
        {
            consoleComp.Machine = ent.Owner;
            Dirty(console, consoleComp);
        }

        if (TryComp<MultipartMachineComponent>(ent, out var multipart))
        {
            _multipart.Rescan((ent.Owner, multipart));
            machine.Constructed = _multipart.IsAssembled((ent.Owner, multipart));
        }
        else
        {
            machine.Constructed = false;
        }

        Dirty(control, machine);
    }

    private void OnLinkAttempt(Entity<DEMConsoleComponent> ent, ref LinkAttemptEvent args)
    {
        if (args.SourcePort != "DEMControl")
            return;

        if (args.SinkPort != "DEMMachine" ||
            ent.Comp.Machine is { } current && current != args.Sink ||
            TryComp<DEMAssemblyComponent>(args.Sink, out var assembly) && assembly.Console is { } console && console != ent.Owner ||
            !TryComp<DEMScrubberPartComponent>(args.Sink, out _) ||
            !Transform(args.Sink).Anchored ||
            TryComp<MultipartMachinePartComponent>(args.Sink, out var part) && part.Master is { } master && master != args.Sink)
        {
            args.Cancel();
        }
    }

    private void OnLinked(Entity<DEMConsoleComponent> ent, ref NewLinkEvent args)
    {
        if (args.SourcePort != "DEMControl" || args.SinkPort != "DEMMachine")
            return;

        var assembly = EnsureComp<DEMAssemblyComponent>(args.Sink);
        assembly.Console = ent.Owner;
        ent.Comp.Machine = args.Sink;
        Dirty(args.Sink, assembly);

        if (assembly.Control is { } control && TryComp<DEMMachineComponent>(control, out var machine))
        {
            machine.Console = ent.Owner;
            Dirty(control, machine);
        }

        Dirty(ent);
    }

    public bool TryForm(
        EntityUid origin,
        EntityUid? user,
        out string error,
        ICommonSession? previewSession = null)
    {
        if (!TryFindLayout(origin, out var parts, out error, out var missing))
        {
            if (previewSession != null && missing.Length > 0)
                RaiseNetworkEvent(new DEMFormationPreviewEvent(missing), previewSession);

            return false;
        }

        if (TryComp<DEMAssemblyComponent>(origin, out var formedAssembly) &&
            formedAssembly.Control is { } formedControl &&
            TryComp<DEMMachineComponent>(formedControl, out var formed) &&
            formed.Constructed &&
            TryComp<MultipartMachineComponent>(origin, out var existingMultipart))
        {
            _multipart.Rescan((origin, existingMultipart), user);
            if (_multipart.IsAssembled((origin, existingMultipart)))
            {
                error = string.Empty;
                return true;
            }
        }

        var multipart = EnsureComp<MultipartMachineComponent>(origin);
        Dictionary<Enum, MachinePart> machineParts = [];

        var originRotation = Transform(origin).LocalRotation.GetCardinalDir().ToAngle();
        var inverseRotation = new Angle(-originRotation.Theta);
        var centerOffset = Vector2i.Zero;
        var scrubberCount = 0;
        foreach (var (layout, part) in parts)
        {
            var partRotation = Transform(part).LocalRotation.GetCardinalDir().ToAngle();
            if (IsScrubber(layout.Key))
            {
                centerOffset += layout.Position;
                scrubberCount++;
            }

            machineParts.Add(layout.Key, new MachinePart
            {
                Component = layout.Kind == PartKind.Laser ? "DEMLaser" : "DEMScrubberPart",
                Offset = layout.Position.Rotate(inverseRotation),
                Rotation = new Angle(partRotation.Theta - originRotation.Theta),
                GhostProto = layout.Kind switch
                {
                    PartKind.Cardinal => "StructureDEMScrubber",
                    PartKind.Diagonal => "StructureDEMScrubberDiagonal",
                    _ => "StructureDEMPowerLaser"
                },
                Graph = layout.Kind == PartKind.Laser ? "DEMLaser" : "DEMScrubber",
                ExpectedNode = "completed"
            });
        }

        var assembly = EnsureComp<DEMAssemblyComponent>(origin);
        var control = assembly.Control ?? default;
        var createdControl = !Exists(control);
        if (createdControl)
        {
            var offset = new System.Numerics.Vector2(
                centerOffset.X / (float) scrubberCount,
                centerOffset.Y / (float) scrubberCount);
            control = Spawn("DEMControl", Transform(origin).Coordinates.Offset(offset));
            assembly.Control = control;
        }

        var machine = EnsureComp<DEMMachineComponent>(control);
        EnsureComp<DEMComponent>(control);
        EnsureComp<GlobalPvsComponent>(control);
        machine.Origin = origin;
        machine.Console = assembly.Console;
        Dirty(origin, assembly);
        Dirty(control, machine);

        machine.Constructed = _multipart.ConfigureParts((origin, multipart), machineParts, user);
        Dirty(control, machine);

        if (!machine.Constructed)
        {
            if (createdControl)
            {
                assembly.Control = null;
                Dirty(origin, assembly);
                QueueDel(control);
            }

            error = "The DEM layout was found, but multipart assembly failed.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TrySetPhase(EntityUid uid, DEMPhase phase, bool requireFormed, out string error)
    {
        if (!TryResolveControl(uid, out var control) || !TryComp<DEMComponent>(control, out var dem))
        {
            error = "The target is not a formed DEM.";
            return false;
        }

        if (requireFormed && (!TryComp<DEMMachineComponent>(control, out var machine) || !machine.Constructed))
        {
            error = "The DEM structure is not formed.";
            return false;
        }

        dem.State.CurrentPhase = phase;
        if (phase == DEMPhase.OFFLINE)
            dem.State.CoreVisible = false;
        Dirty(control, dem);
        error = string.Empty;
        return true;
    }

    public bool TrySpawn(EntityUid origin, EntityUid? user, out string error)
    {
        error = string.Empty;
        if (!TryComp<DEMScrubberPartComponent>(origin, out _) ||
            !TryComp<TransformComponent>(origin, out var xform) ||
            !xform.Anchored ||
            xform.GridUid is not { } gridUid ||
            !TryComp<MapGridComponent>(gridUid, out var grid))
        {
            error = "The specified origin must be an anchored DEM scrubber on a grid.";
            return false;
        }

        var originRotation = xform.LocalRotation.GetCardinalDir().ToAngle();
        LayoutPart? basis = null;
        var turn = Angle.Zero;
        foreach (var candidate in Layout)
        {
            if (candidate.Kind == PartKind.Laser ||
                candidate.Rotation is not { } candidateRotation ||
                !MatchesPrototype(origin, candidate))
            {
                continue;
            }

            basis = candidate;
            turn = new Angle(originRotation.Theta - candidateRotation.Theta);
            break;
        }

        if (basis == null)
        {
            error = "The specified entity is not a recognized DEM scrubber type.";
            return false;
        }

        var originTile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var center = originTile - RotateLayout(basis.Value, turn).Position;
        List<EntityUid> spawned = [];
        foreach (var baseLayout in Layout)
        {
            var layout = RotateLayout(baseLayout, turn);
            if (layout.Key == basis.Value.Key)
                continue;

            var coordinates = _map.GridTileToLocal(gridUid, grid, center + layout.Position);
            var part = Spawn(layout.Prototype, coordinates);
            _transform.SetLocalRotation(part, layout.Rotation ?? Angle.Zero);
            spawned.Add(part);
        }

        if (TryForm(origin, user, out error))
            return true;

        foreach (var part in spawned)
            QueueDel(part);

        if (string.IsNullOrEmpty(error))
            error = "The DEM structure spawned, but could not be formed.";

        return false;
    }

    public bool TryDescribeLayout(EntityUid origin, out string report, out string error)
    {
        report = string.Empty;
        if (!TryComp<DEMScrubberPartComponent>(origin, out _) ||
            !TryComp<TransformComponent>(origin, out var xform) ||
            !xform.Anchored || xform.GridUid is not { } gridUid ||
            !TryComp<MapGridComponent>(gridUid, out var grid))
        {
            error = "The selected entity must be an anchored DEM scrubber on a grid.";
            return false;
        }

        var originTile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var originRotation = xform.LocalRotation.GetCardinalDir().ToAngle();
        var bestScore = -1;
        var bestCenter = Vector2i.Zero;
        LayoutPart? originPart = null;
        var bestTurn = Angle.Zero;
        foreach (var candidate in Layout)
        {
            if (candidate.Kind == PartKind.Laser ||
                candidate.Rotation is not { } candidateRotation ||
                !MatchesPrototype(origin, candidate))
                continue;

            var turn = new Angle(originRotation.Theta - candidateRotation.Theta);
            var orientedCandidate = RotateLayout(candidate, turn);
            var center = originTile - orientedCandidate.Position;
            var score = 0;
            foreach (var baseLayout in Layout)
            {
                var layout = RotateLayout(baseLayout, turn);
                if (HasFamilyPart(gridUid, grid, center + layout.Position, layout.Kind))
                    score++;
            }

            if (score <= bestScore)
                continue;

            bestScore = score;
            bestCenter = center;
            originPart = orientedCandidate;
            bestTurn = turn;
        }

        if (originPart == null)
        {
            error = "Unable to locate a DEM circle from the selected scrubber.";
            return false;
        }

        var builder = new StringBuilder();
        var centerCoordinates = _transform.ToMapCoordinates(_map.GridTileToLocal(gridUid, grid, bestCenter));
        builder.AppendLine($"center=({centerCoordinates.X:N2}, {centerCoordinates.Y:N2}), matched={bestScore}/{Layout.Length}, origin={originPart.Value.Key}");
        foreach (var baseLayout in Layout)
        {
            var layout = RotateLayout(baseLayout, bestTurn);
            var tile = bestCenter + layout.Position;
            var coordinates = _transform.ToMapCoordinates(_map.GridTileToLocal(gridUid, grid, tile));
            if (!TryGetFamilyPart(gridUid, grid, tile, layout.Kind, out var part))
            {
                builder.AppendLine($"{layout.Key}: missing at ({coordinates.X:N2}, {coordinates.Y:N2})");
                continue;
            }

            var prototype = MetaData(part).EntityPrototype?.ID ?? "unknown";
            var rotation = Transform(part).LocalRotation.Degrees % 360;
            if (rotation < 0)
                rotation += 360;

            builder.AppendLine($"{layout.Key}: {prototype}, rotation={rotation:N0}, world=({coordinates.X:N2}, {coordinates.Y:N2})");
        }

        report = builder.ToString().TrimEnd();
        error = string.Empty;
        return true;
    }

    private void OnDisconnected(Entity<DEMConsoleComponent> ent, ref PortDisconnectedEvent args)
    {
        if (args.Port != "DEMControl" || ent.Comp.Machine is not { } machineUid)
            return;

        if (TryComp<DEMAssemblyComponent>(machineUid, out var assembly) && assembly.Console == ent.Owner)
        {
            assembly.Console = null;
            Dirty(machineUid, assembly);

            if (assembly.Control is { } control && TryComp<DEMMachineComponent>(control, out var machine))
            {
                machine.Console = null;
                Dirty(control, machine);
            }
        }

        ent.Comp.Machine = null;
        Dirty(ent);
    }

    private void OnAssemblyChanged(Entity<DEMAssemblyComponent> ent, ref MultipartMachineAssemblyStateChanged args)
    {
        if (ent.Comp.Control is not { } control ||
            !TryComp<DEMMachineComponent>(control, out var machine) ||
            !TryComp<DEMComponent>(control, out var dem) ||
            !TryComp<MultipartMachineComponent>(ent, out var multipart))
            return;

        if (args.IsAssembled)
        {
            machine.Constructed = true;
            machine.ScrubberBreached = false;
            Dirty(control, machine);
            return;
        }

        EntityUid? removedScrubber = null;
        foreach (var (key, part) in args.PartsRemoved)
        {
            if (key is DEMMachinePart machinePart && IsScrubber(machinePart))
            {
                removedScrubber = part;
                break;
            }
        }

        if (removedScrubber == null)
        {
            if (machine.ScrubberBreached && HasAllScrubbers((ent.Owner, multipart)))
            {
                machine.ScrubberBreached = false;
                Dirty(control, machine);
            }

            return;
        }

        if (dem.State.CurrentPhase == DEMPhase.OFFLINE)
        {
            ent.Comp.Control = null;
            Dirty(ent);
            QueueDel(control);
            return;
        }

        machine.ScrubberBreached = true;
        var ev = new DEMScrubberBreachEvent(removedScrubber.Value);
        RaiseLocalEvent(control, ref ev);

        if (HasAllScrubbers((ent.Owner, multipart)))
            machine.ScrubberBreached = false;

        Dirty(control, machine);
    }

    private bool TryFindLayout(
        EntityUid origin,
        out List<(LayoutPart Layout, EntityUid Entity)> parts,
        out string error,
        out DEMFormationPreviewPart[] previewParts)
    {
        parts = [];
        previewParts = [];
        if (!TryComp<DEMScrubberPartComponent>(origin, out _) ||
            !TryComp<TransformComponent>(origin, out var xform) ||
            !xform.Anchored || xform.GridUid is not { } gridUid ||
            !TryComp<MapGridComponent>(gridUid, out var grid))
        {
            error = "The selected entity must be an anchored DEM scrubber on a grid.";
            return false;
        }

        var originTile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var originRotation = xform.LocalRotation.GetCardinalDir().ToAngle();
        var bestGeometryScore = -1;
        var bestExactScore = -1;
        List<string> bestMissing = [];
        List<DEMFormationPreviewPart> bestPreview = [];
        foreach (var candidate in Layout)
        {
            if (candidate.Kind == PartKind.Laser ||
                candidate.Rotation is not { } candidateRotation ||
                !MatchesPrototype(origin, candidate))
                continue;

            var turn = new Angle(originRotation.Theta - candidateRotation.Theta);
            var orientedCandidate = RotateLayout(candidate, turn);
            var center = originTile - orientedCandidate.Position;
            parts.Clear();
            List<string> missing = [];
            List<DEMFormationPreviewPart> preview = [];
            var geometryScore = 0;
            foreach (var baseLayout in Layout)
            {
                var layout = RotateLayout(baseLayout, turn);
                var tile = center + layout.Position;
                if (HasFamilyPart(gridUid, grid, tile, layout.Kind))
                    geometryScore++;

                if (!TryGetPart(gridUid, grid, tile, origin, layout, candidate.Key == layout.Key, out var part))
                {
                    var coordinates = _transform.ToMapCoordinates(_map.GridTileToLocal(gridUid, grid, tile));
                    var direction = layout.Rotation is { } rotation ? $", rotation={rotation.Degrees:N0} degrees" : string.Empty;
                    missing.Add($"{PartName(layout.Kind)} at X={coordinates.X:N2}, Y={coordinates.Y:N2}{direction}");
                    preview.Add(new DEMFormationPreviewPart(layout.Prototype, coordinates, layout.Rotation ?? Angle.Zero));
                    continue;
                }

                parts.Add((layout with { Position = tile - originTile }, part));
            }

            if (missing.Count == 0)
            {
                error = string.Empty;
                return true;
            }

            if (geometryScore > bestGeometryScore ||
                geometryScore == bestGeometryScore && parts.Count > bestExactScore)
            {
                bestGeometryScore = geometryScore;
                bestExactScore = parts.Count;
                bestMissing = missing;
                bestPreview = preview;
            }
        }

        parts.Clear();
        previewParts = bestPreview.ToArray();
        error = bestGeometryScore < 0
            ? "The selected entity is not a recognized DEM scrubber type."
            : $"DEM structure is incomplete. Expected:\n- {string.Join("\n- ", bestMissing)}";
        return false;
    }

    private static string PartName(PartKind kind)
    {
        return kind switch
        {
            PartKind.Cardinal => "cardinal horizon scrubber",
            PartKind.Diagonal => "diagonal horizon scrubber",
            PartKind.Laser => "DEM power laser",
            _ => "DEM part"
        };
    }

    public bool TryResolveControl(EntityUid uid, out EntityUid control)
    {
        if (HasComp<DEMComponent>(uid))
        {
            control = uid;
            return true;
        }

        if (TryComp<DEMAssemblyComponent>(uid, out var assembly) &&
            assembly.Control is { } assemblyControl &&
            Exists(assemblyControl))
        {
            control = assemblyControl;
            return true;
        }

        control = default;
        return false;
    }

    private static LayoutPart RotateLayout(LayoutPart part, Angle turn)
    {
        return part with
        {
            Position = part.Position.Rotate(turn),
            Rotation = part.Rotation is { } rotation
                ? new Angle(rotation.Theta + turn.Theta)
                : null
        };
    }

    private bool TryGetPart(
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i tile,
        EntityUid origin,
        LayoutPart layout,
        bool mustBeOrigin,
        out EntityUid part)
    {
        foreach (var entity in _map.GetAnchoredEntities(gridUid, grid, tile))
        {
            if (mustBeOrigin && entity != origin ||
                TryComp<MultipartMachinePartComponent>(entity, out var linked) && linked.Master is { } master && master != origin ||
                !MatchesPrototype(entity, layout) ||
                layout.Rotation is { } rotation && !Transform(entity).LocalRotation.EqualsApprox(rotation.Theta))
            {
                continue;
            }

            part = entity;
            return true;
        }

        part = default;
        return false;
    }

    private bool MatchesPrototype(EntityUid entity, LayoutPart layout)
    {
        var prototype = MetaData(entity).EntityPrototype?.ID;
        return layout.Kind switch
        {
            PartKind.Cardinal or PartKind.Diagonal => HasComp<DEMScrubberPartComponent>(entity) && prototype == layout.Prototype,
            PartKind.Laser => HasComp<DEMLaserComponent>(entity) && prototype == layout.Prototype,
            _ => false
        };
    }

    private bool HasFamilyPart(EntityUid gridUid, MapGridComponent grid, Vector2i tile, PartKind kind)
    {
        return TryGetFamilyPart(gridUid, grid, tile, kind, out _);
    }

    private bool TryGetFamilyPart(
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i tile,
        PartKind kind,
        out EntityUid part)
    {
        foreach (var entity in _map.GetAnchoredEntities(gridUid, grid, tile))
        {
            if (MatchesFamily(entity, kind))
            {
                part = entity;
                return true;
            }
        }

        part = default;
        return false;
    }

    private bool MatchesFamily(EntityUid entity, PartKind kind)
    {
        var prototype = MetaData(entity).EntityPrototype?.ID;
        return kind switch
        {
            PartKind.Cardinal => HasComp<DEMScrubberPartComponent>(entity) &&
                prototype is "StructureDEMScrubber" or "StructureDEMScrubberFlipped",
            PartKind.Diagonal => HasComp<DEMScrubberPartComponent>(entity) &&
                prototype is "StructureDEMScrubberDiagonal" or "StructureDEMScrubberDiagonalFlipped",
            PartKind.Laser => HasComp<DEMLaserComponent>(entity) && prototype == "StructureDEMPowerLaser",
            _ => false
        };
    }

    private static bool IsScrubber(DEMMachinePart part)
    {
        return part is >= DEMMachinePart.Scrubber0 and <= DEMMachinePart.Scrubber15;
    }

    private bool HasAllScrubbers(Entity<MultipartMachineComponent> machine)
    {
        for (var i = (int) DEMMachinePart.Scrubber0; i <= (int) DEMMachinePart.Scrubber15; i++)
        {
            var part = (DEMMachinePart) i;
            if (!_multipart.HasPart(machine.AsNullable(), part))
                return false;
        }

        return true;
    }
}
