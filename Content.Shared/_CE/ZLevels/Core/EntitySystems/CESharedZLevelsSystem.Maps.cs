/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Shared._CE.ZLevels.Core.Components;
using Content.Shared.Chasm;
using JetBrains.Annotations;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Shared._CE.ZLevels.Core.EntitySystems;

public abstract partial class CESharedZLevelsSystem
{
    /// <summary>
    /// Checks whether the map is in the zLevels network. If so, returns true and the current depth + Entity of the current zLevels network.
    /// </summary>
    [PublicAPI]
    public bool TryGetMapNetwork(EntityUid mapUid, out Entity<CEZMapNetworkComponent> zLevel)
    {
        zLevel = default;
        if (!_zMapQuery.TryComp(mapUid, out var zMap))
            return false;

        var networkUid = zMap.NetworkUid;
        if (TerminatingOrDeleted(networkUid))
        {
            Log.Warning($"Trying access to terminated z-network, map: {mapUid}, outdated network uid: {networkUid}");
            return false;
        }

        if (!_zNetworkQuery.TryComp(networkUid, out var zNetworkComponent))
        {
            Log.Warning($"Trying access to z-network without component??? WHY?! map: {mapUid}, network uid: {networkUid}");
            return false;
        }

        zLevel = (networkUid, zNetworkComponent);
        return true;
    }

    [PublicAPI]
    public bool TryMapOffset(Entity<CEZMapComponent?> entity, int offset, out Entity<CEZMapComponent> output)
    {
        return TryMapOffset(entity, offset, out output, out _);
    }

    [PublicAPI]
    public bool TryMapOffset(Entity<CEZMapComponent?> entity, int offset, out Entity<CEZMapComponent> output, [NotNullWhen(true)] out MapComponent? mapComponent)
    {
        output = default;
        mapComponent = null;

        if (MapOffset(entity, offset) is not { } result)
            return false;

        if (!TryComp(result, out mapComponent))
            return false;

        output = result;
        return true;
    }

    [PublicAPI]
    public bool TryMapUp(Entity<CEZMapComponent?> inputMapUid, out Entity<CEZMapComponent> aboveMapUid) =>
        TryMapOffset(inputMapUid, 1, out aboveMapUid);

    [PublicAPI]
    public bool TryMapDown(Entity<CEZMapComponent?> inputMapUid, out Entity<CEZMapComponent> belowMapUid) =>
        TryMapOffset(inputMapUid, -1, out belowMapUid);

    /// <summary>
    /// Collects maps that are up/down from the given map for rendering purposes
    /// (e.g. radar ghosting): the neighbouring z-levels of a stacked network out to
    /// <paramref name="maxLayers"/>, transit gaps touching this level, and — when
    /// called on a transit map itself — the levels and convoy layers bounding it.
    /// Dir is the signed layer offset (+1 = one level above, -2 = two below, ...);
    /// transit gaps use the sign of the side they sit on.
    /// </summary>
    [PublicAPI]
    public void GetAdjacentRenderMaps(EntityUid mapUid, List<(EntityUid MapUid, int Dir)> result, int maxLayers = 2)
    {
        // Transit map: bounded by its lower/upper z-levels plus convoy neighbours.
        if (_zTransitQuery.TryComp(mapUid, out var transit))
        {
            if (transit.UpperMap is { } upper && !TerminatingOrDeleted(upper))
                result.Add((upper, 1));
            if (transit.LowerMap is { } lower && !TerminatingOrDeleted(lower))
                result.Add((lower, -1));
            if (transit.TransitAbove is { } tAbove && !TerminatingOrDeleted(tAbove))
                result.Add((tAbove, 1));
            if (transit.TransitBelow is { } tBelow && !TerminatingOrDeleted(tBelow))
                result.Add((tBelow, -1));
            return;
        }

        if (!_zMapQuery.TryComp(mapUid, out var zMap))
            return;

        // Walk the stack in both directions out to maxLayers.
        var cursor = zMap.MapAbove;
        for (var i = 1; i <= maxLayers && cursor is { } above && !TerminatingOrDeleted(above); i++)
        {
            result.Add((above, i));
            cursor = _zMapQuery.TryComp(above, out var aboveMap) ? aboveMap.MapAbove : null;
        }

        cursor = zMap.MapBelow;
        for (var i = 1; i <= maxLayers && cursor is { } below && !TerminatingOrDeleted(below); i++)
        {
            result.Add((below, -i));
            cursor = _zMapQuery.TryComp(below, out var belowMap) ? belowMap.MapBelow : null;
        }

        // Transit maps holding grids currently between this level and a neighbour.
        var query = EntityQueryEnumerator<CEZTransitMapComponent>();
        while (query.MoveNext(out var transitUid, out var transitComp))
        {
            if (transitComp.LowerMap == mapUid)
                result.Add((transitUid, 1));
            else if (transitComp.UpperMap == mapUid)
                result.Add((transitUid, -1));
        }
    }

    /// <summary>
    /// Returns a list of all maps above the specified map. The closest map at the top is returned first.
    /// </summary>
    [PublicAPI]
    public List<EntityUid> GetAllMapsAbove(Entity<CEZMapComponent> entity)
    {
        if (!_zNetworkQuery.TryComp(entity.Comp.NetworkUid, out var network) || entity.Comp.Depth >= network.SortedMax)
            return new List<EntityUid>();

        var startIndex = entity.Comp.Depth < network.SortedMin ? 0 : entity.Comp.Depth - network.SortedMin + 1;
        var estimatedCount = network.SortedZLevels.Count - startIndex;
        var result = new List<EntityUid>(estimatedCount);
        var zLevels = network.SortedZLevels;

        for (var i = startIndex; i < zLevels.Count; i++)
        {
            var uid = zLevels[i];
            if (uid == EntityUid.Invalid)
                continue;

            if (_zMapQuery.HasComp(uid))
                result.Add(uid);
        }

        return result;
    }

    /// <summary>
    /// Returns a list of all maps above the specified map. The closest map at the top is returned first.
    /// </summary>
    [PublicAPI]
    public void GetAllMapsAbove(Entity<CEZMapComponent> entity, List<EntityUid> result)
    {
        result.Clear();

        if (!_zNetworkQuery.TryComp(entity.Comp.NetworkUid, out var network) || entity.Comp.Depth >= network.SortedMax)
            return;

        var startIndex = entity.Comp.Depth < network.SortedMin ? 0 : entity.Comp.Depth - network.SortedMin + 1;
        var zLevels = network.SortedZLevels;

        for (var i = startIndex; i < zLevels.Count; i++)
        {
            var uid = zLevels[i];

            if (uid.IsValid() && _zMapQuery.HasComp(uid))
                result.Add(uid);
        }
    }

    /// <summary>
    /// Returns a list of all maps below the specified map. The closest map at the bottom is returned first.
    /// </summary>
    [PublicAPI]
    public List<EntityUid> GetAllMapsBelow(Entity<CEZMapComponent> entity)
    {
        if (!_zNetworkQuery.TryComp(entity.Comp.NetworkUid, out var network) || entity.Comp.Depth <= network.SortedMin)
            return new List<EntityUid>();

        var endIndex = entity.Comp.Depth - network.SortedMin;
        var result = new List<EntityUid>(endIndex);
        var zLevels = network.SortedZLevels;

        // Iterate backwards for nearest-first ordering.
        for (var i = endIndex - 1; i >= 0; i--)
        {
            var uid = zLevels[i];
            if (uid == EntityUid.Invalid)
                continue;

            if (_zMapQuery.HasComp(uid))
                result.Add(uid);
        }

        return result;
    }

    private Entity<CEZMapComponent>? MapOffset(Entity<CEZMapComponent?> entity, int offset)
    {
        // Transit maps hang between two z-levels and are not members of any z-network
        // (they sit at fractional altitude and several can occupy the same gap), so
        // network walks would otherwise dead-end on them. Hop through their anchor
        // links instead: -1 resolves to the level below the gap, +1 to the level above,
        // and larger offsets continue through the real network from that anchor.
        if (offset != 0 && entity.Comp is null && !_zMapQuery.HasComp(entity) &&
            TryComp<CEZTransitMapComponent>(entity, out var transit))
        {
            var anchor = offset < 0 ? transit.LowerMap : transit.UpperMap;
            if (anchor is not { } anchorUid || TerminatingOrDeleted(anchorUid))
                return null;

            var remaining = offset < 0 ? offset + 1 : offset - 1;
            if (remaining == 0)
            {
                return _zMapQuery.TryComp(anchorUid, out var anchorComp)
                    ? (anchorUid, anchorComp)
                    : null;
            }

            return MapOffset(new Entity<CEZMapComponent?>(anchorUid, null), remaining);
        }

        if (!Resolve(entity, ref entity.Comp, false))
            return null;

        var target = offset switch
        {
            1 => entity.Comp.MapAbove,
            -1 => entity.Comp.MapBelow,
            _ => null,
        };

        if (target is not null && _zMapQuery.TryComp(target.Value, out var component))
            return (target.Value, component);

        if (!_zNetworkQuery.TryComp(entity.Comp.NetworkUid, out var network))
            return null;

        if (!network.ZLevels.TryGetValue(entity.Comp.Depth + offset, out var targetId))
            return null;

        return _zMapQuery.TryComp(targetId, out var comp)
            ? (targetId.Value, comp)
            : null;
    }
}
