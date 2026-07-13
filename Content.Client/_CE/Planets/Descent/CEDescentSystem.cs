/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._CE.Planets.Descent;

namespace Content.Client._CE.Planets.Descent;

/// <summary>
/// Client half of the descent sequence. All the visuals live in the render path
/// (ScalingViewport z-pass builder + CEPlanetOverlay) and read the shared progress
/// helpers; this subclass just gets the shared system registered client-side.
/// </summary>
public sealed class CEDescentSystem : CESharedDescentSystem
{
}
