/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Server._CE.ZLevels.Core;
using Content.Server.Administration;
using Content.Shared._CE.Planets;
using Content.Shared._CE.ZLevels.Mapping.Prototypes;
using Content.Shared.Administration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Toolshed;
using Robust.Shared.Toolshed.Errors;
using Robust.Shared.Utility;

namespace Content.Server._CE.Planets;

/// <summary>
/// Spawns a planet prototype at a coordinate on a map with a freshly loaded z-network already
/// attached, so the planet is immediately descendable. Usage:
/// <c>cezspawnplanet CEPlanetNauvis &lt;mapId&gt; &lt;x&gt; &lt;y&gt; [zMap]</c>. The zMap arg is a
/// <see cref="CEZLevelMapPrototype"/> id and defaults to <c>Grasslands</c> when omitted. Returns
/// the spawned planet entity so it can be piped into further toolshed commands, same as
/// <c>cespawnplanet</c>.
/// </summary>
[ToolshedCommand(Name = "cezspawnplanet"), AdminCommand(AdminFlags.Spawn)]
public sealed class CEZSpawnPlanetCommand : ToolshedCommand
{
    [Dependency] private IPrototypeManager _proto = default!;

    public const string DefaultZMap = "Grasslands";

    [CommandImplementation]
    public EntityUid SpawnPlanet(
        IInvocationContext ctx,
        [CommandArgument(typeof(CEPlanetProtoParser))] EntProtoId proto,
        [CommandArgument(typeof(CEMapUidParser))] EntityUid map,
        float x,
        float y,
        [CommandArgument(typeof(CEZMapProtoParser))] ProtoId<CEZLevelMapPrototype> zMap = default)
    {
        var zLevels = GetSys<CEZLevelsSystem>();
        var mapLoader = GetSys<MapLoaderSystem>();
        var mapSys = GetSys<SharedMapSystem>();
        var meta = GetSys<MetaDataSystem>();

        // Optional arg: toolshed passes default(ProtoId) when omitted.
        var zMapId = string.IsNullOrEmpty(zMap.Id) ? DefaultZMap : zMap.Id;
        if (!_proto.TryIndex<CEZLevelMapPrototype>(zMapId, out var indexed))
        {
            ctx.ReportError(new UnknownZMapPrototype(zMapId));
            return EntityUid.Invalid;
        }

        // Build the z-network before spawning the planet so a failed map load leaves nothing behind.
        var network = zLevels.CreateMapNetwork(indexed.Components);
        meta.SetEntityName(network, $"Planet z-Network: {proto.Id} ({indexed.ID})");

        var maps = new Dictionary<EntityUid, int>();
        var depth = 0;
        foreach (var path in indexed.Maps)
        {
            if (!mapLoader.TryLoadMap(path, out var mapEnt, out _))
            {
                ctx.ReportError(new ZMapLoadFailed(path, depth));
                Cleanup();
                return EntityUid.Invalid;
            }

            mapSys.InitializeMap(mapEnt.Value.Comp.MapId);
            meta.SetEntityName(mapEnt.Value, $"{proto.Id} ({indexed.ID}) [{depth}]");
            maps.Add(mapEnt.Value, depth);
            depth++;
        }

        if (!zLevels.TryAddMapsIntoNetwork(network, maps))
        {
            ctx.ReportError(new ZNetworkBuildFailed(indexed.ID));
            Cleanup();
            return EntityUid.Invalid;
        }

        var planet = Spawn(proto.Id, new EntityCoordinates(map, x, y));
        var planetComp = EntityManager.GetComponent<CEPlanetComponent>(planet);
        planetComp.Network = network;
        EntityManager.Dirty(planet, planetComp);

        return planet;

        void Cleanup()
        {
            foreach (var (mapUid, _) in maps)
            {
                EntityManager.DeleteEntity(mapUid);
            }

            EntityManager.DeleteEntity(network);
        }
    }
}

public sealed class UnknownZMapPrototype(string proto) : ConError
{
    public override FormattedMessage DescribeInner()
    {
        return FormattedMessage.FromUnformatted($"Unknown zMap prototype {proto}");
    }
}

public sealed class ZMapLoadFailed(ResPath path, int depth) : ConError
{
    public override FormattedMessage DescribeInner()
    {
        return FormattedMessage.FromUnformatted($"Failed to load zNetwork map (depth {depth}): {path}");
    }
}

public sealed class ZNetworkBuildFailed(string proto) : ConError
{
    public override FormattedMessage DescribeInner()
    {
        return FormattedMessage.FromUnformatted($"Failed to assemble zNetwork from {proto} maps (see server log)");
    }
}
