namespace Content.Shared.ActionBlocker;

/// <summary>
/// Raised on the user by <see cref="MobCapabilitiesSystem.CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/>
/// for rules about a particular user and target, like pacification or the station AI whitelist.
/// Handlers only ever cancel.
/// </summary>
[ByRefEvent]
public record struct MobActionAttemptEvent(EntityUid User, MobAction Action, EntityUid? Target, EntityUid? Used)
{
    public bool Cancelled;

    /// <summary>
    /// Optional localized explanation, for consumers that report failures.
    /// </summary>
    public string? Reason;
}

/// <summary>
/// Raised on the target (and on the used item, for <see cref="MobAction.UseHeld"/> and <see cref="MobAction.Throw"/>)
/// by <see cref="MobCapabilitiesSystem.CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/>,
/// for things that can't be acted on at all, like virtual items or things hidden under the floor.
/// Handlers only ever cancel.
/// </summary>
[ByRefEvent]
public record struct MobActionTargetAttemptEvent(EntityUid User, MobAction Action, EntityUid? Used)
{
    public bool Cancelled;

    /// <summary>
    /// Optional localized explanation, for consumers that report failures.
    /// </summary>
    public string? Reason;
}
