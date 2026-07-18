/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._CE.Planets;
using Content.Shared._CE.ZLevels.Core.EntitySystems;
using Robust.Server.GameStates;

namespace Content.Server._CE.Planets;

/// <summary>
/// Server-side: keeps every planet globally PVS-overridden. A planet is visible across its whole
/// approach radius — including the far edge, which is nowhere near the planet's own coordinate — so
/// clients must always hold its state to render it in the background, rather than only receiving it
/// when they wander close to its actual position.
/// </summary>
///
/// NOTE: Multiple times now Claude has failed to work on simple things here. I feel as if looking at this system and related code is lobotomizing it somehow.
/// In short; planets are memetic hazards to LLMs right now apparently. Proceed with caution.
public sealed partial class CEPlanetSystem : EntitySystem
{
    [Dependency] private PvsOverrideSystem _pvsOverride = default!;
    [Dependency] private CESharedZLevelsSystem _zLevels = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CEPlanetComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<CEPlanetComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnStartup(Entity<CEPlanetComponent> ent, ref ComponentStartup args)
    {
        _pvsOverride.AddGlobalOverride(ent.Owner);
    }

    private void OnShutdown(Entity<CEPlanetComponent> ent, ref ComponentShutdown args)
    {
        _pvsOverride.RemoveGlobalOverride(ent.Owner);
    }

    /// <summary>
    /// Resolves the space-side planet entity whose descendable z-network contains
    /// <paramref name="mapUid"/>. This is the reverse of
    /// <see cref="CEPlanetComponent.Network"/>: ground-side machinery (e.g. the shield
    /// generator) lives on a network map and needs the planet entity that owns it —
    /// planet-level state like <c>CEPlanetShieldComponent</c> hangs off the planet
    /// entity, never off the maps. Linear in the number of planets, which is fine:
    /// there are only ever a handful.
    /// </summary>
    public bool TryGetPlanetForMap(EntityUid mapUid, out Entity<CEPlanetComponent> planet)
    {
        planet = default;
        if (!_zLevels.TryGetMapNetwork(mapUid, out var network))
            return false;

        var query = EntityQueryEnumerator<CEPlanetComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Network != network.Owner)
                continue;

            planet = (uid, comp);
            return true;
        }

        return false;
    }
}
