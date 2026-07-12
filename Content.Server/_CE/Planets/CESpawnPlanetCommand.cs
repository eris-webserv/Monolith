/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Toolshed;

namespace Content.Server._CE.Planets;

/// <summary>
/// Spawns a planet prototype at a coordinate on a map and returns the spawned entity, so it can be
/// piped into further toolshed commands (e.g. to assign its z-network). Usage:
/// <c>cespawnplanet CEPlanetNauvis &lt;mapId&gt; &lt;x&gt; &lt;y&gt;</c>. The prototype arg
/// autocompletes only planets (CEPlanet component); the map arg takes a map id like <c>savemap</c>.
/// </summary>
[ToolshedCommand(Name = "cespawnplanet"), AdminCommand(AdminFlags.Spawn)]
public sealed class CESpawnPlanetCommand : ToolshedCommand
{
    [CommandImplementation]
    public EntityUid SpawnPlanet(
        [CommandArgument(typeof(CEPlanetProtoParser))] EntProtoId proto,
        [CommandArgument(typeof(CEMapUidParser))] EntityUid map,
        float x,
        float y)
    {
        return Spawn(proto.Id, new EntityCoordinates(map, x, y));
    }
}
