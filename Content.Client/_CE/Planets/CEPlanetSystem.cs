/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Robust.Client.Graphics;

namespace Content.Client._CE.Planets;

/// <summary>
/// Client-side: registers the <see cref="CEPlanetOverlay"/> that renders planet bodies into the
/// parallax background.
/// </summary>
public sealed partial class CEPlanetSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlay = default!;

    public override void Initialize()
    {
        base.Initialize();
        _overlay.AddOverlay(new CEPlanetOverlay());
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlay.RemoveOverlay<CEPlanetOverlay>();
    }
}
