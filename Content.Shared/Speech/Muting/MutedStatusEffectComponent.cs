using Robust.Shared.GameStates;

namespace Content.Shared.Speech.Muting;

/// <summary>
/// Marks a status effect that prevents speaking, screaming, and vocal emotes.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class MutedStatusEffectComponent : Component
{
    /// <summary>
    /// Why the mob can't speak, shown when its speech is blocked by this effect.
    /// </summary>
    [DataField, AutoNetworkedField]
    public LocId SpeakPopup = "speech-muted";

    /// <summary>
    /// Popup shown when screaming is blocked.
    /// </summary>
    [DataField, AutoNetworkedField]
    public LocId ActionPopup = "speech-muted";
}
