using Content.Shared.Lock;
using Content.Shared.SmartFridge;
using Robust.Shared.Analyzers;

namespace Content.Client.Lock.UI;

public sealed class SafeUISystem : EntitySystem
{
    [Dependency] private readonly SharedUserInterfaceSystem _uiSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RotaryLockComponent, AfterAutoHandleStateEvent>(OnSmartFridgeAfterState);
    }

    private void OnSmartFridgeAfterState(Entity<RotaryLockComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (!_uiSystem.TryGetOpenUi<RotaryLockBoundUserInterface>(ent.Owner, StorageUiSafeKey.Key, out var bui))
            return;

        bui.Refresh();
    }
}
