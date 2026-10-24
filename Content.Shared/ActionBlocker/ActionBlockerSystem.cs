using Content.Shared.Movement.Components;
using Content.Shared.Weapons.Melee;
using JetBrains.Annotations;

namespace Content.Shared.ActionBlocker
{
    /// <summary>
    /// Utility methods to check if a specific entity is allowed to perform an action.
    /// </summary>
    /// <remarks>
    /// Replaced by <see cref="MobCapabilitiesSystem"/>, which every method here forwards to. New code should call
    /// <see cref="MobCapabilitiesSystem.CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/>.
    /// This forwarder stays for downstream code until the new API has been stable for a release, then goes.
    /// </remarks>
    [UsedImplicitly]
    [Obsolete("Use MobCapabilitiesSystem instead. ActionBlockerSystem will be removed in a future release.")]
    public sealed partial class ActionBlockerSystem : EntitySystem
    {
        [Dependency] private MobCapabilitiesSystem _capabilities = default!;

        public bool CanMove(EntityUid uid, InputMoverComponent? component = null)
        {
            return _capabilities.CanMove(uid, component);
        }

        public bool UpdateCanMove(EntityUid uid, InputMoverComponent? component = null)
        {
            return _capabilities.UpdateCanMove(uid, component);
        }

        /// <summary>
        /// Checks if a given entity is able to do specific complex interactions.
        /// This is used to gate manipulation to general humanoids. If a mouse shouldn't be able to do something, then it's complex.
        /// </summary>
        public bool CanComplexInteract(EntityUid user)
        {
            return _capabilities.CanComplexInteract(user);
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
            return _capabilities.CanPerform(user, new InteractionRequirements { Required = MobCapability.Interact }, target);
        }

        /// <summary>
        ///     Can a user utilize the entity that they are currently holding in their hands.
        /// </summary>
        public bool CanUseHeldEntity(EntityUid user, EntityUid used)
        {
            return _capabilities.CanPerform(user, new InteractionRequirements { Required = MobCapability.UseHeld }, used: used);
        }

        /// <summary>
        /// Whether a user conscious to perform an action.
        /// </summary>
        /// <remarks>
        /// This should be used when you want a much more permissive check than <see cref="CanInteract"/>
        /// </remarks>
        public bool CanConsciouslyPerformAction(EntityUid user)
        {
            return _capabilities.CanPerform(user, new InteractionRequirements { Required = MobCapability.Conscious });
        }

        public bool CanThrow(EntityUid user, EntityUid itemUid)
        {
            return _capabilities.CanPerform(user, new InteractionRequirements { Required = MobCapability.Throw }, used: itemUid);
        }

        /// <summary>
        /// Whether a player is able to speak.
        /// This only checks if something blocks them from speaking, not if they had the ability to do so in the first place.
        /// </summary>
        /// <param name="uid">The mob to check.</param>
        public bool CanSpeak(EntityUid uid)
        {
            return _capabilities.CanPerform(uid, new InteractionRequirements { Required = MobCapability.Speak });
        }

        public bool CanDrop(EntityUid uid)
        {
            return _capabilities.CanPerform(uid, new InteractionRequirements { Required = MobCapability.Drop });
        }

        /// <summary>
        /// Whether a user can pickup the given item.
        /// </summary>
        /// <param name="user">The mob trying to pick up the item.</param>
        /// <param name="item">The item being picked up.</param>
        /// <param name="showPopup">Whether or not to show a popup to the player telling them why the attempt failed.</param>
        public bool CanPickup(EntityUid user, EntityUid item, bool showPopup = false)
        {
            // The bookkeeper never shows popups; the caller shows the reason (CON-2).
            return _capabilities.CanPerform(user, new InteractionRequirements { Required = MobCapability.Pickup }, item);
        }

        public bool CanEmote(EntityUid uid)
        {
            return _capabilities.CanPerform(uid, new InteractionRequirements { Required = MobCapability.Emote });
        }

        public bool CanAttack(EntityUid uid, EntityUid? target = null, Entity<MeleeWeaponComponent>? weapon = null, bool disarm = false)
        {
            return _capabilities.CanAttack(uid, target, weapon, disarm);
        }

        public bool CanChangeDirection(EntityUid uid)
        {
            return _capabilities.CanPerform(uid, new InteractionRequirements { Required = MobCapability.ChangeDirection });
        }
    }
}
