using Content.Shared.ActionBlocker;
using Content.Shared.Chat;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Popups;
using Content.Shared.StatusEffectNew;
using Content.Shared.StatusEffectNew.Components;

namespace Content.Shared.Speech.Muting;

/// <summary>
/// Handles the speech restrictions imposed by <see cref="MutedStatusEffectComponent"/>.
/// The mob is <see cref="MobCondition.Silenced"/> while the effect is applied.
/// </summary>
public sealed partial class MutedStatusEffectSystem : EntitySystem
{
    [Dependency] private MobCapabilitiesSystem _capabilities = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    [Dependency] private EntityQuery<StatusEffectComponent> _effectQuery = default!;

    /// <inheritdoc />
    public override void Initialize()
    {
        SubscribeLocalEvent<MutedStatusEffectComponent, StatusEffectAppliedEvent>(OnApplied);
        SubscribeLocalEvent<MutedStatusEffectComponent, StatusEffectRemovedEvent>(OnRemoved);
        SubscribeLocalEvent<MutedStatusEffectComponent, StatusEffectRelayedEvent<RefreshMobConditionsEvent>>(OnRefreshConditions);
        SubscribeLocalEvent<MutedStatusEffectComponent, StatusEffectRelayedEvent<GetMobConditionReasonEvent>>(OnGetReason);
        SubscribeLocalEvent<MutedStatusEffectComponent, StatusEffectRelayedEvent<EmoteEvent>>(OnEmote);
        SubscribeLocalEvent<MutedStatusEffectComponent, StatusEffectRelayedEvent<EmoteActionEvent>>(OnEmoteAction);
    }

    private void OnApplied(Entity<MutedStatusEffectComponent> ent, ref StatusEffectAppliedEvent args)
    {
        _capabilities.Refresh(args.Target);
    }

    private void OnRemoved(Entity<MutedStatusEffectComponent> ent, ref StatusEffectRemovedEvent args)
    {
        // Raised once the effect has left the mob's container, so the refresh no longer reaches it.
        _capabilities.Refresh(args.Target);
    }

    private void OnRefreshConditions(Entity<MutedStatusEffectComponent> ent,
        ref StatusEffectRelayedEvent<RefreshMobConditionsEvent> args)
    {
        // A delayed mute silences once it starts, which is when it refreshes.
        if (!_effectQuery.TryComp(ent, out var effect) || !effect.Applied)
            return;

        // Args is a property: add to a copy and write it back.
        var conditions = args.Args;
        conditions.Add(MobCondition.Silenced);
        args.Args = conditions;
    }

    private void OnGetReason(Entity<MutedStatusEffectComponent> ent,
        ref StatusEffectRelayedEvent<GetMobConditionReasonEvent> args)
    {
        if (args.Args.Condition == MobCondition.Silenced && args.Args.Reason == null)
            args.Args = args.Args with { Reason = ent.Comp.SpeakPopup };
    }

    private void OnEmote(Entity<MutedStatusEffectComponent> ent, ref StatusEffectRelayedEvent<EmoteEvent> args)
    {
        if (args.Args.Handled)
            return;

        // Still leaves the text so it looks like they are pantomiming a laugh.
        if (args.Args.Emote.Category.HasFlag(EmoteCategory.Vocal))
        {
            args.Args = args.Args with { Handled = true };
        }
    }

    private void OnEmoteAction(Entity<MutedStatusEffectComponent> ent, ref StatusEffectRelayedEvent<EmoteActionEvent> args)
    {
        if (args.Args.Handled)
            return;

        _popup.PopupEntity(Loc.GetString(ent.Comp.ActionPopup), args.AppliedTo, args.AppliedTo);
        args.Args.Handled = true;
    }
}
