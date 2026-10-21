using Robust.Shared.Serialization;

namespace Content.Shared.ActionBlocker;

/// <summary>
/// A closed vocabulary of things that can be wrong with a mob. Producers declare these during
/// <see cref="RefreshMobConditionsEvent"/>; <see cref="MobCapabilitiesSystem"/> cascades them and maps them to
/// blocked <see cref="MobAction"/>s. A member must not be added unless something declares or derives it.
/// </summary>
[Flags, Serializable, NetSerializable]
public enum MobCondition : ushort
{
    None = 0,

    /// <summary>
    /// The mind is unavailable: asleep, critical, dead.
    /// </summary>
    Unconscious = 1 << 0,

    /// <summary>
    /// Generally unable to act. No specific cause is implied.
    /// </summary>
    Incapacitated = 1 << 1,

    /// <summary>
    /// Bound but awake. Blocks nothing directly; acts through <see cref="HandsBlocked"/>.
    /// Exists so that a call site can forgive restraint specifically.
    /// </summary>
    Restrained = 1 << 2,

    /// <summary>
    /// Cannot use manipulators.
    /// </summary>
    HandsBlocked = 1 << 3,

    /// <summary>
    /// Cannot move voluntarily.
    /// </summary>
    Immobilized = 1 << 4,

    /// <summary>
    /// Cannot be upright.
    /// </summary>
    Floored = 1 << 5,

    /// <summary>
    /// Cannot operate user interfaces. Only derived by the cascade.
    /// </summary>
    UiBlocked = 1 << 6,

    /// <summary>
    /// Cannot pull. Only derived by the cascade.
    /// </summary>
    PullBlocked = 1 << 7,

    /// <summary>
    /// No physical presence, like a spectating ghost.
    /// </summary>
    Incorporeal = 1 << 8,
}

/// <summary>
/// The actions a <see cref="MobCondition"/> can block. Each member corresponds to the attempt event that
/// <see cref="MobCapabilitiesSystem"/> cancels for it.
/// </summary>
[Flags, Serializable, NetSerializable]
public enum MobAction : uint
{
    None = 0,

    /// <summary><see cref="Content.Shared.Interaction.Events.InteractionAttemptEvent"/></summary>
    Interact = 1 << 0,

    /// <summary><see cref="Content.Shared.Interaction.Events.ConsciousAttemptEvent"/></summary>
    Conscious = 1 << 1,

    /// <summary><see cref="Content.Shared.Interaction.Events.UseAttemptEvent"/></summary>
    UseHeld = 1 << 2,

    /// <summary><see cref="Content.Shared.Item.PickupAttemptEvent"/></summary>
    Pickup = 1 << 3,

    /// <summary><see cref="Content.Shared.Hands.DropAttemptEvent"/></summary>
    Drop = 1 << 4,

    /// <summary><see cref="Content.Shared.Interaction.Events.AttackAttemptEvent"/></summary>
    Attack = 1 << 5,

    /// <summary><see cref="Content.Shared.Throwing.ThrowAttemptEvent"/></summary>
    Throw = 1 << 6,

    /// <summary><see cref="Content.Shared.Speech.SpeakAttemptEvent"/></summary>
    Speak = 1 << 7,

    /// <summary><see cref="Content.Shared.Emoting.EmoteAttemptEvent"/></summary>
    Emote = 1 << 8,

    /// <summary><see cref="Content.Shared.Pointing.PointAttemptEvent"/></summary>
    Point = 1 << 9,

    /// <summary><see cref="Content.Shared.Movement.Events.UpdateCanMoveEvent"/></summary>
    Move = 1 << 10,

    /// <summary><see cref="Content.Shared.Standing.StandAttemptEvent"/>: may this entity be upright at all.</summary>
    Stand = 1 << 11,

    /// <summary><see cref="Content.Shared.Pulling.Events.StartPullAttemptEvent"/></summary>
    Pull = 1 << 12,

    /// <summary><see cref="Content.Shared.Interaction.Events.ChangeDirectionAttemptEvent"/></summary>
    ChangeDirection = 1 << 13,

    /// <summary>No attempt event; consumed by the UI access tiers.</summary>
    UiAccess = 1 << 14,

    /// <summary><see cref="Content.Shared.Stunnable.StandUpAttemptEvent"/>: may it try to rise.</summary>
    StandUp = 1 << 15,
}
