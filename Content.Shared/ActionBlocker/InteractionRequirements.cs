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
}
