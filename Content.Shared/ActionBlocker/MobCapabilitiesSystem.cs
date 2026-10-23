using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Timing;

namespace Content.Shared.ActionBlocker;

/// <summary>
/// The bookkeeper: owns the decision of what an entity can do, and is replacing <see cref="ActionBlockerSystem"/>.
/// Producers declare <see cref="MobCondition"/>s through <see cref="RefreshMobConditionsEvent"/>; this system
/// cascades them and maps them to blocked <see cref="MobCapability"/>s. Consumers ask
/// <see cref="CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/>.
/// </summary>
public sealed partial class MobCapabilitiesSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private INetManager _net = default!;

    [Dependency] private EntityQuery<MobConditionsComponent> _query = default!;

    /// <summary>
    /// The conditions a producer may declare, in the order failure reasons are picked.
    /// </summary>
    private static readonly MobCondition[] ReasonPriority =
    [
        MobCondition.Unconscious,
        MobCondition.Incorporeal,
        MobCondition.Incapacitated,
        MobCondition.Restrained,
        MobCondition.HandsBlocked,
        MobCondition.Immobilized,
        MobCondition.Floored,
        MobCondition.Silenced,
    ];

    /// <summary>
    /// Entities whose producers are currently being polled. Guards against re-entrant refreshes.
    /// </summary>
    private readonly HashSet<EntityUid> _polling = new();

    private bool _verify;

    public override void Initialize()
    {
        base.Initialize();

        Subs.CVar(_cfg, CCVars.MobConditionsVerify, value => _verify = value, true);
    }

    #region Tables

    /// <summary>
    /// Applies the cascade until nothing changes.
    /// </summary>
    public static MobCondition Expand(MobCondition conditions)
    {
        while (true)
        {
            var expanded = conditions;

            if ((expanded & MobCondition.Unconscious) != 0)
            {
                expanded |= MobCondition.Incapacitated | MobCondition.Immobilized | MobCondition.Floored |
                            MobCondition.HandsBlocked;
            }

            if ((expanded & MobCondition.Restrained) != 0)
                expanded |= MobCondition.HandsBlocked;

            if ((expanded & (MobCondition.Incapacitated | MobCondition.HandsBlocked)) != 0)
                expanded |= MobCondition.PullBlocked;

            if (expanded == conditions)
                return conditions;

            conditions = expanded;
        }
    }

    /// <summary>
    /// The single place where a condition blocks a capability.
    /// </summary>
    public static MobCapability GetBlocked(MobCondition conditions)
    {
        var blocked = MobCapability.None;

        if ((conditions & MobCondition.Unconscious) != 0)
            blocked |= MobCapability.Conscious | MobCapability.Speak | MobCapability.Emote | MobCapability.Point | MobCapability.StandUp;

        if ((conditions & MobCondition.Incapacitated) != 0)
        {
            blocked |= MobCapability.Interact | MobCapability.UseHeld | MobCapability.Pickup | MobCapability.Drop |
                       MobCapability.Attack | MobCapability.Throw | MobCapability.ChangeDirection;
        }

        if ((conditions & MobCondition.HandsBlocked) != 0)
            blocked |= MobCapability.Interact | MobCapability.UseHeld | MobCapability.Pickup | MobCapability.Drop | MobCapability.Attack;

        if ((conditions & MobCondition.Incorporeal) != 0)
            blocked |= MobCapability.Interact | MobCapability.UseHeld | MobCapability.Pickup | MobCapability.Drop | MobCapability.Emote;

        if ((conditions & MobCondition.Immobilized) != 0)
            blocked |= MobCapability.Move;

        if ((conditions & MobCondition.Floored) != 0)
            blocked |= MobCapability.Stand;

        if ((conditions & MobCondition.PullBlocked) != 0)
            blocked |= MobCapability.Pull;

        if ((conditions & MobCondition.Silenced) != 0)
            blocked |= MobCapability.Speak;

        return blocked;
    }

    /// <summary>
    /// The conditions in effect when a caller forgives <paramref name="ignoring"/>. The mask is applied before the
    /// cascade, so forgiving a declared condition forgives what it alone implies, and again after it, so forgiving a
    /// derived condition works too. An independent cause of the same action is never forgiven.
    /// </summary>
    public static MobCondition Effective(MobCondition declared, MobCondition ignoring)
    {
        return Expand(declared & ~ignoring) & ~ignoring;
    }

    /// <summary>
    /// Every declared condition that would block <paramref name="required"/> on its own, once
    /// <paramref name="ignoring"/> is forgiven.
    /// </summary>
    public static MobCondition GetBlockingConditions(MobCondition declared,
        MobCapability required,
        MobCondition ignoring = MobCondition.None)
    {
        var result = MobCondition.None;

        // Only declarable conditions can be in the declared set.
        foreach (var condition in ReasonPriority)
        {
            if ((declared & condition) == 0)
                continue;

            if ((GetBlocked(Effective(condition, ignoring)) & required) != 0)
                result |= condition;
        }

        return result;
    }

    /// <summary>
    /// Picks the condition to explain a failure with, by a fixed priority.
    /// </summary>
    public static MobCondition PickReason(MobCondition conditions)
    {
        foreach (var condition in ReasonPriority)
        {
            if ((conditions & condition) != 0)
                return condition;
        }

        return MobCondition.None;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Re-polls every producer and caches the result. Producers call this whenever their own state changes,
    /// before anything else can observe the change.
    /// </summary>
    /// <remarks>
    /// An entity without <see cref="MobConditionsComponent"/> gets one the first time a producer declares something
    /// on it, so producers never need to add it themselves. Mobs normally have it from their prototype.
    /// </remarks>
    public void Refresh(Entity<MobConditionsComponent?> ent)
    {
        // Declared arrives with the server state.
        if (_timing.ApplyingState || TerminatingOrDeleted(ent))
            return;

        if (!TryPoll(ent, out var declared))
            return;

        if (!_query.Resolve(ent, ref ent.Comp, false))
        {
            // The component's startup refreshes again and commits.
            if (declared != MobCondition.None)
                AddComp<MobConditionsComponent>(ent);

            return;
        }

        if (declared == ent.Comp.Declared)
            return;

        var oldConditions = ent.Comp.Conditions;
        var oldBlocked = ent.Comp.Blocked;

        // Commit before anything downstream runs: UpdateCanMove handlers query CanInteract.
        ent.Comp.Declared = declared;
        Derive(ent.Comp);
        Dirty(ent);

        if (((oldBlocked ^ ent.Comp.Blocked) & MobCapability.Move) != 0)
            UpdateCanMove(ent);

        if (oldConditions != ent.Comp.Conditions)
        {
            // Broadcast too: only one system may subscribe to a directed event per component.
            var ev = new MobConditionsChangedEvent(ent, oldConditions, ent.Comp.Conditions);
            RaiseLocalEvent(ent, ref ev, true);
        }
    }

    /// <summary>
    /// Whether the mob has any of <paramref name="condition"/>, after the cascade.
    /// </summary>
    public bool HasCondition(Entity<MobConditionsComponent?> ent, MobCondition condition)
    {
        return _query.Resolve(ent, ref ent.Comp, false) && (ent.Comp.Conditions & condition) != 0;
    }

    /// <summary>
    /// Whether any of <paramref name="required"/> is blocked for this mob.
    /// </summary>
    public bool IsBlocked(Entity<MobConditionsComponent?> ent, MobCapability required)
    {
        if (!_query.Resolve(ent, ref ent.Comp, false))
            return false;

        Verify((ent, ent.Comp));
        return (ent.Comp.Blocked & required) != 0;
    }

    /// <summary>
    /// Whether any of <paramref name="required"/> is blocked for this mob, forgiving <paramref name="ignoring"/>.
    /// </summary>
    public bool IsBlocked(Entity<MobConditionsComponent?> ent, MobCapability required, MobCondition ignoring)
    {
        if (ignoring == MobCondition.None)
            return IsBlocked(ent, required);

        if (!_query.Resolve(ent, ref ent.Comp, false))
            return false;

        Verify((ent, ent.Comp));
        return (GetBlocked(Effective(ent.Comp.Declared, ignoring)) & required) != 0;
    }

    /// <summary>
    /// Every declared condition that blocks <paramref name="required"/> on its own, once <paramref name="ignoring"/>
    /// is forgiven.
    /// </summary>
    public MobCondition GetBlockingConditions(Entity<MobConditionsComponent?> ent,
        MobCapability required,
        MobCondition ignoring = MobCondition.None)
    {
        if (!_query.Resolve(ent, ref ent.Comp, false))
            return MobCondition.None;

        return GetBlockingConditions(ent.Comp.Declared, required, ignoring);
    }

    /// <summary>
    /// Gets a localized "you can't do that while ..." message for why <paramref name="required"/> is blocked.
    /// </summary>
    public bool TryGetBlockedReason(Entity<MobConditionsComponent?> ent,
        MobCapability required,
        out string reason,
        MobCondition ignoring = MobCondition.None)
    {
        var condition = PickReason(GetBlockingConditions(ent, required, ignoring));
        if (condition == MobCondition.None)
        {
            reason = string.Empty;
            return false;
        }

        // Whatever declared the condition may word it.
        var ev = new GetMobConditionReasonEvent(condition, required);
        RaiseLocalEvent(ent, ref ev);

        reason = ev.Reason is { } custom
            ? Loc.GetString(custom)
            : Loc.GetString($"mob-condition-blocked-{condition.ToString().ToLowerInvariant()}");
        return true;
    }

    #endregion

    #region Lifecycle

    [SubscribeLocalEvent]
    private void OnStartup(Entity<MobConditionsComponent> ent, ref ComponentStartup args)
    {
        Refresh((ent, ent.Comp));
    }

    [SubscribeLocalEvent]
    private void OnHandleState(Entity<MobConditionsComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        var oldBlocked = ent.Comp.Blocked;
        Derive(ent.Comp);

        // Producers may have recomputed CanMove earlier in this state application, against the stale cache.
        if (((oldBlocked ^ ent.Comp.Blocked) & MobCapability.Move) != 0)
            UpdateCanMove(ent);
    }

    #endregion


    #region Implementation

    /// <summary>
    /// Polls the producers, refusing a re-entrant poll of the same entity.
    /// </summary>
    private bool TryPoll(EntityUid uid, out MobCondition declared)
    {
        declared = MobCondition.None;

        if (!_polling.Add(uid))
        {
            Log.Error($"Re-entrant {nameof(Refresh)} of {ToPrettyString(uid)}. {nameof(RefreshMobConditionsEvent)} handlers must only call Add.");
            return false;
        }

        try
        {
            declared = GetDeclared(uid);
            return true;
        }
        finally
        {
            _polling.Remove(uid);
        }
    }

    private MobCondition GetDeclared(EntityUid uid)
    {
        var ev = new RefreshMobConditionsEvent();
        RaiseLocalEvent(uid, ref ev);
        return ev.Conditions;
    }

    private static void Derive(MobConditionsComponent comp)
    {
        comp.Conditions = Expand(comp.Declared);
        comp.Blocked = GetBlocked(comp.Conditions);
    }

    /// <summary>
    /// Asserts that the cache matches a fresh poll, to catch producers that forgot to refresh.
    /// Enabled by <see cref="CCVars.MobConditionsVerify"/>, which integration tests turn on.
    /// </summary>
    private void Verify(Entity<MobConditionsComponent> ent)
    {
        // A deleting entity's producers shut down one by one, so its cache is legitimately behind.
        if (!_verify || !_net.IsServer || _timing.ApplyingState || _polling.Contains(ent) || TerminatingOrDeleted(ent))
            return;

        _polling.Add(ent);
        try
        {
            var declared = GetDeclared(ent);
            if (declared != ent.Comp.Declared)
                Log.Error($"Stale mob conditions on {ToPrettyString(ent)}: cached {ent.Comp.Declared}, producers declare {declared}. A producer changed state without calling {nameof(Refresh)}.");
        }
        finally
        {
            _polling.Remove(ent);
        }
    }

    #endregion
}
