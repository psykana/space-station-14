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
            if (comp?.RequiresComplex == true && !_capabilities.CanComplexInteract(user))
                return UiStatus.Close;

            return UiStatus.ReadOnly;
        }

        // An unconscious mind can't have a UI open at all, unlike a stunned or restrained one.
        if (_capabilities.HasCondition(user, MobCondition.Unconscious))
            return UiStatus.Close;

        if (comp?.AdminOnly == true && !_adminManager.IsAdmin(user))
            return UiStatus.Close;

        if (comp?.RequiresComplex == true && !_capabilities.CanComplexInteract(user))
            return UiStatus.Close;

        var req = new InteractionRequirements { Required = MobCapability.UiAccess };
        if (_capabilities.CanPerform(user, req, target))
            return UiStatus.Interactive;

        if (comp != null && comp.ReadOnlyConditions != MobCondition.None)
        {
            req.Ignoring = comp.ReadOnlyConditions;
            if (_capabilities.CanPerform(user, req, target))
                return UiStatus.ReadOnly;
        }

        return UiStatus.Disabled;
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
