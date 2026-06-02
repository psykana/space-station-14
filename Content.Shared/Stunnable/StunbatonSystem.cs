using Content.Shared.Damage.Events;
using Content.Shared.Examine;
using Content.Shared.Item;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Popups;
using Content.Shared.Power;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell;
using Content.Shared.PowerCell.Components;

namespace Content.Shared.Stunnable;

public sealed partial class StunbatonSystem : EntitySystem
{
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedBatterySystem _battery = default!;
    [Dependency] private SharedItemSystem _item = default!;
    [Dependency] private ItemToggleSystem _itemToggle = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private PowerCellSystem _powerCell = default!;


    // In-hand sprite prefixes for RSI state naming
    private const string OnPrefix = "on";
    private const string OffPrefix = "off";
    private const string NoCellPrefix = "nocell";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<StunbatonComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<StunbatonComponent, StaminaDamageOnHitAttemptEvent>(OnStaminaHitAttempt);
        SubscribeLocalEvent<StunbatonComponent, ChargeChangedEvent>(OnChargeChanged);
        SubscribeLocalEvent<StunbatonComponent, ItemToggleActivateAttemptEvent>(TryTurnOn);
        SubscribeLocalEvent<StunbatonComponent, PowerCellSlotEmptyEvent>(OnCellSlotEmpty);
        SubscribeLocalEvent<StunbatonComponent, PowerCellChangedEvent>(OnPowerCellChanged);
        SubscribeLocalEvent<StunbatonComponent, ItemToggledEvent>(OnToggled);
        SubscribeLocalEvent<StunbatonComponent, MapInitEvent>(OnMapInit);
    }

    /// <summary>
    /// Handle stamina damage application.
    /// Make sure the stunbaton is active and there's enough battery juice.
    /// </summary>
    private void OnStaminaHitAttempt(Entity<StunbatonComponent> entity, ref StaminaDamageOnHitAttemptEvent args)
    {
        if (!_itemToggle.IsActivated(entity.Owner) ||
            !_powerCell.TryGetBatteryFromSlotOrEntity(entity.Owner, out var battery) ||
            !_battery.TryUseCharge(battery.Value.AsNullable(), GetEnergyPerUse(entity, battery.Value)))
        {
            args.Cancelled = true;
        }
    }

    /// <summary>
    /// Communicate the stunbaton's status and number of remaining uses.
    /// </summary>
    private void OnExamined(Entity<StunbatonComponent> entity, ref ExaminedEvent args)
    {
        var onMsg = _itemToggle.IsActivated(entity.Owner)
        ? Loc.GetString("comp-stunbaton-examined-on")
        : Loc.GetString("comp-stunbaton-examined-off");
        args.PushMarkup(onMsg);

        if (_powerCell.TryGetBatteryFromSlotOrEntity(entity.Owner, out var battery))
        {
            var count = _battery.GetRemainingUses(battery.Value.AsNullable(), GetEnergyPerUse(entity, battery.Value));
            args.PushMarkup(Loc.GetString("melee-battery-examine", ("color", "yellow"), ("count", count)));
        }
    }

    /// <summary>
    /// Handle activation attempt.
    /// Make sure there's at least <see cref="StunbatonComponent.EnergyPerUse"/> left in the battery.
    /// </summary>
    private void TryTurnOn(Entity<StunbatonComponent> entity, ref ItemToggleActivateAttemptEvent args)
    {
        if (!_powerCell.TryGetBatteryFromSlotOrEntity(entity.Owner, out var battery))
        {
            args.Cancelled = true;
            if (args.User != null)
                _popup.PopupClient(Loc.GetString("power-cell-no-battery"), args.User.Value, args.User);
            return;
        }

        if (_battery.GetCharge(battery.Value.AsNullable()) < GetEnergyPerUse(entity, battery.Value))
        {
            args.Cancelled = true;
            if (args.User != null)
            {
                _popup.PopupPredicted(Loc.GetString("stunbaton-component-low-charge"), args.User.Value, args.User);
            }
            return;
        }
    }

    private void OnCellSlotEmpty(Entity<StunbatonComponent> entity, ref PowerCellSlotEmptyEvent args)
    {
        _itemToggle.TryDeactivate(entity.Owner);
    }

    private void OnPowerCellChanged(Entity<StunbatonComponent> entity, ref PowerCellChangedEvent args)
    {
        UpdateAppearance(entity);
    }

    private void OnToggled(Entity<StunbatonComponent> entity, ref ItemToggledEvent args)
    {
        UpdateAppearance(entity);
    }

    private void OnMapInit(Entity<StunbatonComponent> entity, ref MapInitEvent args)
    {
        UpdateAppearance(entity);
    }

    /// <summary>
    /// Update inhand and item sprite for removable power cells.
    /// Batons with integrated batteries are updated by <see cref="ItemTogglePrefixComponent"/>.
    /// </summary>
    private void UpdateAppearance(Entity<StunbatonComponent> entity)
    {
        if (!HasComp<PowerCellSlotComponent>(entity))
            return;

        if (_powerCell.HasBattery(entity.Owner))
        {
            _appearance.SetData(entity.Owner, StunbatonVisuals.NoCell, false);
            var prefix = _itemToggle.IsActivated(entity.Owner) ? OnPrefix : OffPrefix;
            _item.SetHeldPrefix(entity.Owner, prefix);
        }
        else
        {
            _appearance.SetData(entity.Owner, StunbatonVisuals.NoCell, true);
            _item.SetHeldPrefix(entity.Owner, NoCellPrefix);
        }
    }

    /// <summary>
    /// Turns off the stunbaton when battery level drops below the charge needed for a single hit.
    /// </summary>
    private void OnChargeChanged(Entity<StunbatonComponent> entity, ref ChargeChangedEvent args)
    {
        if (_powerCell.TryGetBatteryFromSlotOrEntity(entity.Owner, out var battery) &&
            _battery.GetCharge(battery.Value.AsNullable()) < GetEnergyPerUse(entity, battery.Value))
        {
            _itemToggle.TryDeactivate(entity.Owner, predicted: false);
        }
    }

    /// <summary>
    /// Get charge consumed per hit.
    /// See <see cref="StunbatonComponent.MaxChargeFraction"/>.
    /// </summary>
    private static float GetEnergyPerUse(Entity<StunbatonComponent> entity, Entity<BatteryComponent> battery)
    {
        return entity.Comp.EnergyPerUse + entity.Comp.MaxChargeFraction * battery.Comp.MaxCharge;
    }
}
