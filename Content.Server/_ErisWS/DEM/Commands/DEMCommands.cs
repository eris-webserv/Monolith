using Content.Server.Administration;
using Content.Shared._ErisWS.DEM;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ErisWS.DEM.Commands;

public abstract class DEMCommand : IConsoleCommand
{
    [Dependency] protected readonly IEntityManager Entities = default!;

    public abstract string Command { get; }
    public abstract string Description { get; }
    public string Help => $"{Command} <uid>";

    public abstract void Execute(IConsoleShell shell, string argStr, string[] args);

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length == 1
            ? CompletionResult.FromHintOptions(CompletionHelper.Components<DEMScrubberPartComponent>(args[0]), "<uid>")
            : CompletionResult.Empty;
    }

    protected bool TryGetTarget(IConsoleShell shell, string[] args, out EntityUid uid)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            uid = default;
            return false;
        }

        if (!NetEntity.TryParse(args[0], out var netEntity) || !Entities.TryGetEntity(netEntity, out var target))
        {
            shell.WriteError($"Unable to find entity '{args[0]}'.");
            uid = default;
            return false;
        }

        uid = target.Value;
        return true;
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class DEMStopCommand : DEMCommand
{
    public override string Command => "demstop";
    public override string Description => "Immediately stops a DEM.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!TryGetTarget(shell, args, out var uid))
            return;

        if (!Entities.System<DEMMachineSystem>().TrySetPhase(uid, DEMPhase.OFFLINE, false, out var error))
            shell.WriteError(error);
        else
            shell.WriteLine($"Stopped DEM {Entities.GetNetEntity(uid)}.");
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class DEMStartCommand : DEMCommand
{
    public override string Command => "demstart";
    public override string Description => "Immediately puts a formed DEM online.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!TryGetTarget(shell, args, out var uid))
            return;

        if (!Entities.System<DEMMachineSystem>().TrySetPhase(uid, DEMPhase.ONLINE, true, out var error))
            shell.WriteError(error);
        else
            shell.WriteLine($"Started DEM {Entities.GetNetEntity(uid)}.");
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class DEMFormCommand : DEMCommand
{
    public override string Command => "demform";
    public override string Description => "Attempts to form a DEM using a scrubber as its origin.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!TryGetTarget(shell, args, out var uid))
            return;

        if (!Entities.System<DEMMachineSystem>().TryForm(uid, shell.Player?.AttachedEntity, out var error, shell.Player))
            shell.WriteError(error);
        else
            shell.WriteLine($"Formed DEM {Entities.GetNetEntity(uid)}.");
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class DEMStatusCommand : DEMCommand
{
    public override string Command => "demstatus";
    public override string Description => "Displays the state of a DEM or prospective origin.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!TryGetTarget(shell, args, out var uid))
            return;

        var system = Entities.System<DEMMachineSystem>();
        if (!system.TryResolveControl(uid, out var control) ||
            !Entities.TryGetComponent<DEMMachineComponent>(control, out var machine))
        {
            if (Entities.HasComponent<DEMScrubberPartComponent>(uid))
                shell.WriteLine($"{Entities.GetNetEntity(uid)} is an unformed DEM scrubber.");
            else
                shell.WriteError("The target is not a DEM scrubber.");
            return;
        }

        var phase = Entities.TryGetComponent<DEMComponent>(control, out var dem)
            ? dem.State.CurrentPhase.ToString()
            : "UNFORMED";
        var console = machine.Console is { } consoleUid ? Entities.GetNetEntity(consoleUid).ToString() : "none";
        shell.WriteLine($"Control: {Entities.GetNetEntity(control)}, phase: {phase}, formed: {machine.Constructed}, breached: {machine.ScrubberBreached}, console: {console}.");
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class DEMScanCommand : DEMCommand
{
    public override string Command => "demscan";
    public override string Description => "Dumps the observed geometry, prototypes, and rotations of a DEM layout.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!TryGetTarget(shell, args, out var uid))
            return;

        if (!Entities.System<DEMMachineSystem>().TryDescribeLayout(uid, out var report, out var error))
            shell.WriteError(error);
        else
            shell.WriteLine(report);
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class DEMSpawnCommand : DEMCommand
{
    public override string Command => "demspawn";
    public override string Description => "Completes and forms a DEM from a specified scrubber section.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!TryGetTarget(shell, args, out var origin))
            return;

        if (!Entities.System<DEMMachineSystem>().TrySpawn(origin, shell.Player?.AttachedEntity, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine($"Completed and formed DEM {Entities.GetNetEntity(origin)}.");
    }
}
