using Robust.Shared.GameStates;

namespace Content.Shared.ActionBlocker;

/// <summary>
/// Caches the conditions declared on a mob and the actions they block.
/// Only <see cref="Declared"/> is networked; the rest is derived from it.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
[Access(typeof(MobCapabilitiesSystem))]
public sealed partial class MobConditionsComponent : Component
{
    /// <summary>
    /// Union of everything producers declared on the last refresh, before the cascade.
    /// Ignore masks are evaluated against this.
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public MobCondition Declared;

    /// <summary>
    /// <see cref="Declared"/> after the cascade.
    /// </summary>
    [ViewVariables]
    public MobCondition Conditions;

    /// <summary>
    /// The actions blocked by <see cref="Conditions"/>.
    /// </summary>
    [ViewVariables]
    public MobCapability Blocked;
}

/// <summary>
/// Raised on a mob to rebuild its conditions. Handlers call <see cref="Add"/> and do nothing else.
/// </summary>
[ByRefEvent]
public record struct RefreshMobConditionsEvent
{
    public MobCondition Conditions { get; private set; }

    public void Add(MobCondition conditions)
    {
        Conditions |= conditions;
    }
}

/// <summary>
/// Raised on a mob, and broadcast, after its cascaded conditions changed, once the new state has been committed.
/// Subscribe directed on your own component, or to the broadcast. Don't subscribe directed on
/// <see cref="MobConditionsComponent"/>: only one system may hold that subscription.
/// </summary>
[ByRefEvent]
public readonly record struct MobConditionsChangedEvent(EntityUid Uid, MobCondition Old, MobCondition New)
{
    public MobCondition Added => New & ~Old;
    public MobCondition Removed => Old & ~New;
}
