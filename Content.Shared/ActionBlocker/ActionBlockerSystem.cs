using Content.Shared.Movement.Components;
using Content.Shared.Weapons.Melee;
using JetBrains.Annotations;

namespace Content.Shared.ActionBlocker
{
    /// <summary>
    /// Utility methods to check if a specific entity is allowed to perform an action.
    /// </summary>
    /// <remarks>
    /// Being replaced by <see cref="MobConditionSystem"/>, which every method here forwards to. New code should call
    /// <see cref="MobConditionSystem.CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/>.
    /// </remarks>
    [UsedImplicitly]
    public sealed partial class ActionBlockerSystem : EntitySystem
    {
        [Dependency] private MobConditionSystem _conditions = default!;

        public bool CanMove(EntityUid uid, InputMoverComponent? component = null)
        {
            return _conditions.CanMove(uid, component);
        }

        public bool UpdateCanMove(EntityUid uid, InputMoverComponent? component = null)
        {
            return _conditions.UpdateCanMove(uid, component);
        }

        /// <summary>
        /// Checks if a given entity is able to do specific complex interactions.
        /// This is used to gate manipulation to general humanoids. If a mouse shouldn't be able to do something, then it's complex.
        /// </summary>
        public bool CanComplexInteract(EntityUid user)
        {
            return _conditions.CanComplexInteract(user);
        }

        /// <summary>
        ///     Whether a user is capable of interacting with this entity.
        /// </summary>
        /// <remarks>
        ///     If this is a generic interaction without a target (e.g., stop-drop-and-roll when burning), the target
        ///     may be null. Note that this is checked by <see cref="Content.Shared.Interaction.SharedInteractionSystem"/>.
        ///     In the majority of cases, systems that provide interactions will not need to check this themselves,
        ///     though they may need to check other blockers like <see cref="CanPickup"/>
        /// </remarks>
        public bool CanInteract(EntityUid user, EntityUid? target)
        {
            return _conditions.CanPerform(user, new InteractionRequirements { Action = MobAction.Interact }, target);
        }

        /// <summary>
        ///     Can a user utilize the entity that they are currently holding in their hands.
        /// </summary>
        public bool CanUseHeldEntity(EntityUid user, EntityUid used)
        {
            return _conditions.CanPerform(user, new InteractionRequirements { Action = MobAction.UseHeld }, used: used);
        }

        /// <summary>
        /// Whether a user conscious to perform an action.
        /// </summary>
        /// <remarks>
        /// This should be used when you want a much more permissive check than <see cref="CanInteract"/>
        /// </remarks>
        public bool CanConsciouslyPerformAction(EntityUid user)
        {
            return _conditions.CanPerform(user, new InteractionRequirements { Action = MobAction.Conscious });
        }

        public bool CanThrow(EntityUid user, EntityUid itemUid)
        {
            return _conditions.CanPerform(user, new InteractionRequirements { Action = MobAction.Throw }, used: itemUid);
        }

        /// <summary>
        /// Whether a player is able to speak.
        /// This only checks if something blocks them from speaking, not if they had the ability to do so in the first place.
        /// </summary>
        /// <param name="uid">The mob to check.</param>
        public bool CanSpeak(EntityUid uid)
        {
            return _conditions.CanPerform(uid, new InteractionRequirements { Action = MobAction.Speak });
        }

        public bool CanDrop(EntityUid uid)
        {
            return _conditions.CanPerform(uid, new InteractionRequirements { Action = MobAction.Drop });
        }

        /// <summary>
        /// Whether a user can pickup the given item.
        /// </summary>
        /// <param name="user">The mob trying to pick up the item.</param>
        /// <param name="item">The item being picked up.</param>
        /// <param name="showPopup">Whether or not to show a popup to the player telling them why the attempt failed.</param>
        public bool CanPickup(EntityUid user, EntityUid item, bool showPopup = false)
        {
            return _conditions.CanPickup(user, item, showPopup);
        }

        public bool CanEmote(EntityUid uid)
        {
            return _conditions.CanPerform(uid, new InteractionRequirements { Action = MobAction.Emote });
        }

        public bool CanAttack(EntityUid uid, EntityUid? target = null, Entity<MeleeWeaponComponent>? weapon = null, bool disarm = false)
        {
            return _conditions.CanAttack(uid, target, weapon, disarm);
        }

        public bool CanChangeDirection(EntityUid uid)
        {
            return _conditions.CanPerform(uid, new InteractionRequirements { Action = MobAction.ChangeDirection });
        }
    }
}
