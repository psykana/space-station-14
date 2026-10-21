using Content.Shared.ActionBlocker;
using Robust.Shared.Collections;

namespace Content.Shared.UserInterface;

/// <summary>
/// Closes a mob's UIs when it falls unconscious. Other conditions leave open windows alone: they stay open at
/// <see cref="UiStatus.Disabled"/> and their input is rejected until the mob recovers.
/// </summary>
public sealed partial class ActivatableUIStatusSystem : EntitySystem
{
    [Dependency] private ActivatableUISystem _activatableUI = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;

    [SubscribeLocalEvent]
    private void OnConditionsChanged(Entity<MobConditionsComponent> ent, ref MobConditionsChangedEvent args)
    {
        if ((args.Added & MobCondition.Unconscious) == 0)
            return;

        var toClose = new ValueList<(EntityUid Entity, Enum Key)>();

        foreach (var bui in _ui.GetActorUis(ent.Owner))
        {
            // These opted out of input validation (laws, vending), so they opt out of this too.
            if (!_ui.TryGetInterfaceData(bui.Entity, bui.Key, out var data) || !data.RequireInputValidation)
                continue;

            if (_activatableUI.GetUiStatus(ent, bui.Entity, bui.Key) < UiStatus.ReadOnly)
                toClose.Add(bui);
        }

        foreach (var bui in toClose)
        {
            _ui.CloseUi(bui.Entity, bui.Key, ent);
        }
    }
}
