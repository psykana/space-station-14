using Robust.Shared.Serialization;

namespace Content.Shared.ActionBlocker;

/// <summary>
/// What a caller requires of a mob, and which conditions it tolerates.
/// Consumed by <see cref="MobCapabilitiesSystem.CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/>.
/// </summary>
[DataDefinition, Serializable, NetSerializable]
public partial record struct InteractionRequirements()
{
    /// <summary>
    /// Every capability the act needs. The act is blocked if any of them is.
    /// </summary>
    [DataField]
    public MobCapability Required = MobCapability.Interact;

    /// <summary>
    /// Conditions this caller forgives.
    /// </summary>
    [DataField]
    public MobCondition Ignoring = MobCondition.None;

    // Note that default(InteractionRequirements) skips the field initializers and requires nothing (REQ-2):
    // build requirements with new(), an initializer or one of these presets.

    /// <summary>
    /// Requires <see cref="MobCapability.Interact"/>, the common case.
    /// </summary>
    public static readonly InteractionRequirements Interact = new();

    /// <summary>
    /// Requires <see cref="MobCapability.Conscious"/>: the act needs a mind, not hands.
    /// </summary>
    public static readonly InteractionRequirements Conscious = new() { Required = MobCapability.Conscious };
}
