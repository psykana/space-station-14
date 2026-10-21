using Content.Shared.Interaction.Events;

namespace Content.Shared.ActionBlocker;

public sealed partial class ActionBlockerSystem
{
    [Dependency] private MobConditionSystem _conditions = default!;

    /// <summary>
    /// Whether <paramref name="user"/> can perform <see cref="InteractionRequirements.Action"/>, forgiving the
    /// conditions in <see cref="InteractionRequirements.Ignoring"/>.
    /// </summary>
    /// <remarks>
    /// Unmigrated sources that still cancel the legacy attempt events get a say too, but they don't honour the
    /// ignore mask, so whatever they block stays unforgivable until they migrate.
    /// </remarks>
    public bool CanInteract(EntityUid user, EntityUid? target, in InteractionRequirements req)
    {
        if (_conditions.IsBlocked(user, req.Action, req.Ignoring))
            return false;

        if (req.NeedComplex && !CanComplexInteract(user))
            return false;

        switch (req.Action)
        {
            case MobAction.None:
                return true;
            case MobAction.Conscious:
                return CanConsciouslyPerformAction(user);
            case MobAction.Interact:
            case MobAction.UiAccess:
                return CanInteractLegacy(user, target, req.Ignoring);
            default:
                Log.Error($"{nameof(CanInteract)} does not support {req.Action}; use the specific CanX method.");
                return false;
        }
    }

    private bool CanInteractLegacy(EntityUid user, EntityUid? target, MobCondition ignoring)
    {
        if (!CanConsciouslyPerformAction(user))
            return false;

        var ev = new InteractionAttemptEvent(user, target, ignoring);
        RaiseLocalEvent(user, ref ev);

        if (ev.Cancelled)
            return false;

        if (target == null || target == user)
            return true;

        var targetEv = new GettingInteractedWithAttemptEvent(user, target);
        RaiseLocalEvent(target.Value, ref targetEv);

        return !targetEv.Cancelled;
    }
}
