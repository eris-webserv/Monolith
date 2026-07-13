/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._CE.Planets;
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
}
