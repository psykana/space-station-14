using Content.Shared.CCVar;
using Content.Shared.Damage.ForceSay;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Timing;

namespace Content.Shared.ActionBlocker;

/// <summary>
/// The bookkeeper: owns the decision of what an entity can do, and is replacing <see cref="ActionBlockerSystem"/>.
/// Producers declare <see cref="MobCondition"/>s through <see cref="RefreshMobConditionsEvent"/>; this system
/// cascades them and maps them to blocked <see cref="MobAction"/>s. Consumers ask
/// <see cref="CanPerform(EntityUid, in InteractionRequirements, EntityUid?, EntityUid?)"/>.
/// </summary>
public sealed partial class MobCapabilitiesSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private INetManager _net = default!;

    [Dependency] private EntityQuery<MobConditionsComponent> _query = default!;
    [Dependency] private EntityQuery<AllowNextCritSpeechComponent> _critSpeechQuery = default!;

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
                expanded |= MobCondition.UiBlocked | MobCondition.PullBlocked;

            if (expanded == conditions)
                return conditions;

            conditions = expanded;
        }
    }

    /// <summary>
    /// The single place where a condition blocks an action.
    /// </summary>
    public static MobAction GetBlocked(MobCondition conditions)
    {
        var blocked = MobAction.None;

        if ((conditions & MobCondition.Unconscious) != 0)
            blocked |= MobAction.Conscious | MobAction.Speak | MobAction.Emote | MobAction.Point | MobAction.StandUp;

        if ((conditions & MobCondition.Incapacitated) != 0)
        {
            blocked |= MobAction.Interact | MobAction.UseHeld | MobAction.Pickup | MobAction.Drop |
                       MobAction.Attack | MobAction.Throw | MobAction.ChangeDirection;
        }

        if ((conditions & MobCondition.HandsBlocked) != 0)
            blocked |= MobAction.Interact | MobAction.UseHeld | MobAction.Pickup | MobAction.Drop | MobAction.Attack;

        if ((conditions & MobCondition.Incorporeal) != 0)
            blocked |= MobAction.Interact | MobAction.UseHeld | MobAction.Pickup | MobAction.Drop | MobAction.Emote;

        if ((conditions & MobCondition.Immobilized) != 0)
            blocked |= MobAction.Move;

        if ((conditions & MobCondition.Floored) != 0)
            blocked |= MobAction.Stand;

        if ((conditions & MobCondition.UiBlocked) != 0)
            blocked |= MobAction.UiAccess;

        if ((conditions & MobCondition.PullBlocked) != 0)
            blocked |= MobAction.Pull;

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
    /// Every declared condition that would block <paramref name="action"/> on its own, once
    /// <paramref name="ignoring"/> is forgiven.
    /// </summary>
    public static MobCondition GetBlockingConditions(MobCondition declared,
        MobAction action,
        MobCondition ignoring = MobCondition.None)
    {
        var result = MobCondition.None;

        for (var bit = 1; bit <= (int) MobCondition.Incorporeal; bit <<= 1)
        {
            var condition = (MobCondition) bit;
            if ((declared & condition) == 0)
                continue;

            if ((GetBlocked(Effective(condition, ignoring)) & action) != 0)
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
    public void Refresh(Entity<MobConditionsComponent?> ent)
    {
        if (!_query.Resolve(ent, ref ent.Comp, false) || TerminatingOrDeleted(ent))
            return;

        // Declared arrives with the server state.
        if (_timing.ApplyingState)
            return;

        if (!_polling.Add(ent))
        {
            Log.Error($"Re-entrant {nameof(Refresh)} of {ToPrettyString(ent)}. {nameof(RefreshMobConditionsEvent)} handlers must only call Add.");
            return;
        }

        MobCondition declared;
        try
        {
            declared = GetDeclared(ent);
        }
        finally
        {
            _polling.Remove(ent);
        }

        if (declared == ent.Comp.Declared)
            return;

        var oldConditions = ent.Comp.Conditions;
        var oldBlocked = ent.Comp.Blocked;

        // Commit before anything downstream runs: UpdateCanMove handlers query CanInteract.
        ent.Comp.Declared = declared;
        Derive(ent.Comp);
        Dirty(ent);

        if (((oldBlocked ^ ent.Comp.Blocked) & MobAction.Move) != 0)
            UpdateCanMove(ent);

        if (oldConditions != ent.Comp.Conditions)
        {
            // Broadcast too: only one system may subscribe to a directed event per component.
            var ev = new MobConditionsChangedEvent(ent, oldConditions, ent.Comp.Conditions);
            RaiseLocalEvent(ent, ref ev, true);
        }
    }

    /// <summary>
    /// Adds <see cref="MobConditionsComponent"/> if it is missing. Does nothing while applying a server state or
    /// for an entity that is being deleted.
    /// </summary>
    public void EnsureConditions(EntityUid uid)
    {
        if (_timing.ApplyingState || TerminatingOrDeleted(uid))
            return;

        EnsureComp<MobConditionsComponent>(uid);
    }

    /// <summary>
    /// Whether the mob has any of <paramref name="condition"/>, after the cascade.
    /// </summary>
    public bool HasCondition(Entity<MobConditionsComponent?> ent, MobCondition condition)
    {
        return _query.Resolve(ent, ref ent.Comp, false) && (ent.Comp.Conditions & condition) != 0;
    }

    /// <summary>
    /// Whether any of <paramref name="action"/> is blocked for this mob.
    /// </summary>
    public bool IsBlocked(Entity<MobConditionsComponent?> ent, MobAction action)
    {
        if (!_query.Resolve(ent, ref ent.Comp, false))
            return false;

        Verify((ent, ent.Comp));
        return (ent.Comp.Blocked & action) != 0;
    }

    /// <summary>
    /// Whether any of <paramref name="action"/> is blocked for this mob, forgiving <paramref name="ignoring"/>.
    /// </summary>
    public bool IsBlocked(Entity<MobConditionsComponent?> ent, MobAction action, MobCondition ignoring)
    {
        if (ignoring == MobCondition.None)
            return IsBlocked(ent, action);

        if (!_query.Resolve(ent, ref ent.Comp, false))
            return false;

        Verify((ent, ent.Comp));
        return (GetBlocked(Effective(ent.Comp.Declared, ignoring)) & action) != 0;
    }

    /// <summary>
    /// Every declared condition that blocks <paramref name="action"/> on its own, once <paramref name="ignoring"/>
    /// is forgiven.
    /// </summary>
    public MobCondition GetBlockingConditions(Entity<MobConditionsComponent?> ent,
        MobAction action,
        MobCondition ignoring = MobCondition.None)
    {
        if (!_query.Resolve(ent, ref ent.Comp, false))
            return MobCondition.None;

        return GetBlockingConditions(ent.Comp.Declared, action, ignoring);
    }

    /// <summary>
    /// Gets a localized "you can't do that while ..." message for why <paramref name="action"/> is blocked.
    /// </summary>
    public bool TryGetBlockedReason(Entity<MobConditionsComponent?> ent,
        MobAction action,
        out string reason,
        MobCondition ignoring = MobCondition.None)
    {
        var condition = PickReason(GetBlockingConditions(ent, action, ignoring));
        if (condition == MobCondition.None)
        {
            reason = string.Empty;
            return false;
        }

        reason = Loc.GetString($"mob-condition-blocked-{condition.ToString().ToLowerInvariant()}");
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
        if (((oldBlocked ^ ent.Comp.Blocked) & MobAction.Move) != 0)
            UpdateCanMove(ent);
    }

    #endregion


    #region Implementation

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
