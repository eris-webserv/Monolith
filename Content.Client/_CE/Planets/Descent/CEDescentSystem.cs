/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Client.Shuttles;
using Content.Shared._CE.Planets.Descent;
using Content.Shared.Camera;
using Content.Shared.CCVar;
using Robust.Client.Player;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Random;

namespace Content.Client._CE.Planets.Descent;

/// <summary>
/// Client half of the descent sequence. All the visuals live in the render path
/// (ScalingViewport z-pass builder + CEPlanetOverlay) and read the shared progress
/// helpers; this subclass wires the predicted descent request to the client's
/// console component type.
/// </summary>
public sealed partial class CEDescentSystem : CESharedDescentSystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedAudioSystem _audio = default!;

    /// <summary>The drive discharging: a heavy machine power-down buzz.</summary>
    private static readonly SoundSpecifier StunSound =
        new SoundPathSpecifier("/Audio/_CE/Explosions/thrusterfail.ogg", AudioParams.Default.AddVolume(3));
    public override void Initialize()
    {
        base.Initialize();

        // Same predicted console message the server handles — running the shared
        // handler locally starts the spinup the moment the pilot confirms, so the
        // console ring doesn't sit dead through a request round-trip.
        SubscribeLocalEvent<ShuttleConsoleComponent, CEDescentRequestMessage>((uid, _, args) => OnDescentRequest(uid, args));

        // The drive got stunned (an engine was shot out mid-charge): everyone riding
        // the chain hears the discharge; the shake runs in FrameUpdate.
        SubscribeLocalEvent<CEDescentStunnedComponent, ComponentStartup>(OnStunnedStartup);
    }

    /// <summary>
    /// Server-driven, arrives with the replicated stun component so it fires exactly
    /// once per discharge (the server puts the component on every chain grid, so a
    /// plain "my grid" check covers docked riders too).
    /// </summary>
    private void OnStunnedStartup(Entity<CEDescentStunnedComponent> ent, ref ComponentStartup args)
    {
        if (_player.LocalEntity is not { } player
            || Transform(player).GridUid != ent.Owner)
        {
            return;
        }

        // Okay, maybe I'll be a bit merciful.
        var gain = _cfg.GetCVar(CCVars.AmbienceVolume);
        if (gain <= 0f)
            return;

        _audio.PlayGlobal(StunSound, Filter.Local(), false,
            AudioParams.Default.WithVolume(2f + SharedAudioSystem.GainToVolume(gain)));
    }

    /// <summary>
    /// Peak shake amplitude in tiles. Perceived shake scales linearly with this,
    /// hard-limited to 1 (the eye-offset range the recoil system was tuned for).
    /// </summary>
    private const float ShakeAmplitude = 1.2f;

    /// <summary>
    /// The discharge screenshake: while the local player's grid carries a fresh stun,
    /// throw the camera's recoil offset to a fresh random point every frame, tapering
    /// off quadratically over <see cref="CESharedDescentSystem.DischargeStunTime"/>.
    /// Deliberately NOT via <see cref="SharedCameraRecoilSystem.KickCamera"/>: that is
    /// a saturating accumulator (kicks are scaled by the remaining headroom under its
    /// 1-tile cap), so per-frame hammering pins it at the rim where further kicks
    /// multiply to zero — raising the kick constant past ~0.6/frame makes the camera
    /// freeze one tile off-centre instead of shaking harder.
    /// </summary>
    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_player.LocalEntity is not { } player
            || Transform(player).GridUid is not { } grid
            || !TryComp<CEDescentStunnedComponent>(grid, out var stunned))
        {
            return;
        }

        var elapsed = (float) (Timing.CurTime - stunned.Start).TotalSeconds;
        var duration = (float) DischargeStunTime.TotalSeconds;
        if (elapsed < 0f || elapsed >= duration)
            return;

        // Quadratic taper: full fury up front, dying twitches by the end.
        var taper = 1f - elapsed / duration;
        taper *= taper;

        if (!TryComp<CameraRecoilComponent>(player, out var recoil))
            return;

        // Shake is *motion*, so place the offset absolutely each frame; the recoil
        // system's restore loop cleans up whatever's left once the taper hits zero.
        var amplitude = Math.Min(ShakeAmplitude * taper * _cfg.GetCVar(CCVars.ScreenShakeIntensity), 1f);
        recoil.CurrentKick = _random.NextAngle().ToVec() * amplitude;
        recoil.LastKickTime = 0f;
    }
}
