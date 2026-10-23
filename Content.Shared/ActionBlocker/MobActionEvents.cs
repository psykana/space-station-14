namespace Content.Shared.ActionBlocker;

/// <summary>
/// Raised on the user by <see cref="MobCapabilitiesSystem.CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/>
/// for rules about a particular user and target, like pacification or the station AI whitelist.
/// Handlers test the <see cref="Required"/> bits they care about, and only ever cancel.
/// </summary>
[ByRefEvent]
public record struct MobActionAttemptEvent(EntityUid User, MobCapability Required, EntityUid? Target, EntityUid? Used)
{
    public bool Cancelled;

    /// <summary>
    /// Optional localized explanation, for consumers that report failures.
    /// </summary>
    public string? Reason;
}

/// <summary>
/// Raised on the target (and on the used item, when <see cref="MobCapability.UseHeld"/> or
/// <see cref="MobCapability.Throw"/> is required) by
/// <see cref="MobCapabilitiesSystem.CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/>,
/// for things that can't be acted on at all, like virtual items or things hidden under the floor.
/// Handlers test the <see cref="Required"/> bits they care about, and only ever cancel.
/// </summary>
[ByRefEvent]
public record struct MobActionTargetAttemptEvent(EntityUid User, MobCapability Required, EntityUid? Used)
{
    public bool Cancelled;

    /// <summary>
    /// Optional localized explanation, for consumers that report failures.
    /// </summary>
    public string? Reason;
}
