namespace Content.Shared.Interaction.Events
{
    /// <summary>
    ///     Event raised directed at a user to see if they can perform a generic interaction.
    /// </summary>
    [ByRefEvent]
    public struct InteractionAttemptEvent(EntityUid uid, EntityUid? target)
    {
        public bool Cancelled;
        public readonly EntityUid Uid = uid;
        public readonly EntityUid? Target = target;
    }

    /// <summary>
    /// Raised to determine whether an entity is conscious to perform an action.
    /// </summary>
    [ByRefEvent]
    public struct ConsciousAttemptEvent(EntityUid uid)
    {
        public bool Cancelled;
        public readonly EntityUid Uid = uid;
    }

    /// <summary>
    ///     Event raised directed at the target entity of an interaction to see if the user is allowed to perform some
    ///     generic interaction.
    /// </summary>
    [ByRefEvent]
    [Obsolete("Subscribe to MobActionTargetAttemptEvent and check for MobAction.Interact (and UiAccess). This event will be removed in a future release.")]
    public struct GettingInteractedWithAttemptEvent(EntityUid uid, EntityUid? target)
    {
        public bool Cancelled;
        public readonly EntityUid Uid = uid;
        public readonly EntityUid? Target = target;
    }
}
