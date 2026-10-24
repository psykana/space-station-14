using Content.Shared.Weapons.Melee;

namespace Content.Shared.ActionBlocker;

/// <summary>
/// Raised on the user by <see cref="MobCapabilitiesSystem.CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/>
/// for rules about a particular user and target, like pacification or the station AI whitelist.
/// Handlers test the <see cref="Required"/> bits they care about, and can only cancel.
/// </summary>
[ByRefEvent]
public record struct MobActionAttemptEvent(
    EntityUid User,
    MobCapability Required,
    EntityUid? Target,
    EntityUid? Used,
    MobActionPayload Payload)
{
    public bool Cancelled { get; private set; }

    /// <summary>
    /// Optional localized explanation, for consumers that report failures. The first one given wins.
    /// </summary>
    public string? Reason { get; private set; }

    public void Cancel(string? reason = null)
    {
        Cancelled = true;
        Reason ??= reason;
    }
}

/// <summary>
/// Raised on the target (and on the used item, when <see cref="MobCapability.UseHeld"/> or
/// <see cref="MobCapability.Throw"/> is required) by
/// <see cref="MobCapabilitiesSystem.CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/>,
/// for things that can't be acted on at all, like virtual items or things hidden under the floor.
/// Handlers test the <see cref="Required"/> bits they care about, and can only cancel.
/// </summary>
[ByRefEvent]
public record struct MobActionTargetAttemptEvent(
    EntityUid User,
    MobCapability Required,
    EntityUid? Used,
    MobActionPayload Payload)
{
    public bool Cancelled { get; private set; }

    /// <summary>
    /// Optional localized explanation, for consumers that report failures. The first one given wins.
    /// </summary>
    public string? Reason { get; private set; }

    public void Cancel(string? reason = null)
    {
        Cancelled = true;
        Reason ??= reason;
    }
}

/// <summary>
/// Per-capability data for the rule events. A field is meaningful only when its capability is required.
/// </summary>
/// <param name="Weapon">With <see cref="MobCapability.Attack"/>: the weapon used.</param>
/// <param name="Disarm">With <see cref="MobCapability.Attack"/>: whether this is a disarm.</param>
public readonly record struct MobActionPayload(
    Entity<MeleeWeaponComponent>? Weapon = null,
    bool Disarm = false);
