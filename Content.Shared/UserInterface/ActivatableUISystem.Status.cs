using Content.Shared.ActionBlocker;
using Content.Shared.Ghost.Components;

namespace Content.Shared.UserInterface;

public sealed partial class ActivatableUISystem
{
    /// <summary>
    /// Resolves how much <paramref name="user"/> may do with the UI <paramref name="key"/> on
    /// <paramref name="target"/>. The <see cref="ActivatableUIComponent"/> policy applies only to the key it names.
    /// </summary>
    public UiStatus GetUiStatus(EntityUid user, Entity<ActivatableUIComponent?> target, Enum key)
    {
        Resolve(target, ref target.Comp, false);
        var comp = target.Comp != null && Equals(target.Comp.Key, key) ? target.Comp : null;

        // Spectating is a rule about observers, not a mob capability, so it resolves before any condition.
        if (TryComp<GhostComponent>(user, out var ghost) && !ghost.CanGhostInteract)
        {
            // Read before the key match, as it always has been.
            if (target.Comp?.BlockSpectators == true)
                return UiStatus.Close;

            // TODO MOB CONDITIONS: let spectators inspect complex UIs once opening them has no side effects.
            if (comp?.RequiresComplex == true && !_blockerSystem.CanComplexInteract(user))
                return UiStatus.Close;

            return UiStatus.ReadOnly;
        }

        if (comp?.AdminOnly == true && !_adminManager.IsAdmin(user))
            return UiStatus.Close;

        if (comp?.RequiresComplex == true && !_blockerSystem.CanComplexInteract(user))
            return UiStatus.Close;

        var req = new InteractionRequirements { Action = MobAction.UiAccess };
        if (_blockerSystem.CanInteract(user, target, req))
            return UiStatus.Interactive;

        if (comp != null && comp.ReadOnlyConditions != MobCondition.None)
        {
            req.Ignoring = comp.ReadOnlyConditions;
            if (_blockerSystem.CanInteract(user, target, req))
                return UiStatus.ReadOnly;
        }

        return UiStatus.Disabled;
    }

    /// <summary>
    /// Whether <paramref name="actor"/> may use, not just look at, the UI. For <see cref="BoundUIOpenedEvent"/>
    /// handlers with side effects, which must not apply them for read-only viewers.
    /// </summary>
    public bool IsInteractiveViewer(EntityUid uiEntity, Enum key, EntityUid actor)
    {
        return GetUiStatus(actor, uiEntity, key) == UiStatus.Interactive;
    }

    /// <summary>
    /// Opens the UI for a user whose blockers failed, if the UI forgives what is wrong with them.
    /// </summary>
    public bool TryOpenReadOnly(EntityUid user, EntityUid uiEntity)
    {
        if (!TryComp<ActivatableUIComponent>(uiEntity, out var aui) ||
            aui.ReadOnlyConditions == MobCondition.None ||
            aui.VerbOnly ||
            aui.RequiredItems != null)
        {
            return false;
        }

        if (GetUiStatus(user, (uiEntity, aui), aui.Key) != UiStatus.ReadOnly)
            return false;

        return InteractUI(user, uiEntity, aui);
    }
}
