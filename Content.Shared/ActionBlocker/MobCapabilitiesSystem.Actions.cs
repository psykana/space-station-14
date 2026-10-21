using Content.Shared.Damage.ForceSay;
using Content.Shared.Emoting;
using Content.Shared.Hands;
using Content.Shared.Interaction.Components;
using Content.Shared.Interaction.Events;
using Content.Shared.Item;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Pointing;
using Content.Shared.Pulling.Events;
using Content.Shared.Speech;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Containers;

namespace Content.Shared.ActionBlocker;

/// <summary>
/// The consumer side of the bookkeeper: everything <see cref="ActionBlockerSystem"/> used to answer.
/// </summary>
public sealed partial class MobCapabilitiesSystem
{
    [Dependency] private SharedContainerSystem _container = default!;

    [Dependency] private EntityQuery<ComplexInteractionComponent> _complexQuery = default!;
    [Dependency] private EntityQuery<InputMoverComponent> _moverQuery = default!;

    /// <summary>
    /// Extra context some legacy attempt events carry. Goes away with those events.
    /// </summary>
    private readonly record struct LegacyArgs(
        bool ShowPopup = false,
        Entity<MeleeWeaponComponent>? Weapon = null,
        bool Disarm = false);

    /// <summary>
    /// Whether <paramref name="user"/> can perform <see cref="InteractionRequirements.Action"/>, on
    /// <paramref name="target"/> and with <paramref name="used"/> if given.
    /// </summary>
    public bool CanPerform(EntityUid user,
        in InteractionRequirements req,
        EntityUid? target = null,
        EntityUid? used = null)
    {
        return CanPerform(user, req, target, used, out _);
    }

    /// <inheritdoc cref="CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/>
    /// <param name="reason">A localized explanation, if whatever blocked the action gave one.</param>
    public bool CanPerform(EntityUid user,
        in InteractionRequirements req,
        EntityUid? target,
        EntityUid? used,
        out string? reason)
    {
        return Perform(user, req, target, used, default, out reason);
    }

    /// <summary>
    /// <see cref="CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/> for
    /// <see cref="MobAction.Pickup"/>, with the legacy popup flag.
    /// </summary>
    public bool CanPickup(EntityUid user, EntityUid item, bool showPopup = false)
    {
        var req = new InteractionRequirements { Action = MobAction.Pickup };
        return Perform(user, req, item, null, new LegacyArgs(ShowPopup: showPopup), out _);
    }

    /// <summary>
    /// <see cref="CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/> for
    /// <see cref="MobAction.Attack"/>, with the weapon and the disarm flag.
    /// </summary>
    public bool CanAttack(EntityUid user,
        EntityUid? target = null,
        Entity<MeleeWeaponComponent>? weapon = null,
        bool disarm = false)
    {
        var req = new InteractionRequirements { Action = MobAction.Attack };
        return Perform(user, req, target, weapon?.Owner, new LegacyArgs(Weapon: weapon, Disarm: disarm), out _);
    }

    /// <summary>
    /// Whether the entity has the hands and wits for complex interactions. A mouse doesn't.
    /// </summary>
    public bool CanComplexInteract(EntityUid user)
    {
        return _complexQuery.HasComp(user);
    }

    /// <summary>
    /// The cached answer to whether the entity can move.
    /// </summary>
    public bool CanMove(EntityUid uid, InputMoverComponent? mover = null)
    {
        return _moverQuery.Resolve(uid, ref mover, false) && mover.CanMove;
    }

    /// <summary>
    /// Recomputes <see cref="CanMove"/>. Callers are sources of movement blocking whose state changed.
    /// </summary>
    public bool UpdateCanMove(EntityUid uid, InputMoverComponent? mover = null)
    {
        if (!_moverQuery.Resolve(uid, ref mover, false))
            return false;

        var canMove = !IsBlocked(uid, MobAction.Move);
        if (canMove)
        {
            var ev = new UpdateCanMoveEvent(uid);
            RaiseLocalEvent(uid, ev);
            canMove = !ev.Cancelled;
        }

        if (mover.CanMove != canMove)
            Dirty(uid, mover);

        mover.CanMove = canMove;
        var updatedEv = new CanMoveUpdatedEvent(canMove);
        RaiseLocalEvent(uid, ref updatedEv);
        return canMove;
    }

    /// <summary>
    /// <see cref="CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/> for
    /// <see cref="MobAction.StandUp"/>. The legacy event carries data back out (autostand, a message), so the caller
    /// supplies it.
    /// </summary>
    public bool CanStandUp(EntityUid uid, ref StandUpAttemptEvent ev)
    {
        var req = new InteractionRequirements { Action = MobAction.StandUp };

        if (!CheckSelf(uid, req, out _))
        {
            ev.Cancelled = true;
            return false;
        }

        RaiseLocalEvent(uid, ref ev);
        if (ev.Cancelled)
            return false;

        if (!CheckRules(uid, req.Action, null, null, out _))
        {
            ev.Cancelled = true;
            return false;
        }

        return true;
    }

    private bool Perform(EntityUid user,
        in InteractionRequirements req,
        EntityUid? target,
        EntityUid? used,
        LegacyArgs legacy,
        out string? reason)
    {
        if (!CheckSelf(user, req, out reason))
            return false;

        // Sources that haven't migrated yet still cancel their attempt events.
        if (!RaiseLegacyAttempts(user, req.Action, target, used, legacy))
            return false;

        return CheckRules(user, req.Action, target, used, out reason);
    }

    /// <summary>
    /// What the entity itself can do: its conditions, from the cache, then its creature properties.
    /// </summary>
    private bool CheckSelf(EntityUid user, in InteractionRequirements req, out string? reason)
    {
        reason = null;

        if (IsBlocked(user, req.Action, req.Ignoring) && !TryUseLastWords(user, req.Action))
        {
            if (TryGetBlockedReason(user, req.Action, out var conditionReason, req.Ignoring))
                reason = conditionReason;

            return false;
        }

        return !req.NeedComplex || CanComplexInteract(user);
    }

    /// <summary>
    /// Rules about this user and target, then things that can't be acted on at all.
    /// </summary>
    private bool CheckRules(EntityUid user, MobAction action, EntityUid? target, EntityUid? used, out string? reason)
    {
        reason = null;

        var ev = new MobActionAttemptEvent(user, action, target, used);
        RaiseLocalEvent(user, ref ev);
        if (ev.Cancelled)
        {
            reason = ev.Reason;
            return false;
        }

        if (target != null && target != user && !TargetAllows(user, action, target.Value, used, out reason))
            return false;

        if (used != null && action is MobAction.UseHeld or MobAction.Throw &&
            !TargetAllows(user, action, used.Value, used, out reason))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Last words: a one-shot grant with a timeout, so not something an ignore mask can express.
    /// </summary>
    private bool TryUseLastWords(EntityUid user, MobAction action)
    {
        if (action != MobAction.Speak || !_critSpeechQuery.HasComp(user))
            return false;

        RemCompDeferred<AllowNextCritSpeechComponent>(user);
        return true;
    }

    private bool TargetAllows(EntityUid user, MobAction action, EntityUid target, EntityUid? used, out string? reason)
    {
        var ev = new MobActionTargetAttemptEvent(user, action, used);
        RaiseLocalEvent(target, ref ev);
        reason = ev.Reason;
        return !ev.Cancelled;
    }

    /// <summary>
    /// Raises the typed attempt events that <see cref="ActionBlockerSystem"/> used to raise. Each one is deleted once
    /// its last subscriber has moved to conditions, rules or properties (spec §16).
    /// </summary>
    private bool RaiseLegacyAttempts(EntityUid user, MobAction action, EntityUid? target, EntityUid? used, LegacyArgs legacy)
    {
        switch (action)
        {
            case MobAction.None:
                return true;

            case MobAction.Conscious:
                return RaiseConsciousAttempt(user);

            case MobAction.Interact:
            case MobAction.UiAccess:
            {
                if (!RaiseConsciousAttempt(user))
                    return false;

                var ev = new InteractionAttemptEvent(user, target);
                RaiseLocalEvent(user, ref ev);
                if (ev.Cancelled)
                    return false;

                if (target == null || target == user)
                    return true;

                var targetEv = new GettingInteractedWithAttemptEvent(user, target);
                RaiseLocalEvent(target.Value, ref targetEv);
                return !targetEv.Cancelled;
            }

            case MobAction.UseHeld:
            {
                if (used == null)
                    return true;

                var ev = new UseAttemptEvent(user, used.Value);
                RaiseLocalEvent(user, ev);
                if (ev.Cancelled)
                    return false;

                var usedEv = new GettingUsedAttemptEvent(user);
                RaiseLocalEvent(used.Value, usedEv);
                return !usedEv.Cancelled;
            }

            case MobAction.Pickup:
            {
                if (target == null)
                    return true;

                var ev = new PickupAttemptEvent(user, target.Value, legacy.ShowPopup);
                RaiseLocalEvent(user, ev);
                if (ev.Cancelled)
                    return false;

                var itemEv = new GettingPickedUpAttemptEvent(user, target.Value, legacy.ShowPopup);
                RaiseLocalEvent(target.Value, itemEv);
                return !itemEv.Cancelled;
            }

            case MobAction.Drop:
            {
                var ev = new DropAttemptEvent();
                RaiseLocalEvent(user, ev);
                return !ev.Cancelled;
            }

            case MobAction.Throw:
            {
                if (used == null)
                    return true;

                var ev = new ThrowAttemptEvent(user, used.Value);
                RaiseLocalEvent(user, ev);
                if (ev.Cancelled)
                    return false;

                var itemEv = new ThrowItemAttemptEvent(user);
                RaiseLocalEvent(used.Value, ref itemEv);
                return !itemEv.Cancelled;
            }

            case MobAction.Attack:
                return RaiseAttackAttempts(user, target, legacy);

            case MobAction.Speak:
            {
                // Raised broadcast too.
                var ev = new SpeakAttemptEvent(user);
                RaiseLocalEvent(user, ev, true);
                return !ev.Cancelled;
            }

            case MobAction.Emote:
            {
                // Raised broadcast too.
                var ev = new EmoteAttemptEvent(user);
                RaiseLocalEvent(user, ev, true);
                return !ev.Cancelled;
            }

            case MobAction.ChangeDirection:
            {
                var ev = new ChangeDirectionAttemptEvent(user);
                RaiseLocalEvent(user, ev);
                return !ev.Cancelled;
            }

            case MobAction.Stand:
            {
                var ev = new StandAttemptEvent();
                RaiseLocalEvent(user, ev);
                return !ev.Cancelled;
            }

            case MobAction.Down:
            {
                var ev = new DownAttemptEvent();
                RaiseLocalEvent(user, ev);
                return !ev.Cancelled;
            }

            case MobAction.StandUp:
            {
                // Callers that need the event's data use CanStandUp.
                var ev = new StandUpAttemptEvent();
                RaiseLocalEvent(user, ref ev);
                return !ev.Cancelled;
            }

            case MobAction.Pull:
            {
                if (target == null)
                    return true;

                // Raised broadcast too.
                var ev = new StartPullAttemptEvent(user, target.Value);
                RaiseLocalEvent(user, ev, true);
                return !ev.Cancelled;
            }

            case MobAction.Point:
            {
                // Raised broadcast too.
                var ev = new PointAttemptEvent(user);
                RaiseLocalEvent(user, ev, true);
                return !ev.Cancelled;
            }

            default:
                // Move is a cache: use CanMove.
                Log.Error($"{nameof(CanPerform)} does not support {action}.");
                return false;
        }
    }

    private bool RaiseConsciousAttempt(EntityUid user)
    {
        var ev = new ConsciousAttemptEvent(user);
        RaiseLocalEvent(user, ref ev);
        return !ev.Cancelled;
    }

    private bool RaiseAttackAttempts(EntityUid user, EntityUid? target, LegacyArgs legacy)
    {
        // If target is in a container can we attack
        if (target != null && _container.IsEntityInContainer(target.Value))
            return false;

        _container.TryGetOuterContainer(user, Transform(user), out var outerContainer);

        // If we're in a container can we attack the target.
        if (target != null && target != outerContainer?.Owner && _container.IsEntityInContainer(user))
        {
            var containerEv = new CanAttackFromContainerEvent(user, target);
            RaiseLocalEvent(user, containerEv);
            if (!containerEv.CanAttack)
                return false;
        }

        var ev = new AttackAttemptEvent(user, target, legacy.Weapon, legacy.Disarm);
        RaiseLocalEvent(user, ev);
        if (ev.Cancelled)
            return false;

        if (target == null)
            return true;

        var targetEv = new GettingAttackedAttemptEvent(user, legacy.Weapon, legacy.Disarm);
        RaiseLocalEvent(target.Value, ref targetEv);
        return !targetEv.Cancelled;
    }
}
