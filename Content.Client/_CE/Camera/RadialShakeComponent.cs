namespace Content.Client._CE.Camera;

/// <summary>
/// Client-only: the local player's camera is being radially shaken.
/// Added/refreshed by <see cref="RadialShakeSystem"/> (usually off a networked
/// <c>RadialShakeEvent</c>); the system's FrameUpdate drives the actual motion
/// and removes the component once the envelope runs out.
/// </summary>
[RegisterComponent]
public sealed partial class RadialShakeComponent : Component
{
    /// <summary>When the current shake envelope started.</summary>
    public TimeSpan Start;

    /// <summary>Envelope length in seconds.</summary>
    public float Duration;

    /// <summary>Peak amplitude in tiles at the start of the envelope.</summary>
    public float Amplitude;
}
