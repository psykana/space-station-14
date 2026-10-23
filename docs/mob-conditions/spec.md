# MobCondition: implementation spec

This is the normative companion to [design.md](design.md). The design explains *why*; this
document says *what to build*, precisely enough to implement without re-researching.

Citations are against upstream `6a9a8abe96` (2026-10-20). "MUST", "SHOULD" and "MAY" are
used in the RFC 2119 sense.

## 1. Scope

**Goal.** One bookkeeper system answers "can this entity do X?". It replaces
`ActionBlockerSystem` and the attempt events it raises:
- **Producers** declare what is wrong with an entity.
- **Consumers** ask the bookkeeper. No other system decides whether an action is blocked.

§15 specifies that end state. §16 classifies every current attempt-event subscriber into the
bucket it moves to. Sections 2–14 specify the first steps toward it.

**Covered:**
- One closed vocabulary of mob conditions and one of mob actions.
- A cached per-entity component.
- The bookkeeper, which derives blocked actions from conditions and, during the migration,
  still cancels the existing attempt events.
- A consumer-side requirements struct with ignore masks, used by interactions, UIs and the
  action bar.
- UI access tiers.
- The end-state design (§15) and the attempt-event inventory (§16).

**Not covered:**
- Perception and chat filtering (design §8).

**Migration rule (temporary).** This is an upstream repository, so downstream forks get a
deprecation window instead of a break:
- **`ActionBlockerSystem`** is `[Obsolete]`. Each `CanX()` keeps its signature and semantics and
  forwards to the bookkeeper (§15.5). It is removed once the new API has been stable for a
  release.
- **An attempt event whose last upstream subscriber has moved out (§16)** is marked
  `[Obsolete]`, with a message naming the action to check instead. The bookkeeper keeps raising
  it for downstream subscribers until the removal release, suppressing the warning only at its
  own raise site.
- Unmigrated subscribers keep working unchanged until then.

## 2. Types

All new types live in `Content.Shared/ActionBlocker/` under namespace `Content.Shared.ActionBlocker`,
except the UI types, which go in `Content.Shared/UserInterface/` under `Content.Shared.UserInterface`.

### 2.1 `MobCondition` — `MobCondition.cs`

```csharp
[Flags, Serializable, NetSerializable]
public enum MobCondition : ushort
{
    None          = 0,
    Unconscious   = 1 << 0,  // the mind is unavailable: asleep, critical, dead
    Incapacitated = 1 << 1,  // generally unable to act; no specific cause implied
    Restrained    = 1 << 2,  // bound but awake; blocks nothing directly (acts via HandsBlocked)
    HandsBlocked  = 1 << 3,  // cannot use manipulators
    Immobilized   = 1 << 4,  // cannot move voluntarily
    Floored       = 1 << 5,  // cannot be upright
    PullBlocked   = 1 << 6,  // cannot pull
    Incorporeal   = 1 << 7,  // no physical presence (spectating ghost)
    Silenced      = 1 << 8,  // cannot speak: muted, a mime's vow
}
```

**Rules:**
- A member MUST NOT be added unless something sets it, either a producer declares it or the
  cascade (§3.1) derives it. In the end state, the vocabulary is exactly the bookkeeper's API
  plus what its producers declare (§15.6).
- `PullBlocked` is derived-only and changes no behaviour on its own:
  - `PullingSystem.CanPull` (`Movement/Pulling/Systems/PullingSystem.cs:441–451`) already
    requires an empty hand and `CanInteract`.
  - It exists for tg parity and so that a call site can forgive it (§4.3).
- `UiBlocked`, and the `UiAccess` capability it blocks, come back with the UI phase (§8). They
  were removed while nothing consumed them.
- Stasis and Grabbed are deliberately absent (design §3).
- *Declarable* members are the ones a producer may `Add`: `Unconscious`, `Incapacitated`,
  `Restrained`, `HandsBlocked`, `Immobilized`, `Floored`, `Incorporeal`, `Silenced`.
  `PullBlocked` SHOULD only be derived.

### 2.2 `MobCapability` — `MobCondition.cs`

Things a mob can do. A requirement is a mask of them (§2.5): a caller asks for every capability
its act needs, and the act is blocked if any of them is. Each member names the legacy attempt
event the bookkeeper still raises for it.

| Member | Bit | Attempt event (namespace) |
|---|---|---|
| `Interact` | 0 | `InteractionAttemptEvent` (`Content.Shared.Interaction.Events`) |
| `Conscious` | 1 | `ConsciousAttemptEvent` (`Content.Shared.Interaction.Events`) |
| `UseHeld` | 2 | `UseAttemptEvent` (`Content.Shared.Interaction.Events`) |
| `Pickup` | 3 | `PickupAttemptEvent` (`Content.Shared.Item`) |
| `Drop` | 4 | `DropAttemptEvent` (`Content.Shared.Hands`) |
| `Attack` | 5 | `AttackAttemptEvent` (`Content.Shared.Interaction.Events`) |
| `Throw` | 6 | `ThrowAttemptEvent` (`Content.Shared.Throwing`) |
| `Speak` | 7 | `SpeakAttemptEvent` (`Content.Shared.Speech`) |
| `Emote` | 8 | `EmoteAttemptEvent` (`Content.Shared.Emoting`) |
| `Point` | 9 | `PointAttemptEvent` (`Content.Shared.Pointing`) |
| `Move` | 10 | `UpdateCanMoveEvent` (`Content.Shared.Movement.Events`) |
| `Stand` | 11 | `StandAttemptEvent` (`Content.Shared.Standing`) |
| `Pull` | 12 | `StartPullAttemptEvent` (`Content.Shared.Pulling.Events`; the file is under `Movement/Pulling/Events/`) |
| `ChangeDirection` | 13 | `ChangeDirectionAttemptEvent` (`Content.Shared.Interaction.Events`) |
| `StandUp` | 14 | `StandUpAttemptEvent` (`Content.Shared.Stunnable`, by-ref) |
| `Down` | 15 | `DownAttemptEvent` (`Content.Shared.Standing`). Not blocked by `Unconscious`: crit and death lie the mob down. |
| `Complex` | 16 | none. A capability, not blocked by any condition. Today it means having `ComplexInteractionComponent`; in the end state it comes from the creature's profile (§15.4). |

`enum MobCapability : uint`, `[Flags, Serializable, NetSerializable]`, `None = 0`.

### 2.3 `MobConditionsComponent` — `MobConditionsComponent.cs`

```csharp
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
[Access(typeof(MobCapabilitiesSystem))]
public sealed partial class MobConditionsComponent : Component
{
    [ViewVariables, AutoNetworkedField] public MobCondition Declared; // union of producer Adds, pre-cascade
    [ViewVariables] public MobCondition Conditions;                   // Expand(Declared)
    [ViewVariables] public MobCapability Blocked;                     // GetBlocked(Conditions)
}
```

- `Declared` is the only networked field. `MobCapabilitiesSystem` recomputes `Conditions` and
  `Blocked` from it in `AfterAutoHandleStateEvent`, so derived state can't drift from what the
  server sent.
- Storing the pre-cascade set is what makes ignore masks cheap and predictable (§4.3):
  - Forgiving a condition is pure bit math on `Declared`.
  - It never re-polls producers, which the client could only do for client-visible sources.
- It MUST hold nothing else, because it sits on every mob.
- It is not a `[DataField]`. Its state is always derived.

### 2.4 Events — `MobConditionsComponent.cs`

```csharp
/// Raised on the mob to rebuild its conditions. Sources call Add.
[ByRefEvent]
public record struct RefreshMobConditionsEvent
{
    public MobCondition Conditions { get; private set; }
    public void Add(MobCondition conditions) => Conditions |= conditions;
}

/// Raised on the mob after Conditions actually changed, after the new state is committed.
[ByRefEvent]
public readonly record struct MobConditionsChangedEvent(MobCondition Old, MobCondition New)
{
    public MobCondition Added => New & ~Old;
    public MobCondition Removed => Old & ~New;
}
```

`RefreshMobConditionsEvent` is a by-ref struct; the prototype used a class. That avoids an
allocation per refresh. It also allows `Entity<T>, ref` handlers in the `[SubscribeLocalEvent]`
attribute style. The precedent is `RefreshFrictionModifiersEvent`
(`Movement/Systems/MovementSpeedModifierSystem.cs:265`), whose refresh likewise returns early
while `ApplyingState` and dirties only on change.

`RefreshMobConditionsEvent` is raised only by `Refresh` (§4.2) and, in DEBUG, by the
consistency check (§4.5). Nothing on a hot path raises it.

```csharp
/// Raised on the mob when a failure is explained by Condition, so whatever declared it can word it.
[ByRefEvent]
public record struct GetMobConditionReasonEvent(MobCondition Condition, MobCapability Required)
{
    public LocId? Reason;
}
```

`TryGetBlockedReason` raises it after picking the condition (§10.2), and status effects relay it.
A handler leaves `Reason` alone if it is already set. It is only raised on failure. The muted
status effect uses it to show a mime's vow instead of a plain mute.

### 2.5 `InteractionRequirements` — `InteractionRequirements.cs`

```csharp
[DataDefinition, Serializable, NetSerializable]
public partial record struct InteractionRequirements()
{
    [DataField] public MobCapability Required = MobCapability.Interact; // every capability the act needs
    [DataField] public MobCondition Ignoring = MobCondition.None;       // conditions tolerated
}
```

`Required` is a mask. The act is blocked if any capability in it is missing (`Complex`, for
example, §15.4) or blocked by a condition. It replaced a single `Action` and a `NeedComplex`
bool.

It is named to avoid confusion with `ActionRequirementsComponent`/`ActionRequirementsSystem`
(`Content.Shared.Actions`, #46029), which is a different feature (design §6). The prototype
called it `ActionRequirements`; that name MUST NOT be used.

### 2.6 UI types

> **Deferred to a later phase.** The UI tiers are a refactor of their own. A first
> implementation landed on `mob-conditions` (`8b64a50b6b`) and was reverted to keep this phase
> to the bookkeeper. UIs behave as on master until then. This section is the design for that
> phase.

- **`UiStatus.cs`:**
  `public enum UiStatus : sbyte { Close = -1, Disabled = 0, ReadOnly = 1, Interactive = 2 }`.
  It is ordered, so `>=` comparisons are meaningful.

  | Status | Meaning | Open | Input | An already-open window |
  |---|---|---|---|---|
  | `Close` | Policy denial: `AdminOnly`, `BlockSpectators`, `RequiresComplex`. | rejected | rejected | closed by the sweep only if the cause is `Unconscious` (§8.3) |
  | `Disabled` | It would be accessible, but the user's current conditions block it, for example stunned or cuffed. | rejected | rejected | stays open (today's behaviour) |
  | `ReadOnly` | Inspect: a spectator, or a condition the UI forgives (`ReadOnlyConditions`). | allowed | rejected | stays open |
  | `Interactive` | Full use. | allowed | allowed | stays open |

- **`ActivatableUIComponent.cs`** gains:
  ```csharp
  /// Conditions under which this UI opens read-only instead of refusing to open.
  [DataField, AutoNetworkedField]
  public MobCondition ReadOnlyConditions;
  ```
- **`BeforeActivatableUIOpenEvent` and `AfterActivatableUIOpenEvent`** gain
  `public UiStatus Status`. Their handlers MUST NOT apply "use" side effects unless
  `Status == Interactive` (§8.5).
- **`ActivatableUISystem`** gains
  `public bool IsInteractiveViewer(EntityUid uiEntity, Enum key, EntityUid actor)`.
  It is the equivalent for `BoundUIOpenedEvent` handlers, which have no `Status` field: it returns
  `GetUiStatus(actor, uiEntity, key) == Interactive`.

### 2.7 Change to an existing event

> **Superseded by §15.5 step 1.** The bookkeeper checks conditions directly instead of through
> `InteractionAttemptEvent`, so this field is removed again. It is kept here because the demo
> implements it.

`InteractionAttemptEvent` (`Content.Shared/Interaction/Events/InteractionAttemptEvent.cs`) gains
an optional constructor parameter and a field:

```csharp
public struct InteractionAttemptEvent(EntityUid uid, EntityUid? target, MobCondition ignoring = MobCondition.None)
{
    public bool Cancelled;
    public readonly EntityUid Uid = uid;
    public readonly EntityUid? Target = target;
    public readonly MobCondition Ignoring = ignoring; // unmigrated subscribers MAY honour it
}
```

## 3. Normative tables

### 3.1 Cascade — `MobCapabilitiesSystem.Expand(MobCondition) → MobCondition`

The cascade is applied repeatedly until nothing changes, so the order of rules doesn't matter.

| If present | Add |
|---|---|
| `Unconscious` | `Incapacitated \| Immobilized \| Floored \| HandsBlocked` |
| `Restrained` | `HandsBlocked` |
| `Incapacitated` or `HandsBlocked` | `PullBlocked` |

### 3.2 Policy — `MobCapabilitiesSystem.GetBlocked(MobCondition) → MobCapability`

| Condition | Blocks |
|---|---|
| `Unconscious` | `Conscious \| Speak \| Emote \| Point \| StandUp` |
| `Incapacitated` | `Interact \| UseHeld \| Pickup \| Drop \| Attack \| Throw \| ChangeDirection` |
| `HandsBlocked` | `Interact \| UseHeld \| Pickup \| Drop \| Attack` |
| `Incorporeal` | `Interact \| UseHeld \| Pickup \| Drop \| Emote` |
| `Immobilized` | `Move` |
| `Floored` | `Stand` |
| `PullBlocked` | `Pull` |
| `Silenced` | `Speak` |
| `Restrained` | nothing directly |

**Invariants these tables MUST preserve** (each one has a test in §13):
- `StandUp` is blocked only by `Unconscious`. Knockdown raises `StandUpAttemptEvent` itself
  (`SharedStunSystem.Knockdown.cs:346`) to decide recovery, so `Floored` blocking it would
  pin knocked-down mobs forever.
- `Point` is not blocked by `Incapacitated`. A stunned but awake mob can gesture, which is
  today's behaviour.
- No condition blocks `Complex`. It is a capability (§15.4).
- `Silenced` doesn't block `Emote`. Mimes emote; muting only muffles vocal emotes, which is
  not a gate (§16.5).
- For the slice-1 producers, today's semantics are preserved, with the one delta below. Each
  mapping was checked against the producer's current event set:
  - Stun maps to `Incapacitated | Immobilized`. Today it cancels ChangeDirection, Move,
    Interaction, Use, Throw, Drop, Attack and Pickup (`SharedStunSystem.cs:51–58`); the mapping
    blocks the same set.
  - Crit and dead map to `Unconscious`. MobState's 13 cancels (`MobStateSystem.Subscribers.cs:33–43`,
    `:63`, `:166`) are the same set.
  - Asleep maps to `Unconscious`. Sleeping's own five cancels, combined with the Stunned and
    KnockedDown components it ensures (`SleepingSystem.cs:123–124`), are the same set.
  - Cuffs map to `Restrained`. Today they cancel Drop, Pickup, Attack, Use and Interaction
    (`SharedCuffableSystem.cs:83–87`). The cascade adds `Pull`, which `CanPull` already denies through
    `CanInteract` (§2.1).
  - Ghost maps to `Incorporeal`. Today it cancels Use, Emote, Drop, Pickup and Interaction
    (`SharedGhostSystem.cs:32–36`); the mapping blocks the same set.
- **Delta, deliberate:** `Unconscious` blocks `StandUp` for crit and dead mobs. MobState doesn't
  cancel `StandUpAttemptEvent` today; it blocks `StandAttemptEvent` instead.
  - Today a mob knocked down while crit loses `KnockedDownComponent` and then fails to stand.
  - Now it keeps the component until it is revived, and then recovers through the normal
    knockdown path.
  - Covered by `ReviveAfterCritKnockdownStandsUp` (§13).

## 4. `MobCapabilitiesSystem` — `MobCapabilitiesSystem.cs`

`public sealed partial class MobCapabilitiesSystem : EntitySystem`. Its dependencies are
`IGameTiming` and `ActionBlockerSystem`, and it caches an `EntityQuery<MobConditionsComponent>`.

### 4.1 Public API

```csharp
public void Refresh(Entity<MobConditionsComponent?> ent);
public bool HasCondition(Entity<MobConditionsComponent?> ent, MobCondition condition);          // any bit of Conditions
public bool IsBlocked(Entity<MobConditionsComponent?> ent, MobCapability action);                   // hot path
public bool IsBlocked(Entity<MobConditionsComponent?> ent, MobCapability action, MobCondition ignoring);
public MobCondition GetBlockingConditions(Entity<MobConditionsComponent?> ent, MobCapability action); // for popups (§10.2)

public static MobCondition Expand(MobCondition conditions);                                     // §3.1
public static MobCapability GetBlocked(MobCondition conditions);                                    // §3.2
public static MobCondition Effective(MobCondition declared, MobCondition ignoring);              // §4.3
```

- An entity without the component is never blocked by this system: `IsBlocked` returns false
  and `HasCondition` returns false.
- `Expand`, `GetBlocked` and `Effective` are pure and static, so they can be unit-tested
  without a server.

### 4.2 `Refresh` contract

Steps, in order:

1. Resolve the component without logging. If it is absent, or the entity is
   `TerminatingOrDeleted`, return.
2. If `IGameTiming.ApplyingState`, return. `Declared` arrives through the network (§2.3).
3. `declared = GetDeclared(uid)`. `GetDeclared` raises `RefreshMobConditionsEvent` by ref on the
   mob.
4. If `declared == comp.Declared`, return.
5. **Commit:**
   - `old = comp.Conditions`, `oldBlocked = comp.Blocked`.
   - `comp.Declared = declared`, `comp.Conditions = Expand(declared)`,
     `comp.Blocked = GetBlocked(comp.Conditions)`.
   - `Dirty`.
6. If `(oldBlocked ^ comp.Blocked) & Move` is non-zero, call `ActionBlockerSystem.UpdateCanMove(uid)`.
   `InputMoverComponent.CanMove` is a separate cache.
7. If `old != comp.Conditions`, raise `MobConditionsChangedEvent(old, comp.Conditions)` by ref on
   the mob.

Step 4 compares `Declared`, not `Conditions`. Two declared sets can expand to the same
`Conditions` but forgive differently: `Restrained` alone versus `Restrained | HandsBlocked`. So a
change in `Declared` MUST still be committed.

**Invariant R1:** step 5 MUST happen before steps 6 and 7.
- `VehicleSystem.OnOperatorUpdateCanMove` (`Vehicle/Systems/VehicleSystem.cs:82`) calls
  `CanOperate` (`:315`), which calls `CanInteract`. That happens inside step 6, so it must
  read the new cache.
- `MobConditionsChangedEvent` subscribers likewise query `GetUiStatus` against the new
  state.

**Invariant R2:** `Refresh` MUST NOT be re-entered for the same entity.
- A `RefreshMobConditionsEvent` handler MUST NOT call `Refresh` or any `CanX`; it only calls
  `Add`.
- Guard with a debug assert on a per-system `HashSet<EntityUid>` of entities currently
  refreshing.
  - A single "refreshing" field is not enough: a legitimate refresh of entity A can trigger a
    refresh of entity B, for example through step 6 and a vehicle's `UpdateCanMoveEvent` handler.
  - Re-entry for B MUST be allowed; re-entry for A MUST assert.

### 4.3 Ignore masks

```csharp
public static MobCondition Effective(MobCondition declared, MobCondition ignoring)
    => Expand(declared & ~ignoring) & ~ignoring;
```

`IsBlocked(ent, action, ignoring)` returns `(GetBlocked(Effective(comp.Declared, ignoring)) & action) != 0`.
When `ignoring == None` it is the hot path.

It reads only the stored `Declared`. It never raises `RefreshMobConditionsEvent`. That has two
consequences:
- It costs a few bit operations.
- The client gets the same answer as the server, because `Declared` is the networked field.

**Semantics:**
- **The mask is applied before the cascade**, so forgiving a declared condition also forgives
  what it alone implies. Ignoring `Restrained` removes the `HandsBlocked` that restraint derives.
- **The mask is applied again after the cascade**, so forgiving a derived condition works too.
  - Ignoring `HandsBlocked` forgives every hands-only cause: cuffs, and any producer that
    declares `HandsBlocked` directly.
  - Without the second mask, `Restrained` would silently re-derive `HandsBlocked` and the ignore
    would do nothing.
- **Forgiving a condition never forgives an independent cause of the same action.**
  - An unconscious, cuffed mob ignoring `Restrained` is still blocked from `Interact`:
    `Unconscious` derives `HandsBlocked` and `Incapacitated` on its own.
  - The same mob ignoring `HandsBlocked` is still blocked by `Incapacitated`.
- **`Unconscious` cannot usefully be ignored** through `ActionBlockerSystem.CanInteract` (§5.2).
  Its legacy step raises `ConsciousAttemptEvent`, which carries no mask.

### 4.4 Component presence

- **Producers never add the component.** `Refresh` adds `MobConditionsComponent` itself the first
  time a producer declares something on an entity that lacks it. The component's startup then
  refreshes again and commits.
  - Not while `IGameTiming.ApplyingState`: the component arrives with the server state.
  - Not on a `TerminatingOrDeleted` entity.
- **Producers don't subscribe to lifecycle events just for the bookkeeper.** The engine allows
  one subscription per component and event pair, so a new `MapInit` or `ComponentRemove`
  subscription on a widely used component such as `MobStateComponent` or `CuffableComponent`
  can crash a fork that already has one.
  - A producer refreshes from code paths it already owns: its own component's startup and
    shutdown, and its own state changes.
  - Cuffs refresh in `UpdateCuffState`. `CuffableComponent` is never removed at runtime
    upstream, so there is no removal hook. Removing it with VV or `rmcomp` leaves `Restrained`
    cached until the next refresh. The §4.5 check catches that in tests.
- `MobConditionsComponent`'s own `ComponentStartup` calls `Refresh`, which covers mobs spawned
  already dead or cuffed.
- `- type: MobConditions` is on `MobDamageable` in `Resources/Prototypes/Entities/Mobs/base.yml`,
  next to `MobState`, so ordinary mobs have it from spawn. Anything else gets it lazily.

### 4.5 Cache consistency (DEBUG and tests)

The cache replaces checks that used to run live, so a missed `Refresh` silently leaves a mob
blocked or unblocked. To catch this:

- When the cvar `debug.mob_conditions_verify` is on, `IsBlocked` re-raises
  `RefreshMobConditionsEvent` and `DebugTools.Assert`s that the result equals `comp.Declared`.
- The check is skipped while `ApplyingState`, and on the client.
- The integration-test pool enables it, so every existing suite doubles as a staleness test.

## 5. Attempt-event handling

### 5.1 Central subscriptions

> **Migration only.** These subscriptions exist while the typed attempt events do. §15.5 step 1
> drops the `InteractionAttemptEvent` and `ConsciousAttemptEvent` ones, and each of the others
> goes when its event is deleted. The cancel-only rule below carries over to the two
> end-state events (§15.3).

`MobCapabilitiesSystem` subscribes on `MobConditionsComponent` to every event in §2.2. Each
handler cancels when `(comp.Blocked & action) != 0`.

- New code SHOULD use `[SubscribeLocalEvent]` attributes.
- An event MUST NOT be subscribed both ways for the same component.

Exceptions:
- `InteractionAttemptEvent`: uses `IsBlocked(ent, Interact, args.Ignoring)`, so the requested
  mask is honoured.
- `SpeakAttemptEvent`: see §5.4.

**Cancel-only rule.** Every subscriber of these events MUST only ever set `Cancelled` to true.
Only under that rule can an ignore mask be honoured regardless of handler order.

One existing handler breaks the rule. `SharedStationAiSystem.OnHeldInteraction`
(`Silicons/StationAi/SharedStationAiSystem.Held.cs:151`) *assigns*
`args.Cancelled = <whitelist check>`, so it can un-cancel an earlier subscriber.
- The Core PR MUST change it to only set `true`.
- Test `StationAiDoesNotUncancel` (§13) covers it.

### 5.2 `ActionBlockerSystem` overload — `ActionBlockerSystem.Conditions.cs`

> **Superseded by §15.2.** In the end state this overload is the bookkeeper's `CanPerform`, and
> `ActionBlockerSystem` only forwards to it (§15.5).

```csharp
public bool CanInteract(EntityUid user, EntityUid? target, in InteractionRequirements req)
```

**Steps:**
1. If `IsBlocked(user, req.Required, req.Ignoring)`, return false. This is the fast path when
   `Ignoring == None`.
2. If `req.Required` includes a capability the creature lacks (§15.4), return false.
3. Raise the **legacy event for each required capability**, so that unmigrated sources still get a say:

   | In `req.Required` | Legacy check |
   |---|---|
   | `Interact` | the existing `CanInteract(user, target)` body: `ConsciousAttemptEvent`, then `InteractionAttemptEvent(user, target, req.Ignoring)`, then `GettingInteractedWithAttemptEvent` on the target |
   | `Conscious` | `ConsciousAttemptEvent` |
   | `None` | nothing |
   | any other | MUST NOT be used with this overload; use the specific `CanX` |

The existing `CanInteract(user, target)` (`ActionBlockerSystem.cs:73`) is unchanged, and is
equivalent to `CanInteract(user, target, default)`.

Legacy subscribers of `InteractionAttemptEvent` that haven't migrated ignore `req.Ignoring`. So
any cause they still block stays unforgivable until it migrates. That is correct during
migration: an unmigrated cause has not been classified yet.

### 5.3 `Stand` vs `StandUp`

- `StandAttemptEvent` is cancelled by `Stand`, which comes from `Floored`.
- `StandUpAttemptEvent` is cancelled by `StandUp`, which comes from `Unconscious` only. For
  crit and dead mobs this is new (§3.2 delta).
- Knockdown's own `StandUpAttemptEvent` raise (`SharedStunSystem.Knockdown.cs:346`) and the
  `KnockdownStatusEffect` relay handler (`SharedStunSystem.cs:69/371`) are untouched in slice 1
  (see §11 Q2).

### 5.4 Speak: force-say, last words, muting and popups

Three things used to special-case speech. Each is now an ordinary condition or ignore mask.

**Muting is `Silenced`.** The muted status effect declares `Silenced` while it is applied
(§6.5), and `Silenced` blocks `Speak` (§3.2). Its speak-attempt handler is gone: it cancelled
and popped up from inside a query, so every speech check showed the popup.

**Force-say forgives `Unconscious` for one message.** When a mob goes from alive to crit or dead,
`DamageForceSaySystem` asks the client to send whatever was typed, and grants
`AllowNextCritSpeechComponent` for three times the ping. The line arrives as an ordinary chat
message, so the server can't tell it apart.
- Chat raises `CheckIgnoreSpeechBlockerEvent` once per message, as it is sent. The event now
  carries an `Ignoring` mask next to its `IgnoreBlocker` bool.
- `DamageForceSaySystem` answers on `AllowNextCritSpeechComponent` with `Unconscious`, and spends
  the grant there. A query never spends it, and the bookkeeper knows nothing about it.
- Sleeping declares `Unconscious` too (§6.2), so the force-say on falling asleep goes through
  the same mask. Its own copy of the token check is gone.

This was misnamed "last words" before. It is not the Last Words action.

**Last words pass a mask.** `CritMobActionsSystem` sends the line with
`ignoring: Unconscious | Silenced`, through `TrySendInGameICMessage`, which now takes a mask.
- It forgives being crit and, deliberately, being muted.
- Everything else still applies: a speechless creature, an admin mute (§16.3), and unmigrated
  attempt events. Being asleep is `Unconscious`, so a sleeping crit mob can still say them.
- It used to skip every check with `ignoreActionBlocker: true`.

**Popups are shown by chat, at send time.** When a message is blocked, chat pops the reason from
`CanPerform(..., out reason)` (§10.2), unless the mob is still unconscious after the mask. An
unconscious mind gets no feedback.
- A crit or dead mime who types gets nothing. That is what #45977's relay ordering achieved; the
  ordering is gone.
- A dying mime whose typed line is force-said gets the vow text: `Unconscious` is forgiven,
  `Silenced` is not, and the effect words the reason (§2.4).
- Other speech checks, like the typing indicator, magic and the laws UI, pop nothing up.

## 6. Producers

### 6.1 Rules for every producer

1. Subscribe to `RefreshMobConditionsEvent` on the producer's component, and `Add` the
   condition while it applies. Do nothing else in the handler (R2).
2. Guard with `ent.Comp.LifeStage <= ComponentLifeStage.Running`. The component is still
   subscribed during removal, and the removal refresh must not re-declare.
3. Call `Refresh` when the source starts applying, when it is removed, and whenever the
   producer's own state flips (for example `CanStillInteract`, `CanGhostInteract`, a mob-state
   change). Never add the component yourself (§4.4).
4. **Refresh first.** In the code path that changes the producer's state, `Refresh` MUST run
   before any further event is raised or any `CanX` is called.
   - Before this design, every check ran live. Now it reads a cache, so any handler that runs
     between the state change and the refresh sees stale blocking.
   - The per-producer anchors are in the §6.2 "Refresh on" column.
   - §4.5 asserts it in tests.
5. **No unrefreshed writes.** A field the handler reads (`CanStillInteract`, `CanGhostInteract`)
   MUST be written only through a setter that refreshes.
   - Restrict the field with `[Access]`.
   - VV edits that bypass the setter are caught by §4.5 in tests, but not in production.
6. **Shared and networked only.** The producer's system MUST live in `Content.Shared`, and every
   field its handler reads MUST be networked.
   - A predicted `Refresh` on the client polls only client-visible sources. A server-only source
     would be dropped from the client's `Declared` until the next server state arrives.
   - Known sources that fail this, and so MUST stay attempt events: `BrainSystem` (its
     `BrainComponent` is not networked), and the server-only `DnaImprintEntityEffectSystem`.
7. Delete every attempt-event handler the condition now covers.
   - A producer MUST NOT both declare a condition and cancel the same action's event. That
     makes the condition unforgivable.
   - Conditional handlers that aren't plain blocks stay (design §4).
8. One producer per commit.

### 6.2 Slice 1: the original seven

| Producer | Condition (when) | Delete (attempt handlers) | Refresh on | Keep |
|---|---|---|---|---|
| **MobState**: `Mobs/Systems/MobStateSystem.Subscribers.cs` | `Unconscious` (Critical, Dead) | `CheckAct` subscriptions `:33–43` (11 events); `CheckConcious` `:63` (Conscious); `OnSpeakAttempt` `:166` (§5.4) | state entered (`:103` — replace `_blocker.UpdateCanMove` with `_conditions.Refresh`; `_blocker` then leaves `MobStateSystem.cs`) | `Down()` `:55`, equip/unequip/unbuckle self-checks, `OnIncapCuffCheck` `:226` (until §11 Q3) |
| **Stun**: `Stunnable/SharedStunSystem.cs` | `Incapacitated \| Immobilized` (`StunnedComponent` present) | `:51–58` (8 events): `OnAttempt` `:391`, `OnMoveAttempt` `:383`, `OnAttemptInteract` `:76` | `StunnedComponent` startup/shutdown; the existing `UpdateCanMove` calls become `Refresh` | equip/unequip `:59–60`, the KnockdownStatusEffect relay `:69` |
| **Knockdown**: `Stunnable/SharedStunSystem.Knockdown.cs` | `Floored` (`KnockedDownComponent`) | `OnStandAttempt` `:575` (sub `:54`) | knockdown init; shutdown *before* standing | its own `StandUpAttemptEvent` raise `:346` |
| **Sleeping** (**done**): `Bed/Sleep/SleepingSystem.cs` | `Unconscious` (`SleepingComponent`) | `:66` Speak, `:68` Point, `:70` Conscious, `:75` StandUp, `:80` Emote (handlers `:166/183/193/203/368`) | **`ComponentInit` and `ComponentRemove`**, which are the hooks this system already uses (`OnCompInit` `:148`, `OnComponentRemoved` `:156`), not startup/shutdown. In `OnComponentRemoved`, `Refresh` MUST run before `SleepStateChangedEvent` is raised (`:160`), because that event's handler calls `TryUnstun` and `TryStanding` (`:142–143`). | `OnSeeAttempt`, `OnUnbuckleAttempt`, `StunEndAttemptEvent`, `OnIncapCuffCheck` `:210` |
| **Cuffs**: `Cuffs/SharedCuffableSystem.cs` | `Restrained` (`!CanStillInteract`) | `:83–86` `CheckAct`, `:87` `CheckInteract` `:95` | `UpdateCuffState`: replace the `_actionBlocker.UpdateCanMove` at `:183`. It already runs before `CuffedStateChangeEvent`. Make `CanStillInteract` writable only from here (§6.1.5). | `HandleMoveAttempt` `:73/238` (only while pulled), `HandleStopPull` `:246`, the `CheckAct` method (still used by equip handlers); self-uncuff → §9.1 |
| **Ghost**: `Ghost/Systems/SharedGhostSystem.cs` (namespace `Content.Shared.Ghost.Systems`) | `Incorporeal` (`!CanGhostInteract`) | `:32–36`: `OnAttempt` `:56`, `OnAttemptInteract` `:50` | `SetCanGhostInteract` (`:114`); server `GhostSystem.OnGhostStartup` calls a protected `EnsureGhostConditions` | everything else. MUST NOT add a `GhostComponent, ComponentStartup` subscription in shared: client and server already subscribe it, and the duplicate crashes at init. `CanGhostInteract` is a YAML `canInteract` field (`admin_ghost.yml:25`), so restrict runtime writes to the setter. |
| **LegsParalyzed**: `Traits/Assorted/LegsParalyzedSystem.cs` | `Immobilized` | `:21/45` | startup, shutdown | buckle and throw handlers |

Sleeping also does `EnsureComp<StunnedComponent>` and `EnsureComp<KnockedDownComponent>`
(`SleepingSystem.cs:123–124`), so a sleeper is `Unconscious | Incapacitated | Immobilized |
Floored` from both paths. Both paths agree, which is fine.

### 6.3 Later slices: pure-cancel sources found since July

> §16.1 lists these again, together with every other producer, using the end-state vocabulary.
> The deltas below still apply.

No closed condition maps exactly onto most of these sources. The "Delta" column lists what the
proposed condition would block *in addition to* today's events. Each row MUST either ship the
delta as a stated gameplay change, or keep a residual event and pick a narrower condition.

| Source | File | Events cancelled today | Proposed condition | Delta |
|---|---|---|---|---|
| AdminFrozen | `Administration/AdminFrozenSystem.cs:23–34` | Use, Pickup, Throw, Interaction, Attack, ChangeDirection, UpdateCanMove; Speak/Emote if `Muted` | `Incapacitated \| Immobilized`; Speak/Emote stay events (Muted is an admin option, not a faculty) | +`Drop` (+`Pull` via cascade, already denied by `CanInteract`) |
| VentriloquistPuppet | `Puppet/SharedVentriloquistPuppetSystem.cs:18–24` | Use, Interaction, Drop, Pickup, UpdateCanMove, Emote, ChangeDirection | `Incapacitated \| Immobilized`, with Emote kept as an event | +`Attack`, +`Throw` |
| KitchenSpike | `Kitchen/SharedKitchenSpikeSystem.cs:75–80` | ChangeDirection, Use, Throw, Drop, Attack, Pickup (**not** Interaction) | `Incapacitated` | +`Interact`, +`Pull` |
| BeingDisposed | `Disposal/Unit/SharedBeingDisposedSystem.cs:27–28` | Interaction, Attack | `HandsBlocked` | +`UseHeld`, +`Pickup`, +`Drop` |
| Brain / BorgBrain | `Body/Systems/BrainSystem.cs:20`, `Silicons/Borgs/SharedBorgSystem.cs:101` | Point | not a condition: no body to point with. The `Brain` capability profile lacks `Point` (§15.4), which needs no networking. | — |
| Worm | `Movement/Systems/WormSystem.cs:19` | StandUp (+ message, autostand) | leave (sets extra fields) | — |
| Chasm, TetherGun | `Chasm/ChasmSystem.cs:107`, `Weapons/Misc/SharedTetherGunSystem.cs:51` | UpdateCanMove | `Immobilized` | none |

Each row SHOULD be its own commit, with that system's existing tests. Rows with a delta go in
the PR description as gameplay changes (§11 Q7).

### 6.4 Everything else

Nothing is permanently out of scope. Every remaining attempt-event subscriber is classified
in §16, and each one leaves its event when its bucket's mechanism (§15.2–15.4) lands.

### 6.5 Status-effect producers

A status effect lives on its own entity, so it declares through the relayed event (Q1):
- Subscribe `StatusEffectRelayedEvent<RefreshMobConditionsEvent>` on the effect's component, and
  add the condition while the effect's `StatusEffectComponent.Applied` is set. A delayed effect
  declares once it starts.
- `Args` is a property of the relay wrapper. Add to a copy and assign it back, or the add is
  lost.
- Refresh `args.Target` on `StatusEffectAppliedEvent` and `StatusEffectRemovedEvent`. The removed
  event is raised after the effect has left the mob's container, so that refresh no longer sees
  it.
- `StatusEffectsSystem` sets `Applied` before it raises `StatusEffectAppliedEvent`, so the refresh
  in that event sees the effect as applied.
- `Applied` isn't networked: the client computes it from the networked start time. The rule in
  §6.1.6 holds.

The muted status effect (`Speech/Muting/MutedStatusEffectSystem.cs`) is the first.

## 7. Action bar

### 7.1 `ActionComponent.Requires`

`Content.Shared/Actions/Components/ActionComponent.cs` gains:

```csharp
/// What the performer must be capable of. Replaces CheckCanInteract/CheckConsciousness.
[DataField, AutoNetworkedField]
public InteractionRequirements Requires = new() { Action = MobCapability.Interact };
```

The call sites switch to the §5.2 overload:

- `SharedActionsSystem.OnValidate` (`:346`; the check is at `:348–349`):
  `if (!_actionBlocker.CanInteract(args.User, null, ent.Comp.Requires)) args.Invalid = true;`
- `SharedActionsSystem.ValidateEntityTarget` (`:442`; the check is at `:454`): same, with the
  target.

  Today, this site checks *only* `CheckCanInteract`. Under parity, an action with
  `action: Conscious` does no target-interaction check, which matches today's
  `checkCanInteract: false`.

### 7.2 Parity mapping

| Today | `requires` |
|---|---|
| `checkCanInteract: true`, `checkConsciousness: true` (default) | `{ action: Interact }` |
| `checkCanInteract: false`, `checkConsciousness: true` | `{ action: Conscious }` |
| `checkCanInteract: false`, `checkConsciousness: false` | `{ action: None }` |
| `checkCanInteract: true`, `checkConsciousness: false` | `{ action: Interact }`. `CanInteract` already implies consciousness (`ActionBlockerSystem.cs:75`), so this combination never behaved differently. |

The 11 prototypes that set the fields. Parity is decided by the *effective* values after
inheritance, not only the fields a prototype writes:

| Prototype | File | Today (effective) | Parity |
|---|---|---|---|
| `BaseMentalAction` (and every child: crit, mapping, observer toggles, laws, pAI, `ActionSleep`/`ActionWake`, suicide implants and others) | `Actions/types.yml:39` | both false | `None` |
| `ActionScream` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionActivateFreedomImplant` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionActivateEmpImplant` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionActivateScramImplant` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionToggleWagging` (and child `ActionToggleWaggingVulpkanin`) | `Actions/types.yml` | interact false | `Conscious` |
| `ActionChangelingBiodegrade` | `Actions/changeling.yml` | interact false | `Conscious` |
| `ActionChangelingStasis` (and child `ActionChangelingStasisWeak`) | `Actions/changeling.yml` | both false | `None` |
| `ActionChangelingLastResort` | `Actions/changeling.yml` | both false | `None` |
| `ActionStethoscope` | `Entities/Clothing/Neck/misc.yml` | interact false | `Conscious` |
| `ActionGhostBoo` | `Entities/Mobs/Player/observer.yml:127` | both false: it sets `checkCanInteract: false` and inherits `checkConsciousness: false` from `BaseMentalAction` | `None` |

Re-grep `checkCanInteract:|checkConsciousness:` before migrating, since new prototypes may have
landed. Resolve each hit against its parents.

The world-target path (`SharedActionsSystem.ValidateWorldTarget` `:478`) checks neither field
today, and `Requires` doesn't change that.

### 7.3 Fork compatibility

`CheckCanInteract` and `CheckConsciousness` stay for one release as `[Obsolete]` data fields.
On `ComponentInit`, if either was set to `false` in YAML, translate it into `Requires` using
§7.2 and log a one-time warning naming the prototype. Remove them in the following release.

### 7.4 Precision follow-ups (balance PRs, not parity)

- **Biodegrade:** `{ action: Interact, ignoring: Restrained }`. It works cuffed but not while
  stunned. Today it works while stunned.
- **Freedom implant:** the same.
- **Scream, Wagging:** keep `Conscious`.

## 8. UI tiers

> **Deferred to a later phase.** It adds back the `UiAccess` capability and the
> `UiBlocked` condition, which `Incapacitated` and `HandsBlocked` imply and which blocks
> `UiAccess`. The UI tiers are a refactor of their own. A first
> implementation landed on `mob-conditions` (`8b64a50b6b`) and was reverted to keep this phase
> to the bookkeeper. UIs behave as on master until then. This section is the design for that
> phase.

### 8.1 `ActivatableUISystem.GetUiStatus` — `ActivatableUISystem.Status.cs`

```csharp
public UiStatus GetUiStatus(EntityUid user, Entity<ActivatableUIComponent?> target, Enum key)
```

**The key is required.**
- Today the ActivatableUI policy (`RequiresComplex`, `SingleUser`) applies only to the BUI key
  that matches `ActivatableUIComponent.Key` (`SharedInteractionSystem.cs:184`). Other BUIs on the
  same entity, such as a PDA's secondary keys, are gated only by `CanInteract`.
- Let `comp` be the `ActivatableUIComponent` if `comp.Key == key`, and null otherwise.
- **Exception:** `BlockSpectators` is read from the entity's component whatever the key. That
  preserves today's behaviour, where it is checked before the key match at `:159`.

The resolution order (first match wins):

1. **A spectator: the user has `GhostComponent` and `!CanGhostInteract`.**
   - If `BlockSpectators`, the result is `Close`.
   - Otherwise it is `ReadOnly`.

   Admin ghosts (`canInteract: true` with hands and complex interaction, `admin_ghost.yml:25`)
   fall through to the normal rules. The rule keys on `CanGhostInteract` rather than admin
   status for two reasons:
   - An admin observing as an ordinary ghost would otherwise gain `Interactive` everywhere.
   - `ISharedAdminManager.IsAdmin` mispredicts for anyone but the local player
     (`ClientAdminManager.cs:128–142`).
2. **`comp?.AdminOnly == true` and the user is not an admin**: `Close`.
3. **`comp?.RequiresComplex == true` and `!CanComplexInteract(user)`**: `Close`. This is a
   creature property, not a condition.
4. **`req = { Required = UiAccess }`.** If `CanInteract(user, target, req)` passes, the result is
   `Interactive`.
5. **If `comp?.ReadOnlyConditions` is not `None`**, set `req.Ignoring` to it. If
   `CanInteract(user, target, req)` passes, the result is `ReadOnly`.
6. **Otherwise**: `Disabled`. The user would have access, but their conditions block it now.

`GetUiStatus` lives in shared code and reads only networked state. So the client can call it
for the local player to render an inspect mode (§8.6).

### 8.2 Enforcement edits

**`Content.Shared/Interaction/SharedInteractionSystem.cs`, `OnBoundInterfaceInteractAttempt` (`:150`):**
- Add an `ActivatableUISystem _activatableUI` dependency.
- At the top: `if (ev.Message is CloseBoundInterfaceMessage) return;` Closing must always
  succeed.
- Replace `:154–164` (the `CanInteract` / ghost special case) with:
  - `status = _activatableUI.GetUiStatus(ev.Actor, (ev.Target, aUiComp), ev.UiKey)`.
  - `Open` requires `status >= ReadOnly`.
  - Every other message requires `status == Interactive`.
  - Otherwise `ev.Cancel()`.

  This applies to every BUI, not only ActivatableUI ones. The engine raises the attempt for
  every non-close message on a BUI with `RequireInputValidation` (the default). That includes
  server-side `OpenUi` calls, which arrive as an `OpenBoundInterfaceMessage`.
- **Keep** the range cancel (`:168`, #46045), the accessibility check (`:174`) and the key match
  (`:184`).
- The single-user check (`:187`) applies only when `status == Interactive`. A `ReadOnly` viewer
  neither holds nor respects the lock.
- Delete the `RequiresComplex` check (`:193`). `GetUiStatus` step 3 now covers it.
- **Side benefit:** a raw client `Open` currently skips `AdminOnly`, because only `InteractUI`
  checks it. The gate now enforces it through step 2. Other raw-open gaps remain (§8.6).

**`InteractionActivate` (`:1148`; the check is at `:1165`):**
- When `CanInteract(user, used)` fails and `used` has an `ActivatableUIComponent` with
  non-`None` `ReadOnlyConditions`, call `ActivatableUISystem.TryOpenReadOnly(user, used)` and
  return its result. That is a new public wrapper around `InteractUI` that succeeds only at
  `ReadOnly`.
- It MUST NOT fall through to raising `ActivateInWorldEvent`. That event goes to every handler
  on the entity, and they would run for a user whose blockers just failed.
- Reachability: left-click is rejected earlier, in `UserInteraction` (`:430`). Read-only
  activation is reached through the activate key (`HandleActivateItemInWorld` `:1126`) and the
  verb (`ShouldAddVerb` below). Don't extend the carve-out to `UserInteraction`, because
  left-click on a world item means pick up.

**`Content.Shared/UserInterface/ActivatableUISystem.cs`:**
- `ShouldAddVerb` (`:97`):
  - Compute `status = GetUiStatus(args.User, (uid, component), component.Key)` **first**.
    Today the `RequiresComplex` branch returns early when `args.Hands == null` (`:107`), which
    would hide the verb from every ghost.
  - Return false if `status < ReadOnly`.
  - Do the hand and `InHandsOnly` checks only when the status is `Interactive`.
  - Keep `RaiseCanOpenEventChecks(..., silent: true)`.
- `InteractUI` (`:194`):
  - Replace `:205` (CanInteract/ghost) and `:208–212` (`RequiresComplex`) with
    `var status = GetUiStatus(user, (uiEntity, aui), aui.Key); if (status < UiStatus.ReadOnly) return false;`.
  - The `InHandsOnly` checks (`:214–224`), the single-user popup (`:229–238`) and
    `SetCurrentSingleUser` (`:250`) apply only when `status == Interactive`.
  - Pass `status` into `BeforeActivatableUIOpenEvent` (`:247`) and
    `AfterActivatableUIOpenEvent` (`:254`).
- `OnBoundInterfaceOpenAttempt` (`:184`, #46045): **unchanged**.

Every enforcement hook only cancels, so no ordering between `SharedInteractionSystem` and
`ActivatableUISystem` is required.

### 8.3 `ActivatableUIStatusSystem` — `ActivatableUIStatusSystem.cs`

Today nothing closes a user's UIs on stun, cuffs or crit. The window stays open, and the
gate rejects its input. The engine's range check (`SharedUserInterfaceSystem.CheckRange`) checks
only map and range. This system adds closing for **unconsciousness only**.

`public sealed partial class`. It subscribes `MobConditionsComponent, MobConditionsChangedEvent`.
When `(args.Added & Unconscious) != 0`, it walks `_uiSystem.GetActorUis(actor)` (collected into
a `ValueList` first). For each `(entity, key)` it:
- skips BUIs whose interface data has `RequireInputValidation == false`. These are borg and AI
  laws, the pAI store and vending machines; they opted out of the gate and must not be swept
  either;
- calls `CloseUi(entity, key, actor)` if `GetUiStatus(actor, entity, key) < ReadOnly`.

What it deliberately does *not* do:
- **No sweep for other conditions.** A stunned or cuffed user's window stays open at `Disabled`,
  with input rejected. That matches today, and a 2-second stun doesn't throw away a console
  window.
- **No `MobStateChangedEvent` backstop.** MobState is itself a producer, so crit already arrives
  as `Unconscious`. On the client the event is raised only through damage thresholds
  (`MobThresholdSystem.OnDamaged`), so it is not a reliable signal.

The precedent for the sweep is `ActivatableUIRequiresVisionSystem.OnBlindnessChanged`
(`Eye/Blinding/Systems/ActivatableUIRequiresVisionSystem.cs:33–52`), which already walks
`GetActorUis` on a condition change.

### 8.4 Data

Set `readOnlyConditions: Restrained` on every `ActivatableUI` block with
`key: enum.PaperUiKey.Key`. Each one is its own block, not inherited from a shared parent:
- `Entities/Objects/Misc/paper.yml:82`: `Paper`
- `Entities/Objects/Misc/paper.yml:475`: the envelope
- `Entities/Objects/Misc/books.yml:23`: `BookBase`
- `Catalog/Fills/Paper/manuals.yml:18`: `HoloparasiteInfo`. It keeps the default
  `requiresComplex: true`, so `ReadOnlyConditions` applies only to complex users.

Re-grep `PaperUiKey` when implementing.

**Paper preconditions for slice 3**, because existing behaviour interacts with read-only:
- `PaperSystem.BeforeUIOpen` (`Paper/PaperSystem.cs:75–79`) resets `Mode = Read` on the entity,
  and `Mode` is one field shared by all viewers. A read-only open MUST NOT reset it. Otherwise a
  ghost or a cuffed reader kicks the current writer out of write mode. Gate it on
  `args.Status == Interactive`.
- `OnInputTextMessage` (`:182–210`) never checks `Mode == Write`. The gate rejects
  `ReadOnly` input, so this doesn't block slice 3, but it is a latent bug for interactive
  viewers. Fix it separately.

### 8.5 Open side effects (required audit)

Ghosts can open every UI that doesn't set `BlockSpectators` (§8.1 step 1). Opening is not
side-effect free.

**Rule:** any handler of `BeforeActivatableUIOpenEvent`, `AfterActivatableUIOpenEvent` or
`BoundUIOpenedEvent` that *does* something beyond pushing UI state MUST gate on
`Status == Interactive` (or `IsInteractiveViewer`), unless the table below says to allow it.
Slice 3 MUST resolve every row; new handlers MUST follow the rule.

| Handler | Effect on open | Required for `ReadOnly` |
|---|---|---|
| `Trigger/Systems/TriggerSystem.Interaction.cs:116` | fires a trigger with the actor as user | gate |
| `Shuttles/Systems/ShuttleConsoleSystem.cs:144` (After) | `TryPilot`: adds `PilotComponent`, blocks movement | gate (spectators watch, they don't pilot) |
| `Shuttles/Systems/ShuttleConsoleSystem.Drone.cs:37` (After) | rebinds the drone console | gate |
| `SurveillanceCamera/Systems/SurveillanceCameraMonitorSystem.cs:190` | `AddViewer` | allow (viewing is inspection) |
| `Arcade/BlockGame/BlockGameArcadeSystem.cs:52` (After) | seats the player | route to the existing `Spectators` list |
| `Arcade/SpaceVillainGame/SpaceVillainArcadeSystem.cs:108` (After) | creates a game | gate |
| `Power/EntitySystems/PowerMonitoringConsoleSystem.cs:110` | resets the focus shared by all viewers | gate |
| `UserInterface/ActivatableUISystem.Power.cs:38` | toggles the item on and drains its cell | gate |
| `Power/EntitySystems/UIPowerStateSystem.cs:39` | sets the working (power draw) state | gate |
| `Atmos/EntitySystems/GasAnalyzerSystem.cs:124` | activates the analyzer | gate |
| `Pinpointer/StationMapSystem.cs:71` | uses cell charge, adds `StationMapUserComponent` | gate the charge; allow the map |
| `Medical/CrewMonitoring/CrewMonitoringConsoleSystem.cs:34` | uses cell charge | gate |
| `Atmos/Monitor/Systems/AirAlarmSystem.cs:245` (After) | registers the interface, device-network sync | allow (needed to show data) |
| `Instruments/InstrumentSystem.cs:268` | `EnsureComp<ActiveInstrumentComponent>` | gate (placeable instruments already block spectators) |
| `Sound/SharedEmitSoundSystem.cs:60` (After) | plays a sound | gate |
| `MagicMirror/MagicMirrorSystem.cs:179` (Before) | sets the mirror's target | gate |
| `Paper/PaperSystem.cs:75` (Before) | resets `Mode` | gate (§8.4) |

Paths are relative to `Content.Server/` or `Content.Shared/`, whichever has the file. The list
came from a sweep of open-event subscribers at `6a9a8abe96`; re-sweep before implementing.

Test `GhostOpenNoSideEffects` (§13) covers the trigger and shuttle-console rows.

### 8.6 Known gaps, not fixed here

- **Client inspect mode is opt-in per window.** A `ReadOnly` viewer sees the normal window,
  and the server rejects its input. A BUI MAY call `GetUiStatus` for the local player and
  disable its controls. Until it does, buttons appear to do nothing.
- **Raw `Open` still skips `InHandsOnly`, `VerbOnly` and `RequiredItems`.** Only `InteractUI`
  checks them. `AdminOnly` is now enforced (§8.2).
- **`InHandsOnly` is skipped for `ReadOnly`** (§8.2), so a ghost can inspect a PDA lying on the
  floor. Whether that is acceptable is §11 Q8.

## 9. Ignore-mask call sites

### 9.1 Self-uncuff (slice 1, required)

`SharedCuffableSystem.OnUncuffAttempt` (`:123–133`) currently sets `CanStillInteract = true`,
calls `CanInteract(user, user)`, then sets it back to `false`, dirtying twice. Replace the whole
self-branch with:

```csharp
if (!_actionBlocker.CanInteract(args.User, args.User,
        new InteractionRequirements { Ignoring = MobCondition.Restrained }))
    args.Cancelled = true;
```

### 9.2 Paper

Paper is the YAML form: §8.4.

### 9.3 Container escape (balance decision, not slice 1)

`Content.Server/Resist/EscapeInventorySystem.cs:37` gates struggling out of a container on
`CanInteract(uid, container.Owner)`. A cuffed mob is `HandsBlocked`, so it currently can't
escape a locker or bag.

Thrashing doesn't need hands, so `Ignoring = Restrained` is the semantically correct choice.
However, it changes containment balance (cuff plus locker), so it goes to maintainers as its
own PR (§11 Q4).

### 9.4 Action-bar precision

See §7.4.

## 10. Integration with EntityConditions and ActionRequirements

### 10.1 `HasMobConditionCondition`

File: `Content.Shared/EntityConditions/Conditions/Body/MobConditionEntityConditionSystem.cs`.

```csharp
public sealed partial class MobConditionEntityConditionSystem
    : EntityConditionSystem<MobConditionsComponent, HasMobConditionCondition>
{
    [Dependency] private MobCapabilitiesSystem _conditions = default!;
    protected override void Condition(Entity<MobConditionsComponent> ent, ref EntityConditionEvent<HasMobConditionCondition> args)
        => args.Result = _conditions.HasCondition((ent, ent.Comp), args.Condition.Condition);
}

public sealed partial class HasMobConditionCondition : EntityConditionBase<HasMobConditionCondition>
{
    [DataField(required: true)] public MobCondition Condition;
    public override string EntityConditionGuidebookText(IPrototypeManager prototype) =>
        Loc.GetString("entity-condition-guidebook-has-mob-condition",
            ("condition", MobCapabilitiesSystem.GetLocalizedName(Condition)));
}
```

- `GetLocalizedName` joins `Loc.GetString($"mob-condition-name-{member.ToString().ToLowerInvariant()}")`
  over the set bits. Passing the enum itself would print the raw member names, such as
  `Restrained, Floored`.
- Add `entity-condition-guidebook-has-mob-condition` to
  `Resources/Locale/en-US/guidebook/entity-effects/conditions.ftl`, next to the existing
  `entity-condition-guidebook-mob-state-condition` (`:46`).
- Add one `mob-condition-name-<member>` key per §2.1 member in a new
  `Resources/Locale/en-US/mob-conditions/mob-conditions.ftl`.
- `HasCondition` tests the expanded `Conditions`, so "is restrained" is true only for a declared
  `Restrained`, but "hands blocked" is true for any cause.
- It is usable from `ActionRequirementsComponent.Conditions`, entity effects and objectives.
- It is additive: `MobStateCondition` stays.

### 10.2 Failure reasons (follow-up)

`GetBlockingConditions(ent, required)` returns every *declared* condition that would block one
of the required capabilities on its own:

```csharp
foreach declared bit b:  if ((GetBlocked(Expand(b)) & required) != 0) result |= b;
```

It iterates only the declarable conditions: pure bit math.

It is not "the conditions whose removal would unblock the action". When there are several
causes, removing any single one unblocks nothing: a cuffed sleeper stays blocked when either
cause is removed. That definition would return an empty set exactly when an explanation is most
needed.

For a popup, pick one bit by fixed priority: `Unconscious`, `Incorporeal`, `Incapacitated`,
`Restrained`, `HandsBlocked`, `Immobilized`, `Floored`, `Silenced`. Then raise
`GetMobConditionReasonEvent` (§2.4), so whatever declared the bit can word it. Otherwise use
the key `mob-condition-blocked-<member>`.

`SharedActionsSystem` MAY put a localized "You can't do that while {condition}" into
`ActionAttemptEvent.Reason`. That addresses the TODO on `ActionRequirementsComponent.FailPopup`
for the condition-based case. Locale keys: `mob-condition-blocked-<member>`.

## 11. Open questions

| # | Question | Recommendation |
|---|---|---|
| Q1 | Should `RefreshMobConditionsEvent` be relayed through status effects, so a status effect entity can declare a condition directly? | **Yes. Done** for muting (§6.5). `RefreshMobConditionsEvent` and `GetMobConditionReasonEvent` are relayed with `RefRelayStatusEffectEvent`, and the effect refreshes on apply and remove. The knockdown status effect's `Pinned` (Q2) will use the same relay. |
| Q2 | How does the `KnockdownStatusEffect` `StandUp` pin (`SharedStunSystem.cs:69/371`) map to conditions? | **A new declarable `Pinned`, which blocks `StandUp` (§15.6).** `Floored` must keep allowing `StandUp`, or recovery breaks (§5.3). The status effect declares `Pinned` through the Q1 relay, and Worm declares it too (§16.1). |
| Q3 | Should `CheckIncapacitatedCuffEvent` (`SharedCuffableSystem.cs:525/902`) be replaced with `HasCondition(target, Unconscious)`? | The Sleeping and MobState answers map to `Unconscious`. The Stamina-crit answer (`SharedStaminaSystem.cs:147`) has no condition: stamcrit applies stun, but #45296 deliberately excluded plain stun. Either add stamcrit as an explicit source or leave the event. Ask maintainers. |
| Q4 | Can cuffed mobs struggle out of containers (§9.3)? | Balance call. Raise it with maintainers. |
| Q5 | Do the action precision tweaks (§7.4) ship with the parity PR? | No. Parity first, then a separate balance PR. |
| Q6 | Should hand-drop and stop-pull side effects move to `HandsBlocked`/`PullBlocked` (design §8)? | Later, as a separate gameplay PR. It interacts with virtual items from cuffs and vehicles. |
| Q7 | Are the §6.3 deltas acceptable? Frozen players can't drop, puppets can't attack or throw, spiked mobs can't interact, mobs in disposals can't use held items. | Each one is defensible, but each is a gameplay change. List it in that row's PR, or keep a residual event. |
| Q8 | Should spectators be able to inspect `InHandsOnly` UIs, for example a PDA lying on the floor (§8.6)? | Ask maintainers. If no, step 1 of §8.1 returns `Close` for spectators when `comp.InHandsOnly`. |
| Q9 | Should `BlockSpectators` stay entity-wide (today's `:159` behaviour) or become per-key like the rest of the policy (§8.1)? | Keep it entity-wide for slice 3 (parity), and revisit if a multi-key entity needs a mixed policy. |
| Q10 | What is the bookkeeper called once it replaces `ActionBlockerSystem` (§15.1)? | **`MobCapabilitiesSystem`** (done). The old working name, `MobConditionSystem`, undersold it: it answers for any entity, and it owns pair rules and target vetoes too. The component, vocabularies and events keep their condition names. |
| Q11 | `BlockMovementComponent` blocks `ChangeDirection` as well as `Move`, but `Immobilized` doesn't (§16.1). Should `Immobilized` block `ChangeDirection`? | No: paralysed legs, chasms and tethers let you turn today. Give `BlockMovement` its own declared pair, `Immobilized` plus a direction lock, or let it declare `Incapacitated` if its users never need hands. Check its prototype users (`pda.yml`, `silicon.yml`, `mothershipcore.yml` and others) first. |
| Q12 | `PostureLocked` blocks both `Stand` and `Down`, but the cryo pod only cancels `Down` today (§16.1). | Accept it: an occupant of a pod is already upright and can't move, so blocking `Stand` changes nothing observable. |
| Q13 | Pair rules (§15.3) and target vetoes (§15.4) are not cached. Is that a performance problem? | Not yet. Only a handful of subscribers handle each event, and the hot path (bucket 1) stays cached. Measure before optimizing. |
| Q14 | `MobCapabilitiesSystem.CanPickup(…, showPopup: true)` shows whatever reason blocked the pickup. But `SharedHandsSystem.TryPickup` hardwires `showPopup: true` (`SharedHandsSystem.Pickup.cs:90`), including for its ~95 programmatic callers such as `PickupOrDrop`. So an item handed to a stunned, cuffed or crit mob pops up "You can't do that while incapacitated" although nobody tried anything. | **Proposed fix:** show the reason in the consumer that knows a person initiated the action. (1) The bookkeeper never shows popups: `CanPickup` returns the reason, and `showPopup` only feeds the legacy pickup events. (2) `SharedHandsSystem.CanPickupToHand(…, showPopup)` shows the reason when asked. (3) `TryPickup` and `TryPickupAnyHand` take `bool showPopup = false` instead of hardwiring `true`, and the player-initiated paths pass `true`: picking up by clicking (`SharedItemSystem.OnHandInteract`, `Item/SharedItemSystem.cs:135`) and ejecting from item slots, which already passes it. Programmatic pickups then show no popup at all, not even the multi-handed message. That is correct, because nobody tried anything. Not implemented yet. |
| Q15 | `SentienceComponent` is shared by every `MakeSentient` caller (§15.4), so their grants combine. Is that acceptable? | Yes for sentience, which is never taken away. A grant that may be removed gets its own component. |
| Q16 | Do `Speak` and `Emote` become opt-out (the default has them, and creatures that can't talk name a profile that lacks them) or opt-in, like `Complex`? | Opt-out matches "everything unless blocked", but it needs an audit of every mob without `SpeechComponent` or `EmotingComponent`. Until it is decided, the component checks stay (§16.4). |
| Q17 | Rename `MobConditionsComponent` to `MobCapabilitiesComponent`, now that it holds the profile and the grants? | Yes, together with §15.4. |

## 12. PR slicing for upstream

1. **Core plus the first producer.** This PR contains:
   - the types (§2) and `MobCapabilitiesSystem` (§4–5), including the §4.5 consistency check;
   - the `ActionBlockerSystem` overload and the `InteractionAttemptEvent.Ignoring` field;
   - the StationAi cancel-only fix (§5.1);
   - the component on `MobDamageable`;
   - **MobState as the first producer**, together with the Speak change from §5.4;
   - unit tests for the tables and integration tests.

   Landing the core with no producer would ship an API that nothing uses. MobState exercises the
   whole loop with the lowest parity risk.
2. **The remaining producers, one commit each, in §6.2 order:**
   - Sleeping.
   - Stun.
   - Knockdown.
   - LegsParalyzed.
   - Cuffs, together with §9.1.
   - Ghost.

   Every commit deletes its handlers.
3. **UI tiers**, in a later phase. §8 plus paper. This fixes cuffed reading and observer access.
4. **Action bar at parity.** §7.1–7.3.
5. **Follow-ups, each separate:**
   - §6.3 producers.
   - §10.1 bridge.
   - §10.2 reasons.
   - §11 Q1 relay.
   - §7.4 and §9.3 balance changes.
   - Perception (design §8).

6. **End state (§15.5).** Each step is its own PR:
   - the forwarding step;
   - the two end-state events;
   - one PR per bucket area of §16;
   - the movement cache;
   - capabilities (§15.4);
   - the call-site migration;
   - removing `ActionBlockerSystem` and the obsolete events, a release after the new API is stable.

Each PR's description links design.md.

### Demo status (branch `mob-conditions`)

A demo implements a vertical slice of the model, one commit per step.

**Implemented:**
- the core (§2–5), including the §4.5 verify cvar, which is on in integration tests;
- the MobState, Stun, Cuffs and Sleeping producers;
- self-uncuff (§9.1);
- failure reasons (§10.2) for self-uncuff and the activate key, through
  `CanPerform(..., out reason)`;
- §15.5 step 1 (forwarding):
  - The `ActionBlockerSystem` bodies live in the bookkeeper (`MobCapabilitiesSystem.Actions.cs`),
    and `ActionBlockerSystem` only forwards to it.
  - The bookkeeper checks conditions directly, and still subscribes only to the events raised
    elsewhere (Stand, StandUp, StartPull, Point).
  - `MobActionAttemptEvent` and `MobActionTargetAttemptEvent` are raised but have no subscribers
    yet.
  - The `InteractionAttemptEvent.Ignoring` field and `CanShiver`/`CanSweat` are gone.
  - `MobConditionsChangedEvent` is also raised broadcast.
- requirements as capability masks, with `Complex` as the first creature-property bit
  (§2.5, §15.4);
- muting as `Silenced`, through the status-effect relay (§6.5); force-say and last words as
  ignore masks; speech popups from chat (§5.4).

**Deliberately not implemented yet:**
- **The UI tiers (§8)**, paper read-only and the close sweep. They were implemented here, then
  reverted: they are a refactor of their own, for a later phase. UIs, spectators included,
  behave as on master.
- **Capabilities** (§15.4): profiles, grants and revokes are designed, not built. `Complex` is
  still read from `ComplexInteractionComponent`.
- **Not migrated:** the action-bar `Requires` (§7), the EntityCondition bridge (§10.1), and the Ghost, Knockdown and LegsParalyzed producers. The §6.3 sources aren't migrated either. Their attempt events work as before.

## 13. Tests

Two kinds of test:
- **Pure-table tests** (`Expand`, `GetBlocked`, `Effective`, `GetBlockingConditions`) are plain
  NUnit tests against the static functions (§4.1). They need no server.
- **Integration tests** go in `Content.IntegrationTests/Tests/ActionBlocker/MobConditionTest.cs`
  (new folder). They derive from `InteractionTest` (`Tests/Interaction/InteractionTest.cs:45`),
  which is built on the `GameTest` fixture (`Content.IntegrationTests/Fixtures/GameTest*.cs`),
  with `[TestPrototypes]` in a sibling `MobConditionTestPrototypes.cs`. The precedent is
  `Tests/Clumsy/ClumsyStatusTest.cs`.

**Pure-table tests:**

| Test | Asserts |
|---|---|
| `CascadeFixedPoint` | `Expand(Unconscious)` contains `HandsBlocked \| PullBlocked`; `Expand(Restrained)` contains `PullBlocked`. |
| `IgnoringDerivedBit` | `Effective(Restrained, HandsBlocked)` does not block `Interact`; `Effective(Unconscious \| Restrained, HandsBlocked)` still blocks it (§4.3). |
| `BlockingConditionsMultiCause` | `GetBlockingConditions` for `Restrained \| Unconscious` and `Interact` returns both bits (§10.2). |
| `NoConditionBlocksComplex` | Every declarable condition together, expanded, still doesn't block `Complex` (§3.2). |

**Integration tests:**

| Test | Asserts |
|---|---|
| `RestraintIsForgivable` | Cuffed: `CanInteract` false; with `Ignoring = Restrained`, true. Cuffed and asleep: still false with the ignore. |
| `WakeWhileCuffed` | Cuff, sleep, wake: `Restrained` persists and interaction stays blocked. |
| `WakeStandsUp` | Sleep, then wake. The mob stands up on the wake tick, which proves `Refresh` ran before `TryStanding` (§6.2). Implemented as `SleepIsUnconsciousAndWakeStandsUp`, which also checks what sleep blocks. |
| `LastWordsWhileAsleepInCrit` | Asleep, then crit: force-say's mask passes, and with a mute added, last words' mask passes (§5.4). |
| `ForceSayOnFallingAsleep` | Asleep with the force-say grant: chat's hook forgives `Unconscious` and Speak passes. |
| `FlooredStillAllowsStandingUp` | Knocked down: `Stand` blocked, `StandUp` not blocked, and the mob recovers after the knockdown time. |
| `ReviveAfterCritKnockdownStandsUp` | Knock down, crit, revive: the mob recovers and stands (§3.2 delta). |
| `UnconsciousBlocksEverything` | Crit: every §2.2 capability but `Down` and `Complex` is blocked (through the cascade). A query neither honours nor spends the force-say grant. |
| `ForceSayForgivesUnconsciousOnce` | Crit with the grant: chat's hook returns `Unconscious` and Speak passes with it; a tick later the grant is spent and the hook returns nothing. A hook call that skips every check doesn't spend it (§5.4). |
| `RequirementIsASet` | Cuffed: `Speak` passes, `Speak \| Interact` fails (§2.5). |
| `ComplexIsACreatureProperty` | Without `ComplexInteractionComponent`, `Interact \| Complex` fails and `Interact` passes; `IsBlocked(Complex)` is false (§15.4). |
| `MutedIsSilenced` | Muted: `Silenced` on server and client, Speak blocked with the `speech-muted` reason, Interact allowed; unmuted clears it on both (§6.5). |
| `MimeReasonComesFromEffect` | With `StatusEffectMimeMuted`, the Speak reason is the vow text (§2.4). |
| `LastWordsMask` | Crit and muted: `Ignoring = Unconscious \| Silenced` passes; force-say's `Unconscious` alone fails with the mute reason (§5.4). |
| `DelayedMuteSilencesOnStart` | A delayed mute isn't `Silenced` until it starts (§6.5). |
| `DeadMimeNoPopup` | Dead mob with `StatusEffectMimeMuted`: a chat message is dropped and no popup is raised (§5.4). Not written yet: it needs the client's popup labels. |
| `CacheConsistency` | With §4.5 on, run stun, cuff, sleep, crit and ghost transitions; no assert fires. |
| `ClientPredictionMatchesServer` | After a predicted stun and after the server state arrives, the client's `IsBlocked` and `IsBlocked(…, Restrained)` match the server's (§2.3). |
| `StationAiDoesNotUncancel` | An AI-held entity that something else has already cancelled stays cancelled (§5.1). |
| `GhostReadOnly` | UI phase (§8). A non-admin ghost opens a `RequiresComplex` machine UI as `ReadOnly`; an input message is rejected server-side; `BlockSpectators` gives `Close`. An admin observing as an ordinary ghost also gets `ReadOnly`. |
| `GhostNoSingleUserLock` | UI phase (§8). A ghost opens a `SingleUser` console; a crew member can still open and use it. |
| `GhostOpenNoSideEffects` | UI phase (§8). A ghost opening a trigger-on-open entity fires no trigger, and opening a shuttle console adds no `PilotComponent` (§8.5). |
| `CuffedPaperReadOnly` | UI phase (§8). A cuffed user activates paper and the UI opens; a write message is rejected; after uncuffing, the write succeeds. |
| `StunKeepsWindowDisabled` | UI phase (§8). An open machine UI stays open while the user is stunned, and its input is rejected. After the stun it works again. |
| `CritClosesWindow` | UI phase (§8). An open machine UI closes when the user goes crit; the borg laws UI (`requireInputValidation: false`) does not. |
| `SelfUncuff` | A cuffed mob can start uncuffing itself, and `CanStillInteract` is never flipped (no extra `Dirty`). |
| `VehicleReentrancy` | Stunning a vehicle operator stops vehicle movement in the same tick (R1). |
| `ActionParity` | For each §7.2 row, an action with that `requires` validates in exactly the states it did with the old bools: awake, stunned, cuffed, crit. |

Existing suites MUST stay green: cuffs, paper, ghost, UI, sleep, mob state, interaction, stun,
buckle, pulling, hands, actions, changeling, vehicle, speech/muting.

## 14. Conventions checklist

- Every new system is `sealed partial`. RA0049 is an error in Release (`MSBuild/Content.props`).
- `[SubscribeLocalEvent]` attributes are preferred. Never subscribe the same event both ways,
  and never duplicate a directed (component, event) pair. That crashes at init, as seen with
  `GhostComponent, ComponentStartup` and `MobStateComponent, MobStateChangedEvent`.
- No BOMs (#45796). The new files are UTF-8 without a BOM.
- `GhostComponent` lives in `Content.Shared.Ghost.Components`, and `SharedGhostSystem` in
  `Content.Shared.Ghost.Systems`.
- XML doc `cref`s must use real namespaces. `StartPullAttemptEvent` is
  `Content.Shared.Pulling.Events`.

## 15. End state: the bookkeeper

### 15.1 Responsibilities

One system, the *bookkeeper*, answers every "can this entity do X?" question. Its working name
is `MobCapabilitiesSystem`; the final name is §11 Q10.

It owns four mechanisms, one per bucket of §16:

| Bucket | Question | Mechanism | Cached |
|---|---|---|---|
| 1. Conditions | What is wrong with the entity? | producers declare `MobCondition`s (§2–4) | yes |
| 2. Target vetoes | Can this target be acted on at all? | one event raised on the target (§15.3) | no |
| 3. Pair rules | Is this action allowed for this user against this target? | one event raised on the user (§15.3) | no |
| 4. Capabilities | What can this kind of creature do at all? | an immutable profile prototype, plus grants and revokes declared by sources (§15.4) | yes |

**Rules:**
- **The bookkeeper answers for any entity**, mob or not: borgs, AI eyes and vehicles too. An
  entity without `MobConditionsComponent` has no conditions and the default capabilities
  (§15.4), and buckets 2 and 3 still apply to it.
- **No other system raises an event to decide whether an action is blocked.** In the end state,
  the typed attempt events (`InteractionAttemptEvent`, `UseAttemptEvent`, `UpdateCanMoveEvent`,
  `StandAttemptEvent`, `GettingPickedUpAttemptEvent` and the rest) no longer exist.
- **Domain rules stay in their domain.** Buckling, slipping, seeing and climbing onto things have
  their own attempt events. They are not blockers and are outside §16. Their handlers become
  *consumers*: a buckle rule that needs "can the user act" asks the bookkeeper.
- **Consumers never re-implement blocking.** Code that today checks `HasComp<StunnedComponent>`,
  `CanStillInteract` or `MobState` to decide whether someone can act asks the bookkeeper instead.

### 15.2 Consumer API

```csharp
public bool CanPerform(EntityUid user, in InteractionRequirements req,
    EntityUid? target = null, EntityUid? used = null);

public bool CanPerform(EntityUid user, in InteractionRequirements req,
    EntityUid? target, EntityUid? used, out string? reason);
```

**Evaluation order** (cheapest first; the first failure wins, and supplies `reason`):

1. **Capabilities.** Every required capability must be in the creature's capabilities (§15.4).
   A missing one is more basic than any condition, so it is checked, and explains the failure,
   first.
2. **Conditions.** `IsBlocked(user, req.Required, req.Ignoring)`, from the cache (§4.3). The
   reason comes from the condition (§10.2).
3. **Pair rule.** Raise `MobActionAttemptEvent` on the user (§15.3).
4. **Target veto.** If `target` is set and isn't the user, raise `MobActionTargetAttemptEvent` on
   the target. For `UseHeld` and `Throw`, raise it on `used` as well.

**Mapping from today's API:**

| `ActionBlockerSystem` | Bookkeeper |
|---|---|
| `CanInteract(user, target)` | `CanPerform(user, {Interact}, target)` |
| `CanConsciouslyPerformAction(user)` | `CanPerform(user, {Conscious})` |
| `CanUseHeldEntity(user, used)` | `CanPerform(user, {UseHeld}, used: used)` |
| `CanPickup(user, item)` | `CanPerform(user, {Pickup}, item)` |
| `CanDrop(user)` | `CanPerform(user, {Drop})` |
| `CanThrow(user, item)` | `CanPerform(user, {Throw}, used: item)` |
| `CanAttack(user, target, weapon, disarm)` | `CanPerform(user, {Attack}, target, weapon)`. The container rule in `CanAttack` (`ActionBlockerSystem.cs:180–197`) becomes a built-in step 3 rule, and the vehicle's container override becomes a pair rule (§16.3). `disarm` and the weapon go in the event. |
| `CanSpeak(user)`, `CanEmote(user)`, `CanChangeDirection(user)` | `CanPerform(user, {Speak / Emote / ChangeDirection})` |
| `CanMove(user)` | `CanMove(user)`, the movement cache (§15.3) |
| `UpdateCanMove(user)` | `Refresh(user)`: the caller is a producer whose state changed |
| `CanComplexInteract(user)` | `HasCapability(user, Complex)` (§15.4), or `Complex` in the mask |
| `CanShiver`, `CanSweat` | deleted. They have callers (`ThermalRegulatorSystem.cs:90/97`) but no subscribers, so they always return true. |

### 15.3 The two events, and movement

```csharp
[ByRefEvent]
public record struct MobActionAttemptEvent(EntityUid User, MobCapability Required, EntityUid? Target, EntityUid? Used)
{
    public bool Cancelled;
    public string? Reason;   // optional, localized; shown by consumers that report failures
}

[ByRefEvent]
public record struct MobActionTargetAttemptEvent(EntityUid User, MobCapability Required, EntityUid? Used)
{
    public bool Cancelled;
    public string? Reason;
}
```

- Both events are **cancel-only** (§5.1). A handler sets `Cancelled = true` or does nothing.
- Each replaces several typed events:
  - `MobActionAttemptEvent` takes over the pair-rule subscribers of today's typed user events.
  - `MobActionTargetAttemptEvent` takes over `GettingInteractedWithAttemptEvent`,
    `GettingPickedUpAttemptEvent`, `GettingUsedAttemptEvent`, `ThrowItemAttemptEvent` and
    `GettingAttackedAttemptEvent`.
- A handler tests the `Required` bits it cares about. One subscription per system covers every action it cares
  about, which also avoids the engine's one-subscription-per-component-and-event limit.
- **`Reason` replaces ad-hoc popups** inside attempt handlers. Examples are the multi-handed
  item popup and the worm's stand-up message. The consumer decides whether to show it.

**Movement.** The bookkeeper owns the movement cache:
- `InputMoverComponent.CanMove` is written only by `Refresh`. Its value is
  `(Blocked & Move) == 0`, together with the movement check on the body (§15.4).
- `Refresh` raises `CanMoveUpdatedEvent` when the value changes, and so does the client-side
  derivation in `AfterAutoHandleStateEvent`.
- `UpdateCanMoveEvent` is deleted. Each of its subscribers becomes a producer (§16.1), and each
  of the 26 `UpdateCanMove` callers becomes a `Refresh`.
- This removes today's two-cache synchronization. That includes the extra `UpdateCanMove` calls
  in the demo's stun producer and in `OnHandleState`.

### 15.4 Capabilities: profiles, grants and revokes

> **Designed, not implemented.** The demo still reads `ComplexInteractionComponent` at query
> time (`GetPropertyBlocked`). This section replaces it.

Two layers answer "can it?":
- **Capabilities:** what this kind of creature can do at all. A mouse can't use a computer, and
  a brain can't point.
- **Conditions** (§2–4): what is stopping it right now.

```
Profile      = (Default | profile.Innate) & ~profile.Lacking
Capabilities = (Profile | Granted) & ~Revoked
CanPerform   requires (required & ~Capabilities) == 0 and (required & Blocked) == 0
```

**Rules:**
- **Revoke beats grant.**
- **Grants don't override conditions.** A grant gives a creature something its kind lacks; it
  doesn't uncuff anyone.
- **Ignore masks forgive conditions only.** A missing capability can't be forgiven at a call site.
- **Choosing a layer.** If a call site should ever be able to forgive the block, it is a
  condition. If it changes what the creature is, it is a capability.
- **The vocabulary stays closed.** A capability exists because the bookkeeper's API asks for it,
  not to model creature traits in general. Literacy and the like stay their own components
  (design §7).

**Profiles.** The baseline is a prototype, so nothing can change it at runtime.

```csharp
[Prototype("mobCapabilityProfile")]
public sealed partial class MobCapabilityProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public MobCapability Innate;   // added to the built-in default
    [DataField] public MobCapability Lacking;  // removed from it
}
```

```yaml
- type: mobCapabilityProfile
  id: Dexterous      # humanoids, borgs, the AI, the admin ghost, guardians, xenos
  innate: [Complex]

- type: mobCapabilityProfile
  id: Brain
  lacking: [Point]
```

The component names one:
`[DataField, AutoNetworkedField] public ProtoId<MobCapabilityProfilePrototype>? Profile;`
- **The built-in default** is every capability except `Complex`. That is today's behaviour:
  everything is allowed unless something blocks it, and `Complex` is opt-in. A component without
  a profile, and an entity without the component, both get the default.
- **Profiles are differences from the default**, so a capability added later reaches every
  profile without an edit.
- **The ID never changes at runtime.** It travels with the initial state and is saved as an ID.
  Writes are restricted with `[Access]`, and there is no setter.
- **Profiles are prototype data**, so the client always knows them. That also settles the §6.1.6
  problem for the brain's `Point` block: `BrainComponent` isn't networked, but the profile needs
  no networking.

**Sources.** Every change at runtime is declared by something on the entity: a component, an
item, an implant or a status effect. `RefreshMobConditionsEvent` gains two methods next to
`Add(MobCondition)`:

```csharp
public void Grant(MobCapability capabilities);
public void Revoke(MobCapability capabilities);
```

- The §6.1 producer rules apply unchanged. The handler only declares, the source refreshes when
  its state flips, and it is shared and networked.
- **A permanent change is a source that stays**, saved with the entity. Nothing stores what the
  baseline used to be, and every capability has a visible cause: the profile, or a source.
- **A removable source MUST be its own component.** Sources that share a component share their
  grants, so removing one would take away the other's.
- `Granted` and `Revoked` are cached and networked next to `Declared`, and §4.5 verifies them.
- `MobConditionsChangedEvent` also fires when `Capabilities` change, carrying the old and new
  sets.

**Queries.**
- `HasCapability(uid, capabilities)` answers from the capability layer only, ignoring
  conditions. It replaces `CanComplexInteract`.
- `CanPerform` checks capabilities first (§15.2). A missing capability's reason comes from
  `mob-capability-lacking-<member>`, for example "You lack the dexterity to do that."

**Mapping**, which replaces the creature-property table:

| Capability | Today | Becomes |
|---|---|---|
| `Complex` | `ComplexInteractionComponent`, read at query time | the `Dexterous` profile. The component becomes an obsolete grant source (below). |
| `Point` | `BrainSystem` and `SharedBorgSystem` cancel | the `Brain` profile lacks it. |
| `Speak`, `Emote` | `SpeechSystem` and `EmoteSystem` cancel without an enabled component | open (Q16). |
| `Move` | `InputMoverComponent` and a non-static body | stays a check on the body, because it feeds the movement cache. A change of body type calls `Refresh`. |

**Removing `ComplexInteractionComponent`.** It is a pure marker in 9 prototypes, read in 44 places,
and removed at runtime in one, the zombie transform (`Zombies/ZombieSystem.Transform.cs:153`).
1. **Prototypes:** the 9 prototypes name the `Dexterous` profile. `Body/species_appearance.yml`
   covers every humanoid.
2. **Deprecation window:** `ComplexInteractionComponent` becomes `[Obsolete]` and a grant source
   that declares `Grant(Complex)`. Downstream prototypes keep working until the removal release.
3. **Readers:** the 44 readers move to `HasCapability(Complex)`. The obsolete warnings track what
   is left.
4. **Zombies:** `ZombieComponent` declares `Revoke(Complex)` instead of removing the marker.
   Revoke beats grant, so no item can give a zombie its hands back.

**Example: cognizine.** Today the reagent's `MakeSentient` effect
(`EntityEffects/Effects/MakeSentientEntityEffectSystem.cs`) removes the squeak accents and adds
a ghost role. When a ghost takes the role, `SharedMindSystem.MakeSentient`
(`Mind/SharedMindSystem.cs:750`) ensures `SpeechComponent` and `EmotingComponent`. With
capabilities:
1. **The effect** still removes the accents and adds the ghost role. Accents are flavour.
2. **`MakeSentient`** ensures a `SentienceComponent` (name open), adds its grants to the
   component's `Grants` and refreshes. The component declares `Grant(Grants)` and is saved with
   the mob. `SpeechComponent` is still ensured, for its sounds and verb.
3. **Uplift is data.** The effect's `allowSpeech` bool becomes `grants: [Speak, Emote]`. A
   reagent that also lists `Complex` uplifts a monkey enough to use computers: one line of YAML
   and no code.
4. **Results:**
   - A mouse's default profile already has `Speak`, so nothing changes for it. A creature whose
     profile lacks `Speak` gains it.
   - Conditions still apply: a sentient mouse in crit can't speak, and last words still forgive
     that.
   - If the mob later becomes a zombie, the zombie's revokes win.
5. **Shared callers:** ghost roles, the admin verb, polymorph, zombification and the changeling
   slug all call `MakeSentient`. They share `SentienceComponent`, and with it their grants (Q15).

### 15.5 Migration

1. **Forward.**
   - Move every `CanX` body into the bookkeeper. Each `ActionBlockerSystem` method becomes a
     one-line forward.
   - The bookkeeper checks conditions directly. It no longer subscribes to
     `InteractionAttemptEvent` or `ConsciousAttemptEvent`, which removes the
     `InteractionAttemptEvent.Ignoring` field (§2.7) and the §5.1 exceptions.
   - During the migration the bookkeeper raises the legacy typed events after its own checks.
     It is the only place that still raises them.
   - Add `MobActionAttemptEvent` and `MobActionTargetAttemptEvent`.
   - Delete `CanShiver` and `CanSweat`.
2. **Move subscribers out, one §16 row per commit.** When an event has no upstream subscribers
   left, mark it `[Obsolete]` and keep raising it until the removal release (§1).
3. **Move the movement cache** (§15.3) once every `UpdateCanMoveEvent` row has moved.
4. **Add capabilities** (§15.4): profile prototypes, grants and revokes. Name the profiles in
   prototypes, make `ComplexInteractionComponent` an obsolete grant source, and move bucket 4
   (§16.4) over.
5. **Move the ~140 call sites** to the bookkeeper, one PR per area. This is mechanical, and
   the `[Obsolete]` warnings track what is left.
6. **Remove `ActionBlockerSystem`, `ComplexInteractionComponent` and the obsolete events** in a
   later release, once the new API has been stable for one.

Steps 2–5 can interleave. Step 1 has to come first, because it fixes the direction of the
dependency: `ActionBlockerSystem` depends on the bookkeeper, never the other way round.

**Done so far:**
- Step 1.
- `StandingStateSystem`, knockdown, `PullingSystem` and `SharedPointingSystem` now ask the
  bookkeeper instead of raising their own attempt events. The bookkeeper therefore subscribes to
  no attempt event at all. Stand up keeps its in/out event data through a typed `CanStandUp`.
- All of §16.2.
- Producers no longer add the component or hook lifecycle events for the bookkeeper (§4.4).
- Requirements are masks. `Complex` is a capability bit, still read from
  `ComplexInteractionComponent` until step 4.
- Muting declares `Silenced` through the status-effect relay (§6.5). Force-say and last words
  are masks (§5.4).

### 15.6 Vocabulary changes

The vocabulary is closed. It is exactly the bookkeeper's API plus what its producers declare. A
member is added together with the producer or consumer that needs it.

**New conditions:**

| Condition | Blocks | Declared by (§16) |
|---|---|---|
| `Pinned` | `StandUp` | the `KnockdownStatusEffect` (through the Q1 relay), Worm |
| `PostureLocked` | `Stand`, `Down` | buckle (while buckled), cryo pod occupant |
| `Silenced` | `Speak` | the muted status effect, through the Q1 relay. **Done** (§6.5). Not the admin freeze: an admin mute must not be forgivable, so it becomes a pair rule (§16.3). Not `Emote`: mimes emote. |

**New actions:**

| Action | Consumer |
|---|---|
| `Down` | `StandingStateSystem.Down`, replacing `DownAttemptEvent` |
| `Equip` | the inventory's self-equip and self-unequip path (user == target), replacing the self-only `IsEquippingAttemptEvent` / `IsUnequippingAttemptEvent` handlers in MobState, Stun and Cuffs |

**Policy additions** (to §3.2): `Incapacitated` and `HandsBlocked` block `Equip`.

`Down` is blocked only by `PostureLocked`. In particular, `Unconscious` must **not** block
`Down`: crit and death lie the mob down through `StandingStateSystem.Down`
(`MobStateSystem.Subscribers.cs:49`).

### 15.7 Not gates

One attempt-event subscriber blocks nothing: `DnaImprintEntityEffectSystem.OnPointAttempt`
clears follower targets as a side effect. It moves into its existing `AfterPointedAtEvent`
handler, which clears the targets first and then sets the new ones. Attempt events are for
asking, not for reacting.

## 16. Attempt-event inventory

Every subscriber, at the demo head of `mob-conditions` (`ed4bc915c5`; line numbers in §15–16 are
against it), of:
- the events `ActionBlockerSystem` raises;
- the standing, stand-up, down, pull and point attempt events;
- their target-side counterparts.

Self-only equip and unbuckle checks that duplicate them are included too.

Buckets are those of §15.1, plus **N** (not a gate, §15.7). "Done" means migrated in the demo.

### 16.1 Bucket 1: conditions

| System (file) | Events cancelled today | Declares | Notes |
|---|---|---|---|
| `MobStateSystem` (`Mobs/Systems/MobStateSystem.Subscribers.cs`) | 11 via `CheckAct`, Conscious, Speak | `Unconscious` | **Done.** The self equip/unequip checks move to `Equip`. The self-unbuckle check becomes a buckle consumer asking `Interact`. |
| `SharedStunSystem`, `StunnedComponent` (`Stunnable/SharedStunSystem.cs`) | ChangeDirection, Move, Interaction, Use, Throw, Drop, Attack, Pickup | `Incapacitated \| Immobilized` | **Done.** The self equip/unequip checks move to `Equip`. |
| `SharedStunSystem.Knockdown`, `KnockedDownComponent` | Stand | `Floored` | |
| `SharedStunSystem`, `KnockdownStatusEffectComponent` relay (`:69/371`) | StandUp | `Pinned` | Needs the Q1 status-effect relay of `RefreshMobConditionsEvent`. |
| `SleepingSystem` (`Bed/Sleep/SleepingSystem.cs`) | Speak, Point, Conscious, StandUp, Emote | `Unconscious` | **Done.** Refresh before `SleepStateChangedEvent` (§6.2). The self-unbuckle check becomes a buckle consumer. |
| `SharedCuffableSystem` (`Cuffs/SharedCuffableSystem.cs`) | Drop, Pickup, Attack, Use, Interaction | `Restrained` | **Done.** |
| `SharedCuffableSystem.HandleMoveAttempt` (`:259`) | Move, while restrained *and* pulled | `Immobilized`, while restrained and pulled | `OnPull` already fires on pull start and stop; it becomes `Refresh`. |
| `SharedGhostSystem` (`Ghost/Systems/SharedGhostSystem.cs`) | Use, Emote, Drop, Pickup, Interaction | `Incorporeal` | |
| `LegsParalyzedSystem` (`Traits/Assorted/LegsParalyzedSystem.cs`) | Move | `Immobilized` | |
| `AdminFrozenSystem` (`Administration/AdminFrozenSystem.cs`) | Use, Pickup, Throw, Interaction, Attack, ChangeDirection, Move; Speak and Emote if `Muted` | `Incapacitated \| Immobilized` | Delta: +`Drop` (Q7). The `Muted` speech and emote block is a pair rule (§16.3), not `Silenced`, so no mask can forgive it. |
| `SharedVentriloquistPuppetSystem` (`Puppet/SharedVentriloquistPuppetSystem.cs`) | Use, Interaction, Drop, Pickup, Move, ChangeDirection, Emote | `Incapacitated \| Immobilized` | Delta: +`Attack`, +`Throw` (Q7). The Emote block moves to bucket 4: check that the puppet prototype (`Objects/Fun/toys.yml:167`) has no enabled emoting. |
| `SharedKitchenSpikeSystem`, `KitchenSpikeHookedComponent` | ChangeDirection, Use, Throw, Drop, Attack, Pickup | `Incapacitated` | Delta: +`Interact` (Q7). |
| `SharedBeingDisposedSystem` | Interaction, Attack | `HandsBlocked` | Delta: +`UseHeld`, `Pickup`, `Drop` (Q7). |
| `WormSystem` (`Movement/Systems/WormSystem.cs`) | StandUp (also sets a message and turns autostand off) | `Pinned` | The message becomes the `Pinned` failure reason. The worm sets autostand on its own knockdown state instead of through the event. |
| `ChasmSystem`, `ChasmFallingComponent` | Move | `Immobilized` | |
| `SharedTetherGunSystem`, `TetheredComponent` | Move | `Immobilized` | |
| `SharedBuckleSystem` (`Buckle/SharedBuckleSystem.Buckle.cs`) | Down, Stand, Move while buckled | `PostureLocked`, plus `Immobilized` unless the entity has `RelayInputMoverComponent` | Refresh on buckle and unbuckle. |
| `SharedInsideCryoPodSystem` | Down | `PostureLocked` | Delta: +`Stand` (Q12). |
| `ClimbSystem`, `ClimbingComponent` | Move while `NextTransition != null` | `Immobilized` while mid-climb | Refresh when a climb starts and ends. |
| `VehicleSystem`, `VehicleComponent` | Move when `VehicleCanRunEvent` says it can't run | `Immobilized`, **on the vehicle** | The vehicle gets `MobConditionsComponent`. It refreshes when the inputs to `VehicleCanRunEvent` change. |
| `VehicleSystem`, `VehicleOperatorComponent` (`:82`) | Move when the operator can't operate | `Immobilized`, **on the vehicle**, when its operator can't operate | **Cross-entity.** The vehicle refreshes on its operator's `MobConditionsChangedEvent` (broadcast). This removes the R1 re-entrancy case: no handler calls `CanX` on its own entity any more. |
| `SharedShuttleConsoleSystem`, `PilotComponent` | Move while piloting | `Immobilized` | |
| `SharedInteractionSystem.Blocking`, `BlockMovementComponent` | Interaction, Drop, Pickup if `BlockInteraction`; Use if `BlockUse`; Move and ChangeDirection always | `HandsBlocked` for the interaction flags; `Immobilized` for movement | `ChangeDirection` has no matching condition (Q11). |
| `MutedStatusEffectSystem` (`Speech/Muting/MutedStatusEffectSystem.cs`) | Speak, through the status-effect relay, with a popup | `Silenced`, through the Q1 relay | **Done** (§6.5). The popup became the failure reason, which the effect words through `GetMobConditionReasonEvent`. Chat shows it at send time, except to an unconscious mind, so the §5.4 relay ordering is gone. |
| `ReplaySpectatorSystem` (`Content.Client/Replay/Spectator/ReplaySpectatorSystem.Blockers.cs`) | Use, Pickup, Throw, Interaction, Attack, Drop, Move, ChangeDirection | `Incorporeal \| Immobilized` | Client-only, so it is the one exception to §6.1.6: a replay has no server to disagree with. |

### 16.2 Bucket 2: target vetoes (`MobActionTargetAttemptEvent`)

| System | Event today | Rule |
|---|---|---|
| `SharedVirtualItemSystem` (`Inventory/VirtualItem/SharedVirtualItemSystem.cs:44`) | GettingInteractedWith | **Done.** A virtual item can't be interacted with (`Interact`). |
| `SharedSubFloorHideSystem` (`SubFloor/SharedSubFloorHideSystem.cs:40–41`) | GettingInteractedWith, GettingAttacked | **Done.** Can't touch or attack things hidden under the floor. Users with `BypassInteractionChecksComponent` are exempt from the touching part. |
| `MultiHandedItemSystem` (`Item/MultiHandedItemSystem.cs:19`) | GettingPickedUp | **Done.** Needs enough free hands. Its popup became `Reason`, which `CanPickup(…, showPopup: true)` shows. |
| `SharedMechSystem.Relay` (`Mech/EntitySystems/SharedMechSystem.Relay.cs:9`) | GettingAttacked | **Done.** Relays the `Attack` veto to the pilot as `MechPilotRelayedEvent<MobActionTargetAttemptEvent>`, and the guardian's relayed handler moved with it. It also keeps relaying the obsolete `GettingAttackedAttemptEvent`, so downstream subscribers of `MechPilotRelayedEvent<GettingAttackedAttemptEvent>` keep working until the removal release. |
| client `DragDropSystem` (`Content.Client/Interaction/DragDropSystem.cs:521`) | raised GettingInteractedWith by hand for the dragged entity | **Done.** Asks `CanPerform` about the dragged entity instead. |
| none | `GettingUsedAttemptEvent`, `ThrowItemAttemptEvent` | No subscribers. |

All five typed target events are `[Obsolete]`. The bookkeeper still raises them for downstream
subscribers (§1).

**One behaviour change:** `CanPickup(…, showPopup: true)` now shows whichever reason blocked
the pickup. That includes a condition such as "You can't do that while incapacitated", where it
used to stay silent.

### 16.3 Bucket 3: pair rules (`MobActionAttemptEvent`)

| System | Event today | Rule |
|---|---|---|
| `PacificationSystem` (`CombatMode/Pacification/PacificationSystem.cs:29`) | Attack | A pacified user can't make harmful attacks. It asks the target through its own `AttemptPacifiedAttackEvent`; the subscribers of that event (`PacificationSystem:31`, `MobStateSystem.Subscribers.cs:186`) are internal to this rule and stay. |
| `SharedStationAiSystem.Held` (`Silicons/StationAi/SharedStationAiSystem.Held.cs:148`) | Interaction | The AI can only interact with whitelisted targets. |
| `GuardianSystem` (`Guardian/GuardianSystem.cs:130, 147`) | Attack; the relayed mech-pilot GettingAttacked | A guardian can't attack its host, including when the host pilots a mech. |
| `SharedBuckleSystem` (`Buckle/SharedBuckleSystem.Buckle.cs:42`) | StartPull | Can't pull the thing you're buckled to, unless `PullStrap`. |
| `VehicleSystem` (`Vehicle/Systems/VehicleSystem.cs:103`) | Attack | An operator can't attack their own vehicle when the vehicle `CanAttack`. |
| `VehicleSystem` (`:93`) | `CanAttackFromContainerEvent` | Lets an operator attack out of their vehicle. It becomes an exception to the built-in container rule (§15.2). |
| `AdminFrozenSystem` (`Administration/AdminFrozenSystem.cs:34`) | Speak and Emote, if `Muted` | An admin-muted user can't speak or emote. A pair rule, not `Silenced`, because a mask must never forgive it: Last Words forgive `Silenced`. |
| `DnaImprintEntityEffectSystem`, `NPCImprintedComponent` (`Content.Server/EntityEffects/Effects/DnaImprintEntityEffectSystem.cs:130`) | Attack | An imprinted NPC won't attack friends. Server-only: unlike conditions, a pair rule is evaluated at query time, so a server-only rule only mispredicts for NPCs, which aren't predicted anyway. |

### 16.4 Bucket 4: capabilities

| System | Event today | Becomes (§15.4) |
|---|---|---|
| `SpeechSystem` (`Speech/SpeechSystem.cs:9`) | Speak (broadcast) | open (Q16); `SpeechComponent` until then |
| `EmoteSystem` (`Emoting/EmoteSystem.cs:9`) | Emote (broadcast) | open (Q16); `EmotingComponent` until then |
| `BrainSystem` (`Body/Systems/BrainSystem.cs:20`) | Point | the `Brain` profile lacks `Point` |
| `SharedBorgSystem` (`Silicons/Borgs/SharedBorgSystem.cs:101`) | Point | the `Brain` profile lacks `Point` |
| `SharedMoverController` (`Movement/Systems/SharedMoverController.cs:93`) | Move | stays a check on `InputMoverComponent` and the body |
| `ActionBlockerSystem.CanComplexInteract` | (a method, not an event) | the `Dexterous` profile. The `Complex` bit is done; it still reads `ComplexInteractionComponent`. |
| `ZombieSystem.Transform` (`Zombies/ZombieSystem.Transform.cs:153`) | removes `ComplexInteractionComponent` | `ZombieComponent` revokes `Complex` |

### 16.5 N: not gates

| System | Event today | Destination |
|---|---|---|
| `DnaImprintEntityEffectSystem`, `DNALeaderComponent` (`:115`) | Point (never cancels) | `AfterPointedAtEvent` (§15.7) |
| `MutedStatusEffectSystem` | `EmoteEvent`, `EmoteActionEvent` (relayed) | Stays. It muffles vocal emotes and the scream action rather than gating anything. |

### 16.6 Status-effect relays

The relays in `StatusEffectNew/StatusEffectSystem.Relay.cs` are plumbing, not rules:
- **`UpdateCanMoveEvent` (`:43`):** no subscribers. Delete it.
- **`RefreshMobConditionsEvent` and `GetMobConditionReasonEvent`:** relayed (Q1, done). The muted
  status effect declares `Silenced` and words its reason through them.
- **`SpeakAttemptEvent` (`:60`):** no upstream subscribers left. It stays relayed for downstream
  subscribers while `SpeakAttemptEvent` exists.
- **`StandUpAttemptEvent` (`:51`):** replaced by the same relay once the knockdown status effect
  declares `Pinned` (Q2).

## Appendix A: the July prototype

A working implementation of slices 1 and 3 exists as a local stash against upstream
`3d9a18a43c`. It is a reference, not a patch to apply. Re-implementing it against the current
code needs these changes:

- **Renames:**
  - `ActionRequirements` → `InteractionRequirements`.
  - `CanPerformAction` → the `CanInteract` overload (§5.2).
  - `RefreshMobConditionsEvent`: class → by-ref struct.
- **`MobStateSystem.Subscribers.cs`** now uses attribute subscriptions for `CheckConcious` and
  `OnSpeakAttempt`. Delete the methods together with their attributes, and don't re-add
  explicit subscriptions. Keep the new `OnIncapCuffCheck`.
- **`SleepingSystem.cs`:** keep the new `OnIncapCuffCheck` when deleting `OnStandUpAttempt`.
  Remove the orphaned "Prevents the use of emote actions" doc comment along with
  `OnEmoteAttempt`.
- **Ghost** moved to `Ghost/Systems/`. Port by hand.
- **`ActivatableUISystem.Status.cs`:** use `using Content.Shared.Ghost.Components`.
- **`ActivatableUIStatusSystem`** must be `partial`.
- **Superseded by the October review.** Don't copy these from the prototype:
  - The re-polling cold path and the `Conditions`/`Blocked` networked fields. Use `Declared`
    (§2.3, §4.3).
  - The single-pass ignore mask. Use `Effective` (§4.3).
  - The two-argument `GetUiStatus`, the admin-ghost rule, `Close` for every denial, and the
    close-on-any-worsening sweep (§8).
- **Not in the prototype at all:**
  - The Speak relay ordering (§5.4).
  - The action-bar integration (§7).
  - The EntityCondition bridge (§10).
  - The #46045 range cancel and open veto, which must be kept (§8.2).
  - The open side-effect audit (§8.5) and the cache consistency check (§4.5).

## Appendix E: fourth review (2026-10-23)

A design review of the demo. Changed:
- **Requirements are a set.** `MobAction` became `MobCapability`, and `InteractionRequirements.Required`
  and both rule events carry a mask. `NeedComplex` became the `Complex` bit, the first creature
  property (§15.4).
- **The crit speech grant left the bookkeeper.** What the self-check called "last words" was the
  damage force-say grant. It was hardcoded there and spent inside a query. Chat now forgives
  `Unconscious` for the force-said message, at send time (§5.4).
- **Muting is `Silenced`**, through the status-effect relay (Q1, §6.5). The Last Words action
  forgives it by mask, instead of skipping every check. Chat shows speech popups, worded by the
  source (§2.4).
- **`StatusEffectsSystem` marks an effect applied before announcing it**, so refreshes in the
  applied event see it.
- **The UI tiers were reverted**, to a later phase of their own (§8). The bookkeeper changes
  don't depend on them. `UiAccess` and `UiBlocked` went with them, since nothing else
  consumed them. The activate-key failure popup stays: it is interaction feedback, not a
  tier.
- **Sleeping became a producer.** The review found that Last Words, once a mask, failed for a
  mob asleep and in crit: crit doesn't wake a sleeper, and Sleeping still cancelled speech
  itself, which no mask can forgive. Declaring `Unconscious` fixes it, and removes Sleeping's
  copy of the force-say token check.

Decided:
- **The cache stays (approach A).** Checking live on every query was considered. It is safer
  when a producer forgets to refresh, because only reactions (movement, the UI sweep) would lag.
  But it re-polls every producer on every check. `CanPerform` hides the cache, so this can be
  revisited without changing the API.
- **Capabilities get a baseline, plus grants and revokes** (§15.4). The baseline is an
  immutable profile prototype. Every change at runtime comes from a source that declares it,
  so nothing stores a history and every capability has a visible cause.
  `ComplexInteractionComponent` becomes an obsolete grant source, then goes. Designed, not
  implemented.
- **The admin freeze's mute is not `Silenced`.** It must stay unforgivable, so it becomes a pair
  rule (§16.3).
- **The target events keep their names** for now. "Mob" in `MobActionTargetAttemptEvent` refers
  to the user, not the target, which can be any entity.

## Appendix D: third review (2026-10-22)

The third adversarial review of the code. Fixed:
- **The mech relay** keeps relaying the obsolete `GettingAttackedAttemptEvent`, so downstream
  subscribers of the relayed form aren't silently dropped (§16.2).
- **Producers no longer add `MobConditionsComponent` or subscribe to `MapInit` or
  `ComponentRemove` for it** (§4.4). `Refresh` adds the component lazily. That removes the
  `EnsureConditions` API, and the fork-collision risk of new lifecycle subscriptions on
  `MobStateComponent` and `CuffableComponent`.

Decided:
- **`CanShiver` and `CanSweat`** stay deleted rather than deprecated: nothing ever cancelled
  them.
- **Code that raises a typed attempt event itself**, instead of asking the bookkeeper, no longer
  gets condition blocking from MobState, stun or cuffs. That is accepted: the events are for
  the bookkeeper to raise.

Open:
- **The pickup popup** (Q14), with a proposed fix.
- **The generic events concentrate subscription collisions:** one system per component may
  subscribe to `MobActionTargetAttemptEvent` or `MobActionAttemptEvent`. Decide before bucket 3
  adds more handlers.
- **Smaller items:** the namespace and the `InteractionRequirements` name, drag-and-drop's
  stricter check, reason overwriting, test gaps, and the items carried over from Appendix B.

## Appendix C: end-state revision (2026-10-21)

The goal changed from "centralise the attempt events" to "replace `ActionBlockerSystem` with one
bookkeeper".

**New sections:**
- **§15:** the end state, the consumer API, the two remaining events, movement ownership,
  creature properties, the migration steps and the vocabulary changes.
- **§16:** every current attempt-event subscriber classified into four buckets, plus one
  non-gate.

**Changes to existing sections:**
- **§1:** the compatibility rule became a temporary migration rule.
- **§2.7, §5.1, §5.2 and §5.4:** marked as superseded or migration-only.
- **§6.4:** "out of scope" became "classified in §16".
- **Q2:** answered with `Pinned`.
- **Q10–Q13:** added.

**Findings from the second review that this resolves in the design:**
- the routing of conditions through the legacy events, and the `Ignoring` field;
- the two movement caches;
- the circular dependency between `ActionBlockerSystem` and `MobCapabilitiesSystem`;
- the vehicle re-entrancy, which becomes a cross-entity producer.

## Appendix B: review changelog (2026-10-20)

This is an adversarial review against `6a9a8abe96`. The full list of attempt-event subscribers
(30 systems), the BUI gate and click paths, and the action bar were checked against the code.

**Model:**
- **Networked state.** The component networks only the pre-cascade `Declared`. The derived
  fields are recomputed on state application (§2.3).
- **The cold path no longer re-polls producers** (§4.3).
  - It was redundant: re-polling yields the union the last refresh already stored.
  - On the client it was wrong: only client-visible sources can be polled there.
- **Ignore masks are applied before *and* after the cascade** (§4.3). Before, ignoring a derived
  bit such as `HandsBlocked` did nothing.
- **`GetBlockingConditions` is now "declared bits that block on their own"** (§10.2). The old
  "minimal removal set" was empty whenever there was more than one cause.
- **Refresh compares `Declared`, not `Conditions`** (§4.2).
- **The re-entrancy guard is a set**, not a single field (§4.2 R2).
- **New consistency check** for missed refreshes (§4.5).

**Producers:**
- **New producer rules** (§6.1):
  - refresh before any downstream event;
  - writes only through refreshing setters;
  - shared and networked only.
- **New `EnsureComp` rules**: not during `ApplyingState`, not on terminating entities (§4.4).
- **Sleeping hooks are `ComponentInit` and `ComponentRemove`**, and the refresh must run before
  `SleepStateChangedEvent` (§6.2).
- **Cuffs refresh anchor** is `:183` (§6.2).
- **Parity verified** for every slice-1 producer (§3.2). One delta is now stated:
  `Unconscious` blocks `StandUp` for crit and dead mobs.
- **§6.3 gained a delta column**, because none of those rows is parity.
- **Missing out-of-scope sources added** (§6.4): cryo pod, muted status effect, DNA imprint.
- **StationAi un-cancels `InteractionAttemptEvent`** (`SharedStationAiSystem.Held.cs:151`). The
  Core PR fixes it, and the cancel-only rule is now explicit (§5.1).

**Action bar:**
- **`ActionGhostBoo` parity is `None`**, not `Conscious`, because it inherits
  `checkConsciousness: false` (§7.2).

**UI:**
- **`GetUiStatus` takes the BUI key** (§8.1). Without it, `RequiresComplex` would apply to every
  key on the entity.
- **Spectator rule keys on `!CanGhostInteract`**, not admin status (§8.1).
- **`Disabled` now has a meaning** (§2.6): accessible in principle, but blocked by conditions.
- **The sweep closes only on `Unconscious`** and skips `requireInputValidation: false` BUIs.
  The `MobStateChangedEvent` backstop is dropped (§8.3).
- **Read-only opens carry `UiStatus`** (§2.6).
- **Spectators don't take `SingleUser` locks** (§8.2).
- **New required side-effect audit** (§8.5).
- **The `InteractionActivate` carve-out calls `TryOpenReadOnly`** instead of letting
  `ActivateInWorldEvent` through (§8.2).
- **Paper data covers all four `PaperUiKey` blocks**, which are not inherited from one parent.
  Paper's global `Mode` reset must skip read-only opens (§8.4).

**Other:**
- **Locale uses per-member names** (§10.1).
- **New open questions** Q7–Q9 (§11).
- **Core lands with the MobState producer** (§12).
- **Tests rebased on `InteractionTest`.** 11 tests added, and `UiClosesOnStun` replaced (§13).
