using Content.Shared._Crescent.ShipShields;
using Content.Shared.CCVar;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Player;

namespace Content.Client._Crescent.ShipShields;

/// <summary>
/// pzn: just here to handle ShipShieldSoundEffects (yes, that's it.)
/// </summary>
public sealed partial class ShipShieldAudioSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private SharedAudioSystem _audio = default!;

    /// <summary>Ambience slider as a dB offset; float.NegativeInfinity when muted.</summary>
    private float _gainVolume;
    private bool _muted;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<ShipShieldSoundEvent>(OnShieldSound);
        Subs.CVar(_cfg, CCVars.AmbienceVolume, OnAmbienceChanged, true);
    }

    private void OnAmbienceChanged(float gain)
    {
        _muted = gain <= 0f;
        _gainVolume = SharedAudioSystem.GainToVolume(gain);
    }

    private void OnShieldSound(ShipShieldSoundEvent ev)
    {
        if (_muted)
            return;

        var audioParams = (ev.AudioParams ?? AudioParams.Default).AddVolume(_gainVolume);
        _audio.PlayGlobal(ev.Specifier, Filter.Local(), false, audioParams);
    }
}
