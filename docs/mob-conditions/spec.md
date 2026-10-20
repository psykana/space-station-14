# MobCondition: implementation spec

This is the normative companion to [design.md](design.md). The design explains *why*; this
document says *what to build*, precisely enough to implement without re-researching.

Citations are against upstream `6a9a8abe96` (2026-10-20). "MUST", "SHOULD" and "MAY" are
used in the RFC 2119 sense.

## 1. Scope

**Covered:**
- One closed vocabulary of mob conditions and one of mob actions.
- A cached per-mob component.
- A single system that derives blocked actions from conditions and cancels the existing
  attempt events.
- A consumer-side requirements struct with ignore masks, used by interactions, UIs and the
  action bar.
- UI access tiers.

**Not covered:**
- Creature properties (`ComplexInteractionComponent`, `BlockWritingComponent`, literacy).
  They stay components; this system consumes them.
- Situational gates (§6.4).
- Perception and chat filtering (design §8).

**Compatibility rule:**
- Every existing `ActionBlockerSystem.CanX()` keeps its signature and semantics.
- Every existing attempt event keeps being raised. Unmigrated subscribers keep working
  unchanged.

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
    UiBlocked     = 1 << 6,  // cannot operate user interfaces
    PullBlocked   = 1 << 7,  // cannot pull
    Incorporeal   = 1 << 8,  // no physical presence (spectating ghost)
}
```

**Rules:**
- A member MUST NOT be added unless something sets it.
- Stasis and Grabbed are deliberately absent (design §3).

### 2.2 `MobAction` — `MobCondition.cs`

Each member names the attempt event that `MobConditionSystem` cancels for it.

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
| `UiAccess` | 14 | none; consumed by `GetUiStatus` (§8) |
| `StandUp` | 15 | `StandUpAttemptEvent` (`Content.Shared.Stunnable`, by-ref) |

`enum MobAction : uint`, `[Flags, Serializable, NetSerializable]`, `None = 0`.

### 2.3 `MobConditionsComponent` — `MobConditionsComponent.cs`

```csharp
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(MobConditionSystem))]
public sealed partial class MobConditionsComponent : Component
{
    [ViewVariables, AutoNetworkedField] public MobCondition Conditions; // declared + cascaded
    [ViewVariables, AutoNetworkedField] public MobAction Blocked;       // GetBlocked(Conditions)
}
```

- It MUST hold nothing else. It sits on every mob (see the layout conventions of
  `MetaDataComponent` and `TransformComponent`).
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
allocation per refresh and per ignore-mask check (§4.3). It also allows
`Entity<T>, ref` handlers in the `[SubscribeLocalEvent]` attribute style. The precedent is
`RefreshFrictionModifiersEvent`.

### 2.5 `InteractionRequirements` — `InteractionRequirements.cs`

```csharp
[DataDefinition, Serializable, NetSerializable]
public partial record struct InteractionRequirements()
{
    [DataField] public MobAction Action = MobAction.Interact;     // what is being attempted
    [DataField] public MobCondition Ignoring = MobCondition.None; // conditions tolerated
    [DataField] public bool NeedComplex = false;                  // requires ComplexInteractionComponent
}
```

It is named to avoid confusion with `ActionRequirementsComponent`/`ActionRequirementsSystem`
(`Content.Shared.Actions`, #46029), which is a different feature (design §6). The prototype
called it `ActionRequirements`; that name MUST NOT be used.

### 2.6 UI types

- **`UiStatus.cs`:**
  `public enum UiStatus : sbyte { Close = -1, Disabled = 0, ReadOnly = 1, Interactive = 2 }`.
  - It is ordered, so `>=` comparisons are meaningful.
  - `Disabled` is reserved and unused.
- **`ActivatableUIComponent.cs`** gains:
  ```csharp
  /// Conditions under which this UI stays open read-only instead of closing or refusing to open.
  [DataField, AutoNetworkedField]
  public MobCondition ReadOnlyConditions;
  ```

### 2.7 Change to an existing event

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

### 3.1 Cascade — `MobConditionSystem.Expand(MobCondition) → MobCondition`

The cascade is applied repeatedly until nothing changes, so the order of rules doesn't matter.

| If present | Add |
|---|---|
| `Unconscious` | `Incapacitated \| Immobilized \| Floored \| HandsBlocked` |
| `Restrained` | `HandsBlocked` |
| `Incapacitated` or `HandsBlocked` | `UiBlocked \| PullBlocked` |

### 3.2 Policy — `MobConditionSystem.GetBlocked(MobCondition) → MobAction`

| Condition | Blocks |
|---|---|
| `Unconscious` | `Conscious \| Speak \| Emote \| Point \| StandUp` |
| `Incapacitated` | `Interact \| UseHeld \| Pickup \| Drop \| Attack \| Throw \| ChangeDirection` |
| `HandsBlocked` | `Interact \| UseHeld \| Pickup \| Drop \| Attack` |
| `Incorporeal` | `Interact \| UseHeld \| Pickup \| Drop \| Emote` |
| `Immobilized` | `Move` |
| `Floored` | `Stand` |
| `UiBlocked` | `UiAccess` |
| `PullBlocked` | `Pull` |
| `Restrained` | nothing directly |

**Invariants these tables MUST preserve** (each one has a test in §13):
- `StandUp` is blocked only by `Unconscious`. Knockdown raises `StandUpAttemptEvent` itself
  (`SharedStunSystem.Knockdown.cs:346`) to decide recovery, so `Floored` blocking it would
  pin knocked-down mobs forever.
- `Point` is not blocked by `Incapacitated`. A stunned but awake mob can gesture, which is
  today's behaviour.
- Today's semantics are preserved exactly:
  - Stun maps to `Incapacitated | Immobilized`.
  - Crit, dead and asleep map to `Unconscious`.
  - Cuffs map to `Restrained`.

## 4. `MobConditionSystem` — `MobConditionSystem.cs`

`public sealed partial class MobConditionSystem : EntitySystem`. Its dependencies are
`IGameTiming` and `ActionBlockerSystem`, and it caches an `EntityQuery<MobConditionsComponent>`.

### 4.1 Public API

```csharp
public void Refresh(Entity<MobConditionsComponent?> ent);
public bool HasCondition(Entity<MobConditionsComponent?> ent, MobCondition condition);     // any bit
public bool IsBlocked(Entity<MobConditionsComponent?> ent, MobAction action);              // hot path
public bool IsBlocked(EntityUid uid, MobAction action, MobCondition ignoring);            // cold path
public MobCondition GetBlockingConditions(EntityUid uid, MobAction action);               // for popups (§10.2)
```

An entity without the component is never blocked by this system: `IsBlocked` returns false
and `HasCondition` returns false.

### 4.2 `Refresh` contract

Steps, in order:

1. Resolve the component without logging. If it is absent, return.
2. If `IGameTiming.ApplyingState`, return. Networked fields carry the state to the client.
3. `conditions = Expand(GetDeclared(uid))`. `GetDeclared` raises `RefreshMobConditionsEvent`
   by ref on the mob.
4. If `conditions == comp.Conditions`, return.
5. **Commit:**
   - `old = comp.Conditions`, `oldBlocked = comp.Blocked`.
   - `comp.Conditions = conditions`, `comp.Blocked = GetBlocked(conditions)`.
   - `Dirty`.
6. If `(oldBlocked ^ comp.Blocked) & Move` is non-zero, call `ActionBlockerSystem.UpdateCanMove(uid)`.
   `InputMoverComponent.CanMove` is a separate cache.
7. Raise `MobConditionsChangedEvent(old, conditions)` by ref on the mob.

**Invariant R1:** step 5 MUST happen before steps 6 and 7.
- `VehicleSystem.OnOperatorUpdateCanMove` (`Vehicle/Systems/VehicleSystem.cs:82`) calls
  `CanOperate` (`:315`), which calls `CanInteract`. That happens inside step 6, so it must
  read the new cache.
- `MobConditionsChangedEvent` subscribers likewise query `GetUiStatus` against the new
  state.

**Invariant R2:** `Refresh` MUST NOT be re-entered for the same entity.
- A `RefreshMobConditionsEvent` handler MUST NOT call `Refresh` or any `CanX`; it only calls
  `Add`.
- Guard with a debug assert on a per-system "refreshing" `EntityUid`.

### 4.3 The ignore-mask cold path

`IsBlocked(uid, action, ignoring)`:
- If `ignoring == None`, it is the hot path.
- Otherwise it computes `GetBlocked(Expand(GetDeclared(uid) & ~ignoring)) & action`. This
  re-polls the sources, which is allocation-free since §2.4.

Callers SHOULD reach it only after the hot path has failed (§5.2 does this).

Forgiving a condition never forgives conditions that another source derives independently.
For example, an unconscious, cuffed mob ignoring `Restrained` is still `HandsBlocked`.

### 4.4 Component presence

- Every producer MUST `EnsureComp<MobConditionsComponent>` on its own `ComponentStartup` (or
  `MapInit`) and call `Refresh`.
- `MobConditionsComponent`'s own `ComponentStartup` calls `Refresh`.
- `- type: MobConditions` is also added to `MobDamageable` in
  `Resources/Prototypes/Entities/Mobs/base.yml`, next to `MobState`, so that ordinary mobs
  have it from spawn.

## 5. Attempt-event handling

### 5.1 Central subscriptions

`MobConditionSystem` subscribes on `MobConditionsComponent` to every event in §2.2. Each
handler cancels when `(comp.Blocked & action) != 0`.

- New code SHOULD use `[SubscribeLocalEvent]` attributes.
- An event MUST NOT be subscribed both ways for the same component.

Exceptions:
- `InteractionAttemptEvent`: uses `IsBlocked(uid, Interact, args.Ignoring)`, so the requested
  mask is honoured.
- `SpeakAttemptEvent`: see §5.4.

### 5.2 `ActionBlockerSystem` overload — `ActionBlockerSystem.Conditions.cs`

```csharp
public bool CanInteract(EntityUid user, EntityUid? target, in InteractionRequirements req)
```

**Steps:**
1. If `IsBlocked(user, req.Action, req.Ignoring)`, return false. This is the fast path when
   `Ignoring == None`.
2. If `req.NeedComplex && !CanComplexInteract(user)`, return false.
3. Raise the **legacy event for the action**, so that unmigrated sources still get a say:

   | `req.Action` | Legacy check |
   |---|---|
   | `Interact`, `UiAccess` | the existing `CanInteract(user, target)` body: `ConsciousAttemptEvent`, then `InteractionAttemptEvent(user, target, req.Ignoring)`, then `GettingInteractedWithAttemptEvent` on the target |
   | `Conscious` | `ConsciousAttemptEvent` |
   | `None` | nothing |
   | any other | MUST NOT be used with this overload; use the specific `CanX` |

The existing `CanInteract(user, target)` (`ActionBlockerSystem.cs:73`) is unchanged, and is
equivalent to `CanInteract(user, target, default)`.

### 5.3 `Stand` vs `StandUp`

- `StandAttemptEvent` is cancelled by `Stand`, which comes from `Floored`.
- `StandUpAttemptEvent` is cancelled by `StandUp`, which comes from `Unconscious` only.
- Knockdown's own `StandUpAttemptEvent` raise (`SharedStunSystem.Knockdown.cs:346`) and the
  `KnockdownStatusEffect` relay handler (`SharedStunSystem.cs:69/371`) are untouched in slice 1
  (see §11 Q2).

### 5.4 Speak, last words and ordering

`OnSpeakAttempt`:
1. If `Speak` is not blocked, return.
2. If the mob has `AllowNextCritSpeechComponent` (`Content.Shared.Damage.ForceSay`):
   `RemCompDeferred` it and return. This grants one free line.
3. Otherwise, cancel.

This replaces the two identical copies at `MobStateSystem.Subscribers.cs:166` and
`SleepingSystem.cs:166`. `AllowNextCritSpeechComponent` stays as it is: it is a stateful
grant with a timeout, not something an ignore mask can express.

**Ordering, required:**
- `StatusEffectSystem.Relay.cs:60` currently subscribes the status-effect Speak relay with
  `after: [typeof(MobStateSystem)]` (#45977). That ensures `MutedStatusEffectSystem` sees the
  event already cancelled for a dead mob and doesn't popup-spam.
- The slice that moves Speak blocking MUST change this to
  `after: [typeof(MobStateSystem), typeof(MobConditionSystem)]`.
- Once MobState no longer subscribes to Speak, it MUST be just
  `after: [typeof(MobConditionSystem)]`.
- A test MUST cover "dead mime types in chat: no muted popup" (§13).

## 6. Producers

### 6.1 Rules for every producer

1. Subscribe to `RefreshMobConditionsEvent` on the producer's component, and `Add` the
   condition while it applies. Do nothing else in the handler (R2).
2. Guard with `ent.Comp.LifeStage <= ComponentLifeStage.Running`. The component is still
   subscribed during removal, and the removal refresh must not re-declare.
3. `EnsureComp<MobConditionsComponent>` and call `Refresh` on startup and on removal
   (`ComponentShutdown`), and whenever the producer's own state flips (for example
   `CanStillInteract`, `CanGhostInteract`, a mob-state change).
4. Delete every attempt-event handler the condition now covers.
   - A producer MUST NOT both declare a condition and cancel the same action's event. That
     makes the condition unforgivable.
   - Conditional handlers that aren't plain blocks stay (design §4).
5. One producer per commit.

### 6.2 Slice 1: the original seven

| Producer | Condition (when) | Delete (attempt handlers) | Refresh on | Keep |
|---|---|---|---|---|
| **MobState**: `Mobs/Systems/MobStateSystem.Subscribers.cs` | `Unconscious` (Critical, Dead) | `CheckAct` subscriptions `:33–43` (11 events); `CheckConcious` `:63` (Conscious); `OnSpeakAttempt` `:166` (§5.4) | state entered (`:103` — replace `_blocker.UpdateCanMove` with `_conditions.Refresh`; `_blocker` then leaves `MobStateSystem.cs`) | `Down()` `:55`, equip/unequip/unbuckle self-checks, `OnIncapCuffCheck` `:226` (until §11 Q3) |
| **Stun**: `Stunnable/SharedStunSystem.cs` | `Incapacitated \| Immobilized` (`StunnedComponent` present) | `:51–58` (8 events): `OnAttempt` `:391`, `OnMoveAttempt` `:383`, `OnAttemptInteract` `:76` | `StunnedComponent` startup/shutdown; the existing `UpdateCanMove` calls become `Refresh` | equip/unequip `:59–60`, the KnockdownStatusEffect relay `:69` |
| **Knockdown**: `Stunnable/SharedStunSystem.Knockdown.cs` | `Floored` (`KnockedDownComponent`) | `OnStandAttempt` `:575` (sub `:54`) | knockdown init; shutdown *before* standing | its own `StandUpAttemptEvent` raise `:346` |
| **Sleeping**: `Bed/Sleep/SleepingSystem.cs` | `Unconscious` (`SleepingComponent`) | `:66` Speak, `:68` Point, `:70` Conscious, `:75` StandUp, `:80` Emote (handlers `:166/183/193/203/368`) | startup, shutdown | `OnSeeAttempt`, `OnUnbuckleAttempt`, `StunEndAttemptEvent`, `OnIncapCuffCheck` `:210` |
| **Cuffs**: `Cuffs/SharedCuffableSystem.cs` | `Restrained` (`!CanStillInteract`) | `:83–86` `CheckAct`, `:87` `CheckInteract` `:95` | `UpdateCuffState` | `HandleMoveAttempt` `:73/238` (only while pulled), the `CheckAct` method (still used by equip handlers); self-uncuff → §9.1 |
| **Ghost**: `Ghost/Systems/SharedGhostSystem.cs` (namespace `Content.Shared.Ghost.Systems`) | `Incorporeal` (`!CanGhostInteract`) | `:32–36`: `OnAttempt` `:56`, `OnAttemptInteract` `:50` | `SetCanGhostInteract`; server `GhostSystem.OnGhostStartup` calls a protected `EnsureGhostConditions` | everything else. MUST NOT add a `GhostComponent, ComponentStartup` subscription in shared: client and server already subscribe it, and the duplicate crashes at init. |
| **LegsParalyzed**: `Traits/Assorted/LegsParalyzedSystem.cs` | `Immobilized` | `:21/45` | startup, shutdown | buckle and throw handlers |

Sleeping also does `EnsureComp<StunnedComponent>` and `EnsureComp<KnockedDownComponent>`
(`SleepingSystem.cs:123–124`), so a sleeper is `Unconscious | Incapacitated | Immobilized |
Floored` from both paths. Both paths agree, which is fine.

### 6.3 Later slices: pure-cancel sources found since July

| Source | File | Events cancelled | Proposed condition |
|---|---|---|---|
| AdminFrozen | `Administration/AdminFrozenSystem.cs:23–34` | Use, Pickup, Throw, Interaction, Attack, ChangeDirection, UpdateCanMove; Speak/Emote if `Muted` | `Incapacitated \| Immobilized`; Speak/Emote stay events (Muted is an admin option, not a faculty) |
| VentriloquistPuppet | `Puppet/SharedVentriloquistPuppetSystem.cs:18–24` | Use, Interaction, Drop, Pickup, UpdateCanMove, Emote, ChangeDirection | `Incapacitated \| Immobilized`, with Emote kept as an event |
| KitchenSpike | `Kitchen/SharedKitchenSpikeSystem.cs:75–80` | ChangeDirection, Use, Throw, Drop, Attack, Pickup | `Incapacitated` |
| BeingDisposed | `Disposal/Unit/SharedBeingDisposedSystem.cs:27–28` | Interaction, Attack | `HandsBlocked` |
| Brain / BorgBrain | `Body/Systems/BrainSystem.cs:20`, `Silicons/Borgs/SharedBorgSystem.cs:101` | Point | leave as events (creature property: no body to point with) |
| Worm | `Movement/Systems/WormSystem.cs:19` | StandUp (+ message, autostand) | leave (sets extra fields) |
| Chasm, TetherGun | `Chasm/ChasmSystem.cs:107`, `Weapons/Misc/SharedTetherGunSystem.cs:51` | UpdateCanMove | `Immobilized` |

Each row SHOULD be its own commit, with that system's existing tests.

### 6.4 Out of scope: situational gates

These depend on the target, the context or extra state rather than the mob's faculties, so
they stay attempt-event subscribers:

- Buckle (`Buckle/SharedBuckleSystem.Buckle.cs`). It pins posture both ways (design §8).
- Pacification (`CombatMode/Pacification/PacificationSystem.cs`).
- Climb.
- Vehicle (`Vehicle/Systems/VehicleSystem.cs`).
- ShuttleConsole.
- StationAi whitelist.
- `SharedInteractionSystem.Blocking.cs`.
- `SharedMoverController`.
- Guardian.
- The Speech and Emote capability checks (`Speech/SpeechSystem.cs`, `Emoting/EmoteSystem.cs`).
  These are creature properties.
- The client `ReplaySpectatorSystem`.

## 7. Action bar

### 7.1 `ActionComponent.Requires`

`Content.Shared/Actions/Components/ActionComponent.cs` gains:

```csharp
/// What the performer must be capable of. Replaces CheckCanInteract/CheckConsciousness.
[DataField, AutoNetworkedField]
public InteractionRequirements Requires = new() { Action = MobAction.Interact };
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

The 11 prototypes to migrate:

| Prototype | File | Today | Parity |
|---|---|---|---|
| `BaseMentalAction` (and every child) | `Actions/types.yml` | both false | `None` |
| `ActionScream` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionActivateFreedomImplant` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionActivateEmpImplant` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionActivateScramImplant` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionToggleWagging` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionChangelingBiodegrade` | `Actions/changeling.yml` | interact false | `Conscious` |
| `ActionChangelingStasis` | `Actions/changeling.yml` | both false | `None` |
| `ActionChangelingLastResort` | `Actions/changeling.yml` | both false | `None` |
| `ActionStethoscope` | `Entities/Clothing/Neck/misc.yml` | interact false | `Conscious` |
| `ActionGhostBoo` | `Entities/Mobs/Player/observer.yml` | interact false | `Conscious` |

Re-grep `checkCanInteract:|checkConsciousness:` before migrating; new prototypes may have
landed.

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

### 8.1 `ActivatableUISystem.GetUiStatus` — `ActivatableUISystem.Status.cs`

```csharp
public UiStatus GetUiStatus(EntityUid user, Entity<ActivatableUIComponent?> target)
```

The resolution order (first match wins):

1. **The user has `GhostComponent`** (`Content.Shared.Ghost.Components`):
   - Admin → `Interactive`.
   - Otherwise `comp?.BlockSpectators == true` → `Close`.
   - Otherwise `ReadOnly`.
2. **`comp?.AdminOnly == true` and the user is not an admin** → `Close`.
3. **`req = { Action = UiAccess, NeedComplex = comp?.RequiresComplex ?? false }`.** If
   `CanInteract(user, target, req)` passes → `Interactive`.
4. **If `comp?.ReadOnlyConditions` is not `None`**, set `req.Ignoring` to it. If
   `CanInteract(user, target, req)` passes → `ReadOnly`.
5. **Otherwise** → `Close`.

### 8.2 Enforcement edits

**`Content.Shared/Interaction/SharedInteractionSystem.cs`, `OnBoundInterfaceInteractAttempt` (`:150`):**
- Add an `ActivatableUISystem _activatableUI` dependency.
- At the top: `if (ev.Message is CloseBoundInterfaceMessage) return;` Closing must always
  succeed.
- Replace `:154–164` (the `CanInteract` / ghost special case) with:
  - `status = _activatableUI.GetUiStatus(ev.Actor, (ev.Target, aUiComp))`.
  - `Open` requires `status >= ReadOnly`.
  - Every other message requires `status == Interactive`.
  - Otherwise `ev.Cancel()`.
- **Keep** the range cancel (`:168`, #46045), the accessibility check, the key match and the
  single-user check (`:187`).
- Delete the `RequiresComplex` check (`:193`). `GetUiStatus` now covers it.

**`InteractionActivate` (`:1148`; the check is at `:1165`):**
- When `CanInteract(user, used)` fails, still let activation through if `used` has an
  `ActivatableUIComponent` with non-`None` `ReadOnlyConditions` and `GetUiStatus >= ReadOnly`.
  The UI then opens read-only, and input stays rejected by the gate above.

**`Content.Shared/UserInterface/ActivatableUISystem.cs`:**
- `ShouldAddVerb` (`:97`):
  - Replace the `args.CanInteract || HasComp<GhostComponent> && !BlockSpectators`
    expression (`:120–123`) with `GetUiStatus(args.User, (uid, component)) >= ReadOnly`.
  - Do the hand checks only when the status is `Interactive`.
  - Keep `RaiseCanOpenEventChecks(..., silent: true)`.
- `InteractUI` (`:194`):
  - Replace `:205` (CanInteract/ghost) and `:208–212` (`RequiresComplex`) with
    `if (GetUiStatus(user, (uiEntity, aui)) < UiStatus.ReadOnly) return false;`.
  - The `InHandsOnly` checks apply only when the status is `Interactive`.
- `OnBoundInterfaceOpenAttempt` (`:184`, #46045): **unchanged**.

Every enforcement hook only cancels, so no ordering between `SharedInteractionSystem` and
`ActivatableUISystem` is required.

### 8.3 `ActivatableUIStatusSystem` — `ActivatableUIStatusSystem.cs`

`public sealed partial class`. It subscribes `MobConditionsComponent, MobConditionsChangedEvent`.
When `args.Added != None`, for each UI in `_uiSystem.GetActorUis(actor)` (collected into a
`ValueList` first), if `GetUiStatus(actor, bui.Entity) < ReadOnly`, it calls
`CloseUi(bui.Entity, bui.Key, actor)`.

- It also subscribes broadcast `MobStateChangedEvent` (non-Alive → same sweep). This is a
  backstop for UIs on entities without the component.
- Use `SubscribeLocalEvent<MobStateChangedEvent>` (broadcast). A directed
  `MobStateComponent, MobStateChangedEvent` subscription already exists, and a duplicate
  crashes at init.

### 8.4 Data

`Resources/Prototypes/Entities/Objects/Misc/paper.yml` sets `readOnlyConditions: Restrained` on
both `ActivatableUI` blocks (`key: enum.PaperUiKey.Key`). Books and other entities that reuse
`PaperUiKey` inherit it through their parents. Check this with a grep for
`PaperUiKey` when implementing.

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
    [Dependency] private MobConditionSystem _conditions = default!;
    protected override void Condition(Entity<MobConditionsComponent> ent, ref EntityConditionEvent<HasMobConditionCondition> args)
        => args.Result = _conditions.HasCondition((ent, ent.Comp), args.Condition.Condition);
}

public sealed partial class HasMobConditionCondition : EntityConditionBase<HasMobConditionCondition>
{
    [DataField(required: true)] public MobCondition Condition;
    public override string EntityConditionGuidebookText(IPrototypeManager prototype) =>
        Loc.GetString("entity-condition-guidebook-has-mob-condition", ("condition", Condition));
}
```

- Add the locale key to
  `Resources/Locale/en-US/guidebook/entity-effects/conditions.ftl`.
- It is usable from `ActionRequirementsComponent.Conditions`, entity effects and objectives.
- It is additive: `MobStateCondition` stays.

### 10.2 Failure reasons (follow-up)

`GetBlockingConditions(uid, action)` returns the minimal set of *declared* (pre-cascade)
conditions whose removal would unblock the action. Compute it by testing each declared bit
with the §4.3 cold path, at most 9 iterations.

`SharedActionsSystem` MAY put a localized "You can't do that while {condition}" into
`ActionAttemptEvent.Reason`. That addresses the TODO on `ActionRequirementsComponent.FailPopup`
for the condition-based case. Locale keys: `mob-condition-blocked-<condition>`.

## 11. Open questions

| # | Question | Recommendation |
|---|---|---|
| Q1 | Should `RefreshMobConditionsEvent` be relayed through status effects, so a status effect entity can declare a condition directly? | **Yes**, in a later slice. The precedent is five existing `Refresh*Event` relays in `StatusEffectNew/StatusEffectSystem.Relay.cs:41–72` (`RefRelayStatusEffectEvent`). It needs refresh calls on status-effect apply and remove. |
| Q2 | How does the `KnockdownStatusEffect` `StandUp` pin (`SharedStunSystem.cs:69/371`) map to conditions? | Keep it as a relay handler for now. `Floored` blocks `Stand`, not `StandUp`, and changing that breaks recovery (§5.3). Revisit together with collapsing the two standing events. |
| Q3 | Should `CheckIncapacitatedCuffEvent` (`SharedCuffableSystem.cs:525/902`) be replaced with `HasCondition(target, Unconscious)`? | The Sleeping and MobState answers map to `Unconscious`. The Stamina-crit answer (`SharedStaminaSystem.cs:147`) has no condition: stamcrit applies stun, but #45296 deliberately excluded plain stun. Either add stamcrit as an explicit source or leave the event. Ask maintainers. |
| Q4 | Can cuffed mobs struggle out of containers (§9.3)? | Balance call. Raise it with maintainers. |
| Q5 | Do the action precision tweaks (§7.4) ship with the parity PR? | No. Parity first, then a separate balance PR. |
| Q6 | Should hand-drop and stop-pull side effects move to `HandsBlocked`/`PullBlocked` (design §8)? | Later, as a separate gameplay PR. It interacts with virtual items from cuffs and vehicles. |

## 12. PR slicing for upstream

1. **Core.** Add the types (§2), `MobConditionSystem` (§4–5), the `ActionBlockerSystem` overload,
   the `InteractionAttemptEvent.Ignoring` field, the component on `MobDamageable`, and unit and
   integration tests for the tables. Nothing produces conditions yet, so behaviour is unchanged.
2. **Producers, one commit each, in §6.2 order:**
   - MobState, together with the Speak and relay-ordering change from §5.4.
   - Sleeping.
   - Stun.
   - Knockdown.
   - LegsParalyzed.
   - Cuffs, together with §9.1.
   - Ghost.

   Every commit deletes its handlers.
3. **UI tiers.** §8 plus paper. This fixes cuffed reading and observer access.
4. **Action bar at parity.** §7.1–7.3.
5. **Follow-ups, each separate:**
   - §6.3 producers.
   - §10.1 bridge.
   - §10.2 reasons.
   - §11 Q1 relay.
   - §7.4 and §9.3 balance changes.
   - Perception (design §8).

Each PR's description links design.md.

## 13. Tests

New tests go in `Content.IntegrationTests/Tests/ActionBlocker/MobConditionTest.cs`, using the
`GameTest`/`Pair` fixtures with `[TestPrototypes]`.

| Test | Asserts |
|---|---|
| `CascadeFixedPoint` | `Expand(Unconscious)` contains `HandsBlocked \| UiBlocked \| PullBlocked`; `Expand(Restrained)` contains `UiBlocked`. |
| `RestraintIsForgivable` | Cuffed: `CanInteract` false; with `Ignoring = Restrained`, true. Cuffed and asleep: still false with the ignore. |
| `WakeWhileCuffed` | Cuff, sleep, wake: `Restrained` persists and interaction stays blocked. |
| `FlooredStillAllowsStandingUp` | Knocked down: `Stand` blocked, `StandUp` not blocked, and the mob recovers after the knockdown time. |
| `UnconsciousBlocksEverything` | Crit: all 16 §2.2 actions are blocked (through the cascade); `AllowNextCritSpeech` grants exactly one line. |
| `DeadMimeNoPopup` | Dead mob with `StatusEffectMimeMuted`: a speak attempt is cancelled and no muted popup is raised (§5.4). |
| `GhostReadOnly` | A non-admin ghost opens a `RequiresComplex` machine UI as `ReadOnly`; an input message is rejected server-side; `BlockSpectators` → `Close`. |
| `CuffedPaperReadOnly` | A cuffed user activates paper and the UI opens; a write message is rejected; after uncuffing, the write succeeds. |
| `UiClosesOnStun` | An open machine UI closes when the user is stunned; open paper stays open when the user is cuffed. |
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
- **Not in the prototype at all:**
  - The Speak relay ordering (§5.4).
  - The action-bar integration (§7).
  - The EntityCondition bridge (§10).
  - The #46045 range cancel and open veto, which must be kept (§8.2).
