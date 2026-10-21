using Content.Shared.ActionBlocker;
using Robust.Shared.Collections;

namespace Content.Shared.UserInterface;

/// <summary>
/// Closes a mob's UIs once their status drops to <see cref="UiStatus.Close"/>, for example when it falls
/// unconscious. Windows at <see cref="UiStatus.Disabled"/> stay open; their input is rejected until the mob recovers.
/// </summary>
public sealed partial class ActivatableUIStatusSystem : EntitySystem
{
    [Dependency] private ActivatableUISystem _activatableUI = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;

    [SubscribeLocalEvent]
    private void OnConditionsChanged(ref MobConditionsChangedEvent args)
    {
        if (args.Added == MobCondition.None)
            return;

        var toClose = new ValueList<(EntityUid Entity, Enum Key)>();

        foreach (var bui in _ui.GetActorUis(args.Uid))
        {
            // These opted out of input validation (laws, vending), so they opt out of this too.
            if (!_ui.TryGetInterfaceData(bui.Entity, bui.Key, out var data) || !data.RequireInputValidation)
                continue;

            if (_activatableUI.GetUiStatus(args.Uid, bui.Entity, bui.Key) == UiStatus.Close)
                toClose.Add(bui);
        }

        foreach (var bui in toClose)
        {
            _ui.CloseUi(bui.Entity, bui.Key, args.Uid);
        }
    }
}
