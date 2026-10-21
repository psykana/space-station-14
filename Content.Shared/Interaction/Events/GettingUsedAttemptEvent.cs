namespace Content.Shared.Interaction.Events;

/// <summary>
/// Event raised on an item when attempting to use it in your hands. Cancelling it stops the interaction.
/// </summary>
/// <param name="user">The user of said item</param>
[Obsolete("Subscribe to MobActionTargetAttemptEvent and check for MobAction.UseHeld. This event will be removed in a future release.")]
public sealed class GettingUsedAttemptEvent(EntityUid user) : CancellableEntityEventArgs
{
    public EntityUid User = user;
}
