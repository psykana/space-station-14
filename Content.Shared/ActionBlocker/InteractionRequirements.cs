using Robust.Shared.Serialization;

namespace Content.Shared.ActionBlocker;

/// <summary>
/// What a caller requires of a mob, and which conditions it tolerates.
/// Consumed by <see cref="ActionBlockerSystem.CanInteract(EntityUid, EntityUid?, in InteractionRequirements)"/>.
/// </summary>
[DataDefinition, Serializable, NetSerializable]
public partial record struct InteractionRequirements()
{
    /// <summary>
    /// The action being attempted.
    /// </summary>
    [DataField]
    public MobAction Action = MobAction.Interact;

    /// <summary>
    /// Conditions this caller forgives.
    /// </summary>
    [DataField]
    public MobCondition Ignoring = MobCondition.None;

    /// <summary>
    /// Whether the mob must also have <see cref="Content.Shared.Interaction.Components.ComplexInteractionComponent"/>.
    /// </summary>
    [DataField]
    public bool NeedComplex = false;
}
