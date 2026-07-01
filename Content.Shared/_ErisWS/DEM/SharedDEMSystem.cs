using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.Shared._ErisWS.DEM;

public sealed partial class SharedDEMSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_timing.IsFirstTimePredicted) // DO NOT RUN THE TICK FUNCTION MULTIPLE TIMES IN A TICK YOU WILL FUCK UP REALITY AND SPACETIME!!!
            return;

        var query = EntityQueryEnumerator<DEMComponent>();
        while (query.MoveNext(out var uid, out var dem))
        {
            Tick((uid, dem), frameTime);
        }
    }

    // Router, this is YOUR problem.
    private void Tick(Entity<DEMComponent> dem, float frameTime)
    {
    }
}
