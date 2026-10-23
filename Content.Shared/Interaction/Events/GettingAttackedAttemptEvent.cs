namespace Content.Shared.Interaction.Events;

/// <summary>
/// Raised directed on the target entity when being attacked.
/// </summary>
[ByRefEvent]
[Obsolete("Subscribe to MobActionTargetAttemptEvent and check for MobCapability.Attack. This event will be removed in a future release.")]
public record struct GettingAttackedAttemptEvent(EntityUid Attacker, EntityUid? Weapon, bool Disarm, bool Cancelled = false);
