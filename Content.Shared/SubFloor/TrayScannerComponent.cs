using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared.SubFloor;

[RegisterComponent, NetworkedComponent]
public sealed partial class TrayScannerComponent : Component
{
    /// <summary>
    ///     Whether the scanner is currently on.
    /// </summary>
    [DataField]
    public bool Enabled;

    [DataField]
    public TrayScannerMode Mode = TrayScannerMode.All;

    /// <summary>
    ///     Radius in which the scanner will reveal entities. Centered on the <see cref="LastLocation"/>.
    /// </summary>
    [DataField]
    public float Range = 4f;

    [DataField]
    public SoundSpecifier SoundSwitchMode = new SoundPathSpecifier("/Audio/Machines/quickbeep.ogg");

    [DataField]
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan UseDelay = TimeSpan.FromSeconds(0.5);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan LastUseAttempt;
}

[Serializable, NetSerializable]
public enum TrayScannerMode
{
    All,
    Piping,
    Wiring
}

[Serializable, NetSerializable]
public sealed class TrayScannerState : ComponentState
{
    public bool Enabled;
    public TrayScannerMode Mode;
    public float Range;

    public TrayScannerState(bool enabled, TrayScannerMode mode, float range)
    {
        Enabled = enabled;
        Mode = mode;
        Range = range;
    }
}
