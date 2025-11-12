using Content.Shared.Access.Systems;
using Content.Shared.Audio;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Electrocution;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Medical.Stethoscope.Components;
using Content.Shared.Popups;
using Content.Shared.SmartFridge;
using Content.Shared.Tools.Systems;
using Content.Shared.Whitelist;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Shared.Lock;

public sealed class RotaryLockSystem : EntitySystem
{
    [Dependency] private readonly LockSystem _lock = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;
    [Dependency] private readonly AccessReaderSystem _accessReader = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly InventorySystem _inventorySystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    public override void Initialize()
    {
        base.Initialize();

        // UI
        SubscribeLocalEvent<RotaryLockComponent, ComponentInit>(OnInit);

        SubscribeLocalEvent<RotaryLockComponent, RotaryLockTurnLeftMessage>(OnRotateLeftPressed);
        SubscribeLocalEvent<RotaryLockComponent, RotaryLockTurnRightMessage>(OnRotateRightPressed);
        SubscribeLocalEvent<RotaryLockComponent, RotaryLockOpenMessage>(OnOpenPressed);

        SubscribeLocalEvent<RotaryLockComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<RotaryLockComponent, EntRemovedFromContainerMessage>(OnItemRemoved);
        Subs.BuiEvents<RotaryLockComponent>(StorageUiSafeKey.Key,
            sub =>
            {
                sub.Event<SmartFridgeDispenseItemMessage>(OnDispenseItem);
            });
    }

    private bool DoInsert(Entity<RotaryLockComponent> ent, EntityUid user, IEnumerable<EntityUid> usedItems, bool playSound)
    {
        if (!_container.TryGetContainer(ent, ent.Comp.Container, out var container))
            return false;

        //if (!Allowed(ent, user))
        //    return true;

        bool anyInserted = false;
        foreach (var used in usedItems)
        {
            //if (!_whitelist.CheckBoth(used, ent.Comp.Blacklist, ent.Comp.Whitelist))
            //    continue;
            anyInserted = true;

            _container.Insert(used, container);
            var key = new SmartFridgeEntry(Identity.Name(used, EntityManager));
            if (!ent.Comp.Entries.Contains(key))
                ent.Comp.Entries.Add(key);

            ent.Comp.ContainedEntries.TryAdd(key, new());
            var entries = ent.Comp.ContainedEntries[key];
            if (!entries.Contains(GetNetEntity(used)))
                entries.Add(GetNetEntity(used));

            Dirty(ent);
        }

        if (anyInserted && playSound)
        {
            //_audio.PlayPredicted(ent.Comp.InsertSound, ent, user);
        }

        return anyInserted;
    }

    private void OnInteractUsing(Entity<RotaryLockComponent> ent, ref InteractUsingEvent args)
    {
        if (!_hands.CanDrop(args.User, args.Used))
            return;

        if (_lock.IsLocked(ent.Owner))
        {
            return;
        }

        args.Handled = DoInsert(ent, args.User, [args.Used], true);
    }

    private void OnItemRemoved(Entity<RotaryLockComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        var key = new SmartFridgeEntry(Identity.Name(args.Entity, EntityManager));

        if (ent.Comp.ContainedEntries.TryGetValue(key, out var contained))
        {
            //contained.Remove(GetNetEntity(args.Entity));
            //_itemSlots.TryEjectToHands(ent, ent.Comp.Container, args.);
        }

        Dirty(ent);
    }

    private bool Allowed(Entity<RotaryLockComponent> machine, EntityUid user)
    {
        if (_accessReader.IsAllowed(user, machine))
            return true;

        _popup.PopupPredicted(Loc.GetString("smart-fridge-component-try-eject-access-denied"), machine, user);
        //_audio.PlayPredicted(machine.Comp.SoundDeny, machine, user);
        return false;
    }

    private void OnDispenseItem(Entity<RotaryLockComponent> ent, ref SmartFridgeDispenseItemMessage args)
    {
        if (!_timing.IsFirstTimePredicted)
            return;

        if (!Allowed(ent, args.Actor))
            return;

        if (!ent.Comp.ContainedEntries.TryGetValue(args.Entry, out var contained))
        {
            //_audio.PlayPredicted(ent.Comp.SoundDeny, ent, args.Actor);
            _popup.PopupPredicted(Loc.GetString("smart-fridge-component-try-eject-unknown-entry"), ent, args.Actor);
            return;
        }

        foreach (var item in contained)
        {
            if (!_hands.CanPickupAnyHand(args.Actor, GetEntity(item)))
                continue;

            if (!_container.TryRemoveFromContainer(GetEntity(item)))
                continue;

            _hands.TryPickup(args.Actor, GetEntity(item));

            //_audio.PlayPredicted(ent.Comp.SoundVend, ent, args.Actor);
            contained.Remove(item);
            Dirty(ent);
            return;
        }

        //_audio.PlayPredicted(ent.Comp.SoundDeny, ent, args.Actor);
        //_popup.PopupPredicted(Loc.GetString("smart-fridge-component-try-eject-out-of-stock"), ent, args.Actor);
    }

    private void OnRotateLeftPressed(EntityUid uid, RotaryLockComponent component, RotaryLockTurnLeftMessage args)
    {
        if (component.Status == SafeStatus.OPEN)
            return;

        bool hearing = false;

        if (_inventorySystem.TryGetSlotEntity(args.Actor, "neck", out var item) && HasComp<StethoscopeComponent>(item))
        {
            hearing = true;
        }

        component.DialPos = (component.DialPos + 100 + args.Value) % 100;

        bool invalidTurn = (!IsEven(component.Current_tumbler_index) || component.Current_tumbler_index > component.Number_of_tumblers - 1);
        if (invalidTurn)
        {
            component.Current_tumbler_index = 0;

            SoundSpecifier SoundDeny = new SoundCollectionSpecifier("VendingDeny");
            _audio.PlayLocal(SoundDeny, uid, args.Actor, AudioParams.Default.WithVariation(1.2f));
        }

        if (!invalidTurn && component.DialPos == component.Tumblers[component.Current_tumbler_index])
        {
            component.Current_tumbler_index++;
        }

        if (component.Current_tumbler_index > component.Number_of_tumblers - 1)
        {
            component.Status = SafeStatus.UNLOCKED;
        } else
        {
            component.Status = SafeStatus.LOCKED;
        }

            /*
            switch (component.Status)
            {
                case SafeStatus.UNLOCKED:
                    if (component.DialPos != 10)
                    {
                        component.Status = SafeStatus.LOCKED;
                    }
                    break;
                case SafeStatus.LOCKED:
                    if (component.DialPos == 10)
                    {
                        component.Status = SafeStatus.UNLOCKED;
                    }
                    break;
            }
            */

        Dirty(uid, component);
        UpdateUserInterface(uid, component);
    }

    private bool IsEven(int num)
    {
        if (num % 2 == 0)
        {
            return true;
        }
        else
        {
            return false;
        }
    }
    private void OnRotateRightPressed(EntityUid uid, RotaryLockComponent component, RotaryLockTurnRightMessage args)
    {
        if (component.Status == SafeStatus.OPEN)
            return;

        component.DialPos = (component.DialPos + 100 - args.Value) % 100;

        bool invalidTurn = (IsEven(component.Current_tumbler_index) || component.Current_tumbler_index > component.Number_of_tumblers - 1);
        if (invalidTurn)
        {
            component.Current_tumbler_index = 0;
        }

        if (!invalidTurn && component.DialPos == component.Tumblers[component.Current_tumbler_index])
        {
            component.Current_tumbler_index++;
        }

        if (component.Current_tumbler_index > component.Number_of_tumblers - 1)
        {
            component.Status = SafeStatus.UNLOCKED;
        }
        else
        {
            component.Status = SafeStatus.LOCKED;
        }

        /*
        switch (component.Status)
        {
            case SafeStatus.UNLOCKED:
                if (component.DialPos != 10)
                {
                    component.Status = SafeStatus.LOCKED;
                }
                break;
            case SafeStatus.LOCKED:
                if (component.DialPos == 10)
                {
                    component.Status = SafeStatus.UNLOCKED;
                }
                break;
        }
        */

        Dirty(uid, component);
        UpdateUserInterface(uid, component);
    }
    private void OnOpenPressed(EntityUid uid, RotaryLockComponent component, RotaryLockOpenMessage args)
    {
        if (component.Status != SafeStatus.UNLOCKED && component.Status != SafeStatus.OPEN)
            return;
            //if ((component.Status == SafeStatus.UNLOCKED) || (component.Status == SafeStatus.OPEN))
            //{
            _lock.ToggleLock(uid, args.Actor);
        //}

        if (component.Status == SafeStatus.OPEN)
        {
            component.Status = SafeStatus.UNLOCKED;
        }
        else if (component.Status == SafeStatus.UNLOCKED)
        {
            component.Status = SafeStatus.OPEN;
        }
        Dirty(uid, component);
        UpdateUserInterface(uid, component);
    }

    #region UI

    /// <summary>
    /// Update UI Status on init
    /// </summary>
    private void OnInit(EntityUid uid, RotaryLockComponent component, ComponentInit args)
    {
        for (int i = 0; i < component.Number_of_tumblers; i++)
        {
#if DEBUG
            component.Tumblers.Add(IsEven(i) ? 1 : 0);
#else
            component.Tumblers.Add(_random.Next(0, 99));
#endif
        }
        UpdateStatus(uid, component);
        UpdateUserInterface(uid, component);
    }

    /// <summary>
    /// Update Status of Digital lock
    /// </summary>
    /// <param name="uid">Lock Owner</param>
    /// <param name="component">Digital Lock Component</param>
    private void UpdateStatus(EntityUid uid, RotaryLockComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (!TryComp<LockComponent>(uid, out var lockComponent))
            return;
    }

    /// <summary>
    /// Update Digital Lock UI
    /// </summary>
    /// <param name="uid">Lock Owner</param>
    /// <param name="component">Digital Lock Component</param>
    private void UpdateUserInterface(EntityUid uid, RotaryLockComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (!_ui.HasUi(uid, StorageUiSafeKey.Key))
            return;

        var state = new RotaryLockUiState
        {
            DialPos = component.DialPos,
            Status = component.Status
        };

        _ui.SetUiState(uid, StorageUiSafeKey.Key, state);
    }

#endregion
}
