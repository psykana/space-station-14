using Content.Shared.Database;
using Content.Shared.Eye;
using Content.Shared.Hands;
using Content.Shared.Interaction;
using Content.Shared.Inventory.Events;
using Content.Shared.Verbs;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Network;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Shared.SubFloor;

public abstract class SharedTrayScannerSystem : EntitySystem
{
    [Dependency] private readonly INetManager _netMan = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedEyeSystem _eye = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;

    public const float SubfloorRevealAlpha = 0.8f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TrayScannerComponent, ComponentGetState>(OnTrayScannerGetState);
        SubscribeLocalEvent<TrayScannerComponent, ComponentHandleState>(OnTrayScannerHandleState);
        SubscribeLocalEvent<TrayScannerComponent, ActivateInWorldEvent>(OnTrayScannerActivate);

        SubscribeLocalEvent<TrayScannerComponent, GotEquippedHandEvent>(OnTrayHandEquipped);
        SubscribeLocalEvent<TrayScannerComponent, GotUnequippedHandEvent>(OnTrayHandUnequipped);
        SubscribeLocalEvent<TrayScannerComponent, GotEquippedEvent>(OnTrayEquipped);
        SubscribeLocalEvent<TrayScannerComponent, GotUnequippedEvent>(OnTrayUnequipped);

        SubscribeLocalEvent<TrayScannerUserComponent, GetVisMaskEvent>(OnUserGetVis);

        SubscribeLocalEvent<TrayScannerComponent, GetVerbsEvent<AlternativeVerb>>(OnAddSwitchModeVerb);
    }

    private void OnAddSwitchModeVerb(EntityUid uid, TrayScannerComponent configurator, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !args.Using.HasValue || !HasComp<TrayScannerComponent>(args.Target) || !configurator.Enabled)
            return;

        AlternativeVerb verb = new()
        {
            Text = Loc.GetString("network-configurator-switch-mode"),
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/settings.svg.192dpi.png")),
            Act = () => SwitchMode(args.User, args.Target, configurator),
            Impact = LogImpact.Low
        };
        args.Verbs.Add(verb);
    }

    /// <summary>
    /// Returns true if the last time this method was called is earlier than the configurators use delay.
    /// </summary>
    private bool Delay(TrayScannerComponent configurator)
    {
        var currentTime = _gameTiming.CurTime;
        if (currentTime < configurator.LastUseAttempt + configurator.UseDelay)
            return true;

        configurator.LastUseAttempt = currentTime;
        return false;
    }

    private static TrayScannerMode Next(TrayScannerMode myEnum)
    {
        switch (myEnum)
        {
            case TrayScannerMode.All:
                return TrayScannerMode.Wiring;
            case TrayScannerMode.Wiring:
                return TrayScannerMode.Piping;
            case TrayScannerMode.Piping:
                return TrayScannerMode.All;
            default:
                return TrayScannerMode.All;
        }
    }
    private void UpdateModeAppearance(EntityUid userUid, EntityUid configuratorUid, TrayScannerComponent configurator)
    {
        Dirty(configuratorUid, configurator);
        //_appearance.SetData(configuratorUid, NetworkConfiguratorVisuals.Mode, configurator.LinkModeActive);

        var pitch = configurator.Mode == TrayScannerMode.All ? 1 : 0.8f;
        _audio.PlayPredicted(configurator.SoundSwitchMode, configuratorUid, userUid, AudioParams.Default.WithVolume(1.5f).WithPitchScale(pitch));
    }

    private void SwitchMode(EntityUid? userUid, EntityUid configuratorUid, TrayScannerComponent configurator)
    {
        if (Delay(configurator))
            return;

        //configurator.LinkModeActive = !configurator.LinkModeActive;

        if (!userUid.HasValue)
            return;

        configurator.Mode = Next(configurator.Mode);

        //if (!configurator.LinkModeActive)
        //    configurator.ActiveDeviceLink = null;

        UpdateModeAppearance(userUid.Value, configuratorUid, configurator);
    }

    private void OnUserGetVis(Entity<TrayScannerUserComponent> ent, ref GetVisMaskEvent args)
    {
        args.VisibilityMask |= (int)VisibilityFlags.Subfloor;
    }

    private void OnEquip(EntityUid user)
    {
        if (_netMan.IsClient)
            return;

        var comp = EnsureComp<TrayScannerUserComponent>(user);
        comp.Count++;

        if (comp.Count > 1)
            return;

        _eye.RefreshVisibilityMask(user);
    }

    private void OnUnequip(EntityUid user)
    {
        if (_netMan.IsClient)
            return;

        if (!TryComp(user, out TrayScannerUserComponent? comp))
            return;

        comp.Count--;

        if (comp.Count > 0)
            return;

        RemComp<TrayScannerUserComponent>(user);
        _eye.RefreshVisibilityMask(user);
    }

    private void OnTrayHandUnequipped(Entity<TrayScannerComponent> ent, ref GotUnequippedHandEvent args)
    {
        OnUnequip(args.User);
    }

    private void OnTrayHandEquipped(Entity<TrayScannerComponent> ent, ref GotEquippedHandEvent args)
    {
        OnEquip(args.User);
    }

    private void OnTrayUnequipped(Entity<TrayScannerComponent> ent, ref GotUnequippedEvent args)
    {
        OnUnequip(args.Equipee);
    }

    private void OnTrayEquipped(Entity<TrayScannerComponent> ent, ref GotEquippedEvent args)
    {
        OnEquip(args.Equipee);
    }

    private void OnTrayScannerActivate(EntityUid uid, TrayScannerComponent scanner, ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        SetScannerEnabled(uid, !scanner.Enabled, scanner);
        args.Handled = true;
    }

    private void SetScannerEnabled(EntityUid uid, bool enabled, TrayScannerComponent? scanner = null)
    {
        if (!Resolve(uid, ref scanner) || scanner.Enabled == enabled)
            return;

        scanner.Enabled = enabled;
        Dirty(uid, scanner);

        // We don't remove from _activeScanners on disabled, because the update function will handle that, as well as
        // managing the revealed subfloor entities

        if (TryComp<AppearanceComponent>(uid, out var appearance))
        {
            _appearance.SetData(uid, TrayScannerVisual.Visual, scanner.Enabled ? TrayScannerVisual.On : TrayScannerVisual.Off, appearance);
        }
    }

    private void OnTrayScannerGetState(EntityUid uid, TrayScannerComponent scanner, ref ComponentGetState args)
    {
        args.State = new TrayScannerState(scanner.Enabled, scanner.Mode, scanner.Range);
    }

    private void OnTrayScannerHandleState(EntityUid uid, TrayScannerComponent scanner, ref ComponentHandleState args)
    {
        if (args.Current is not TrayScannerState state)
            return;

        scanner.Range = state.Range;
        scanner.Mode = state.Mode;
        SetScannerEnabled(uid, state.Enabled, scanner);
    }
}

[Serializable, NetSerializable]
public enum TrayScannerVisual : sbyte
{
    Visual,
    On,
    Off
}
