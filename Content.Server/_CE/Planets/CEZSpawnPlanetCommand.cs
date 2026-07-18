/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Server._CE.Planets.Caves;
using Content.Server._CE.ZLevels.Core;
using Content.Server._DV.Planet;
using Content.Server.Administration;
using Content.Shared._CE.Planets;
using Content.Shared._CE.ZLevels.Core.Components;
using Content.Shared.Administration;
using Content.Shared.Gravity;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Toolshed;
using Robust.Shared.Toolshed.Errors;
using Robust.Shared.Utility;

namespace Content.Server._CE.Planets;

/// <summary>
/// Spawns a planet entity at a coordinate on a map with a fully runtime-generated z-network
/// attached, so the planet is immediately descendable. The ground layer (depth 0) is
/// biome-generated from a <see cref="CEZPlanetPrototype"/>'s planet prototype and the sky layers
/// above it are empty maps created at runtime - no saved map files involved. Usage:
/// <c>cezspawnplanet CEPlanetNauvis &lt;mapId&gt; &lt;x&gt; &lt;y&gt; [zPlanet]</c>. The zPlanet
/// arg is a <see cref="CEZPlanetPrototype"/> id and defaults to <c>Grasslands</c> when omitted.
/// Returns the spawned planet entity so it can be piped into further toolshed commands, same as
/// <c>cespawnplanet</c>.
/// </summary>
[ToolshedCommand(Name = "cezspawnplanet"), AdminCommand(AdminFlags.Spawn)]
public sealed partial class CEZSpawnPlanetCommand : ToolshedCommand
{
    [Dependency] private IPrototypeManager _proto = default!;

    public const string DefaultZPlanet = "Grasslands";

    [CommandImplementation]
    public EntityUid SpawnPlanet(
        IInvocationContext ctx,
        [CommandArgument(typeof(CEPlanetProtoParser))] EntProtoId proto,
        [CommandArgument(typeof(CEMapUidParser))] EntityUid map,
        float x,
        float y,
        [CommandArgument(typeof(CEZPlanetProtoParser))] ProtoId<CEZPlanetPrototype> zPlanet = default)
    {
        var zLevels = GetSys<CEZLevelsSystem>();
        var planetSys = GetSys<PlanetSystem>();
        var mapSys = GetSys<SharedMapSystem>();
        var meta = GetSys<MetaDataSystem>();

        // Optional arg: toolshed passes default(ProtoId) when omitted.
        var zPlanetId = string.IsNullOrEmpty(zPlanet.Id) ? DefaultZPlanet : zPlanet.Id;
        if (!_proto.TryIndex<CEZPlanetPrototype>(zPlanetId, out var indexed))
        {
            ctx.ReportError(new UnknownZPlanetPrototype(zPlanetId));
            return EntityUid.Invalid;
        }

        if (indexed.Layers < 1)
        {
            ctx.ReportError(new InvalidZPlanetLayerCount(zPlanetId, indexed.Layers));
            return EntityUid.Invalid;
        }

        // Build the z-network before spawning the planet so a failure leaves nothing behind.
        var network = zLevels.CreateMapNetwork(indexed.NetworkComponents);
        meta.SetEntityName(network, $"Planet z-Network: {proto.Id} ({indexed.ID})");

        var maps = new Dictionary<EntityUid, int>();

        // Ground layer (depth 0): biome-generated from the planet prototype.
        var ground = planetSys.SpawnPlanet(indexed.Planet);
        EntityManager.EnsureComponent<CEZGroundLayerComponent>(ground);
        maps.Add(ground, 0);

        // Sky layers (depth 1+): empty maps created at runtime. Atmosphere, roof and lighting
        // come from the network's shared components once the maps are added to it.
        for (var depth = 1; depth < indexed.Layers; depth++)
        {
            var sky = mapSys.CreateMap(out _);
            meta.SetEntityName(sky, $"{proto.Id} ({indexed.ID}) sky [{depth}]");

            var gravity = EntityManager.EnsureComponent<GravityComponent>(sky);
            gravity.Enabled = true;
            gravity.Inherent = true;
            EntityManager.Dirty(sky, gravity);

            // The clouds layer is just a sky layer with the cloud visuals marker.
            if (depth == indexed.CloudsIndex)
                EntityManager.EnsureComponent<CEZCloudLayerComponent>(sky);

            maps.Add(sky, depth);
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

public sealed class UnknownZPlanetPrototype(string proto) : ConError
{
    public override FormattedMessage DescribeInner()
    {
        return FormattedMessage.FromUnformatted($"Unknown cezPlanet prototype {proto}");
    }
}

public sealed class InvalidZPlanetLayerCount(string proto, int layers) : ConError
{
    public override FormattedMessage DescribeInner()
    {
        return FormattedMessage.FromUnformatted($"cezPlanet {proto} has invalid layer count {layers} (must be >= 1)");
    }
}

public sealed class ZNetworkBuildFailed(string proto) : ConError
{
    public override FormattedMessage DescribeInner()
    {
        return FormattedMessage.FromUnformatted($"Failed to assemble zNetwork from {proto} maps (see server log)");
    }
}
