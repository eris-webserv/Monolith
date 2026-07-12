/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Server._CE.ZLevels.Core;
using Content.Server.Administration;
using Content.Shared._CE.ZLevels.Core.Components;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Map.Components;

namespace Content.Server._CE.Planets;

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class CEPlanetDescendCommand : LocalizedEntityCommands
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private CEZLevelsSystem _zLevels = default!;

    public override string Command => "ceplanet-descend";
    public override string Description => "TEST: Drop a grid into a z-map network (testing descent sequence)";

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        switch (args.Length)
        {
            case 1:
                {
                    var options = new List<CompletionOption>();
                    var query = _entities.EntityQueryEnumerator<MapGridComponent, MetaDataComponent>();
                    while (query.MoveNext(out var uid, out _, out var meta))
                    {
                        options.Add(new CompletionOption(_entities.GetNetEntity(uid).ToString(), meta.EntityName));
                    }
                    return CompletionResult.FromHintOptions(options, "grid net entity");
                }
            case 2:
                {
                    var options = new List<CompletionOption>();
                    var query = _entities.EntityQueryEnumerator<CEZMapNetworkComponent, MetaDataComponent>();
                    while (query.MoveNext(out var uid, out _, out var meta))
                    {
                        options.Add(new CompletionOption(_entities.GetNetEntity(uid).ToString(), meta.EntityName));
                    }
                    return CompletionResult.FromHintOptions(options, "zNetwork net entity");
                }
        }

        return CompletionResult.Empty;
    }

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError("Usage: ceplanet-descend <grid> <zNetwork>");
            return;
        }

        if (!NetEntity.TryParse(args[0], out var gridNet) ||
            !_entities.TryGetEntity(gridNet, out var gridUid) ||
            !_entities.TryGetComponent(gridUid, out MapGridComponent? grid))
        {
            shell.WriteError($"{args[0]} is not a grid entity.");
            return;
        }

        if (!NetEntity.TryParse(args[1], out var netNet) ||
            !_entities.TryGetEntity(netNet, out var netUid) ||
            !_entities.TryGetComponent(netUid, out CEZMapNetworkComponent? networkComp))
        {
            shell.WriteError($"{args[1]} is not a zNetwork entity.");
            return;
        }

        // Enter at the very top of the network's stack — descending from orbit.
        Entity<CEZMapComponent>? topMap = null;
        for (var i = networkComp.SortedZLevels.Count - 1; i >= 0; i--)
        {
            var level = networkComp.SortedZLevels[i];
            if (_entities.TryGetComponent(level, out CEZMapComponent? zMap))
            {
                topMap = (level, zMap);
                break;
            }
        }

        if (topMap == null)
        {
            shell.WriteError("That network has no valid z-levels.");
            return;
        }

        if (!_zLevels.TryEnterDescent((gridUid.Value, grid), topMap.Value))
        {
            shell.WriteError("Failed to start descent (grid already in transit, or entry blocked).");
            return;
        }

        shell.WriteLine($"Descent started: {_entities.ToPrettyString(gridUid.Value)} into {_entities.ToPrettyString(netUid.Value)}.");
    }
}
