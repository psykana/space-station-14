using Content.Client.SmartFridge;
using Content.Client.Storage;
using Content.Client.UserInterface.Controls;
using Content.Client.UserInterface.Systems.Storage.Controls;
using Content.Shared.Lock;
using Content.Shared.SmartFridge;
using Content.Shared.Storage;
using JetBrains.Annotations;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Robust.Shared.Timing;
using System.Linq;
using static Content.Shared.Storage.StorageComponent;
using static Robust.Client.Input.Mouse;

namespace Content.Client.Lock.UI
{
    [UsedImplicitly]
    public sealed class RotaryLockBoundUserInterface : BoundUserInterface
    {
        [ViewVariables]
        private RotaryLockMenu? _menu;

        public RotaryLockBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
        {
        }

        protected override void Open()
        {
            base.Open();
            _menu = this.CreateWindow<RotaryLockMenu>();

            _menu.OnRotateLeftPressed += i => SendPredictedMessage(new RotaryLockTurnLeftMessage(i));
            _menu.OnRotateRightPressed += i => SendPredictedMessage(new RotaryLockTurnRightMessage(i));

            //_menu.OnOpenPressed += (_) => SendPredictedMessage(new RotaryLockOpenMessage());
            _menu.OnOpenPressed += send;

            _menu.OnItemSelected += OnItemSelected;
            Refresh();
        }

        public void Refresh()
        {
            if (_menu is not { } menu || !EntMan.TryGetComponent(Owner, out RotaryLockComponent? fridge))
                return;

            //menu.SetFlavorText(Loc.GetString(fridge.FlavorText));
            menu.Populate((Owner, fridge));
        }

        private void OnItemSelected(GUIBoundKeyEventArgs args, ListData data)
        {
            if (args.Function != EngineKeyFunctions.UIClick)
                return;

            if (data is not SmartFridgeListData entry)
                return;
            SendPredictedMessage(new SmartFridgeDispenseItemMessage(entry.Entry));
        }

        public void send()
        {
            SendPredictedMessage(new RotaryLockOpenMessage());
        }

        protected override void UpdateState(BoundUserInterfaceState state)
        {
            base.UpdateState(state);

            if (_menu != null && state is RotaryLockUiState msg)
                _menu.UpdateState(msg);
        }
    }
}
