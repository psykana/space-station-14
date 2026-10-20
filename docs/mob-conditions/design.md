# Centralized mob action-blocking

This doc covers how SS14 decides what a mob can do, why that needs to change, and the model
proposed to replace it. The model is adapted from tgstation's condition/trait architecture
rather than ported from it.

The normative details (types, tables, migration steps, tests) are in [spec.md](spec.md).
This document is the rationale. All code citations are against upstream `6a9a8abe96`
(2026-10-20).

## 1. The problem

`ActionBlockerSystem` is a stateless dispatcher. Each `CanX()` raises a cancellable attempt
event, and every system that might block it subscribes individually. Nothing owns the
decision, so the wiring is N sources × M actions of hand-written cancel handlers:

| System | Attempt-event subscriptions | Of which pure cancels |
|---|---|---|
| `Mobs/Systems/MobStateSystem.Subscribers.cs` | **13** | 12. Eleven route to one `CheckAct`, whose body is `switch (CurrentState) { case Dead: case Critical: args.Cancel(); }`. The Speak handler adds the last-words exception. |
| `Stunnable/SharedStunSystem.cs` | **8**, plus a status-effect `StandUpAttemptEvent` relay | 8 |
| `Bed/Sleep/SleepingSystem.cs` | **5** | 4. Speak carries the last-words exception. |
| `Cuffs/SharedCuffableSystem.cs` | **6** | 5 gated on `!CanStillInteract`. The sixth blocks movement only while restrained *and* pulled. |
| `Ghost/Systems/SharedGhostSystem.cs` | **5** | 5 gated on `!CanGhostInteract` |
| `Administration/AdminFrozenSystem.cs` | **9** | 7, plus Speak and Emote gated on `Muted` |
| `Stunnable/SharedStunSystem.Knockdown.cs` | **1** | 1 |
| `Traits/Assorted/LegsParalyzedSystem.cs` | **1** | 1 |

The puppet, kitchen-spike, disposal, brain, borg, worm, chasm and tether-gun systems also
cancel unconditionally (see spec §6.3). Adding an action means editing every blocker, and
adding a blocker means enumerating every action.

The game shows four consequences:

- **Handcuffed players can't read paper.** Cuffs cancel the generic `InteractionAttemptEvent`,
  so "manipulate an object" and "perceive its contents" are indistinguishable.
- **Observers can't see many UIs.** Ghost access is a `HasComp<GhostComponent>` special case
  in two places: `SharedInteractionSystem.cs:158` and `ActivatableUISystem.cs:121/205`.
  `RequiresComplex` defaults to true, so ghosts are rejected from every machine.
- **"Can this mob stand?" has two answers.** `StandAttemptEvent`
  (`Standing/StandingStateSystem.cs:217`) and `StandUpAttemptEvent`
  (`Stunnable/StunnableEvents.cs:55`) have disjoint subscriber sets.
- **Nothing records why something is blocked**, which forces destructive workarounds:
  - `ZombieSystem.Transform.cs:153` does `RemComp<ComplexInteractionComponent>` with no record
    of who removed it.
  - `SharedCuffableSystem.OnUncuffAttempt` (`:123–133`) flips `CanStillInteract`
    true→check→false to sneak past its own blocker.
  - The action bar's `CheckCanInteract` / `CheckConsciousness` bools (§6) are all-or-nothing:
    an ability that must work in handcuffs also works while stunned.

The same gap appears in reverse whenever two systems need to agree on "is this mob
incapacitated". `CheckIncapacitatedCuffEvent` (`SharedCuffableSystem.cs:902`, from #45296) is
a fresh parallel query, answered separately by MobState, Sleeping and Stamina.

## 2. What tgstation does

Verified against tgstation. It has three tiers.

**Producers add condition traits.** Sleep, oxyloss (`carbon_defense.dm:510`), hardcrit
(`carbon.dm:665`) and deathcoma all add `TRAIT_KNOCKEDOUT`, each under its own source. None of
them names an action.

**Conditions cascade.**

```dm
on_restrained_trait_gain:     ADD_TRAIT(src, TRAIT_HANDS_BLOCKED, TRAIT_RESTRAINED)          // init_signals.dm:218
on_handsblocked_start:        add_traits(list(TRAIT_UI_BLOCKED, TRAIT_PULL_BLOCKED), TRAIT_HANDS_BLOCKED)  // living.dm:2632
on_incapacitated_trait_gain:  add_traits(list(TRAIT_UI_BLOCKED, TRAIT_PULL_BLOCKED), TRAIT_INCAPACITATED)  // init_signals.dm:204
```

**Terminal conditions translate** to `mobility_flags` bits; `on_ui_blocked_trait_gain` clears
`MOBILITY_UI`. Side effects hang off the *condition*, not the producer: `on_handsblocked_start`
drops held items and closes storage, and `on_pull_blocked` calls `stop_pulling()`.

**Consumers say what they tolerate.** Interactions use
`INCAPACITATED_IGNORING(mob, flags)`, the mob's incapacitation minus the causes the call site
forgives. Actions carry `check_flags` (`AB_CHECK_HANDS_BLOCKED | IMMOBILE | LYING | CONSCIOUS
| INCAPACITATED | PHASED`, `__DEFINES/actions.dm`), checked against the same traits in
`datums/actions/action.dm:182–214`.

Two properties make it work:

- **Traits are sourced.** `TRAIT_HANDS_BLOCKED` comes from stun (`debuffs.dm:62`), paralysis,
  stasis, stop-drop-and-roll, alien nests, vehicles, cuffs and unconsciousness. If a mob gets
  stunned while cuffed and is then uncuffed, the trait persists because the stun's source
  remains.
- **Both vocabularies are small and closed:** 9 `mobility_flags` and a 4-bit `incapacitated`
  field, unchanged for years. What actually grows is *creature properties*
  (`TRAIT_ADVANCEDTOOLUSER`, `TRAIT_LITERATE`), and that is a different axis.

## 3. The model

The model has three tiers, two closed enums, one component and one system, all in
`Content.Shared/ActionBlocker/`.

```csharp
[Flags] public enum MobCondition : ushort   // the trait layer
{ Unconscious, Incapacitated, Restrained, HandsBlocked, Immobilized,
  Floored, UiBlocked, PullBlocked, Incorporeal }

[Flags] public enum MobAction : uint        // the mobility_flags layer
{ Interact, Conscious, UseHeld, Pickup, Drop, Attack, Throw, Speak, Emote,
  Point, Move, Stand, StandUp, Pull, ChangeDirection, UiAccess }
```

**Cascades** (`MobConditionSystem.Expand`, applied to a fixed point):

| Condition | Implies |
|---|---|
| `Unconscious` | `Incapacitated \| Immobilized \| Floored \| HandsBlocked` |
| `Restrained` | `HandsBlocked` |
| `Incapacitated`, `HandsBlocked` | `UiBlocked \| PullBlocked` |

**Policy** (`MobConditionSystem.GetBlocked`) is the single place where any condition blocks
anything:

| Condition | Blocks |
|---|---|
| `Unconscious` | `Conscious, Speak, Emote, Point, StandUp` |
| `Incapacitated` | `Interact, UseHeld, Pickup, Drop, Attack, Throw, ChangeDirection` |
| `HandsBlocked` | `Interact, UseHeld, Pickup, Drop, Attack` |
| `Incorporeal` | `Interact, UseHeld, Pickup, Drop, Emote` |
| `Immobilized` | `Move` |
| `Floored` | `Stand` |
| `UiBlocked` | `UiAccess` |
| `PullBlocked` | `Pull` |

`Stand` and `StandUp` are deliberately separate actions:
- `Stand` (`StandAttemptEvent`) means "may this entity be upright at all".
- `StandUp` (`StandUpAttemptEvent`) means "may it try to rise".

Knockdown raises `StandUpAttemptEvent` itself (`SharedStunSystem.Knockdown.cs:346`) to decide
whether recovery is allowed. So a condition that merely floors a mob must not block it;
otherwise a knocked-down mob could never get up. Only unconsciousness blocks the attempt.

`Point` hangs off consciousness for the same reason: a stunned but awake mob can still
gesture, which matches what stun does today.

`Restrained` blocks nothing directly. It acts through the cascade, and it exists so that a call
site can forgive that one specific cause.

tg's incapacitated bitfield also carries stasis and aggressive-grab bits. Those are
deliberately absent here, because SS14 has neither mechanic:
- Cryo pods only pin posture.
- Changeling regenerative stasis (#45104) is implemented as `MobState.Dead`, so it already
  arrives as `Unconscious`.
- Pulling has no grab tiers.

A member that nothing ever sets is a member a call site could ignore-mask against and
silently get nothing. Add these together with their mechanics, not before.

**The condition is also the reason.** There is no separate "reason" enum; ignore-masks operate
on the conditions themselves:

```csharp
_actionBlocker.CanInteract(user, target, new InteractionRequirements { Ignoring = MobCondition.Restrained })
```

This recomputes from the declared set minus `Restrained`, which is exactly tg's
`INCAPACITATED_IGNORING`. Forgiving restraint unblocks a handcuffed mob but not an unconscious
one, because unconsciousness re-derives `HandsBlocked` on its own.

**The component** has two flag fields and nothing else, because it sits on every mob:

```csharp
public sealed partial class MobConditionsComponent : Component
{
    [AutoNetworkedField] public MobCondition Conditions;  // declared + cascaded
    [AutoNetworkedField] public MobAction Blocked;        // derived
}
```

**Multiple sources need no refcounts.** `MobConditionSystem.Refresh` raises
`RefreshMobConditionsEvent` and re-polls every source. Nothing is stored between refreshes, so
a source that stops applying simply stops declaring. Waking up while still cuffed unblocks
nothing, because the cuffs re-declare on the same refresh.

This is tg's sourced traits, expressed the way SS14 already handles multi-source state: the
same poll-and-cache shape as `ActionBlockerSystem.UpdateCanMove` and
`RefreshMovementSpeedModifiers`.

## 4. What producers look like

```csharp
// SleepingSystem — replaces 5 attempt handlers
[SubscribeLocalEvent]
private void OnRefreshConditions(Entity<SleepingComponent> ent, ref RefreshMobConditionsEvent args)
{
    if (ent.Comp.LifeStage <= ComponentLifeStage.Running)
        args.Add(MobCondition.Unconscious);
}

// MobStateSystem — replaces 13
if (ent.Comp.CurrentState is MobState.Critical or MobState.Dead)
    args.Add(MobCondition.Unconscious);

// SharedCuffableSystem — replaces 5 (the pulled-movement check stays an event)
if (!ent.Comp.CanStillInteract)
    args.Add(MobCondition.Restrained);

// SharedStunSystem — replaces 8. Stunned mobs can't act or move, but stay awake.
args.Add(MobCondition.Incapacitated | MobCondition.Immobilized);

// SharedStunSystem.Knockdown — replaces 1
args.Add(MobCondition.Floored);

// LegsParalyzedSystem — replaces 1
args.Add(MobCondition.Immobilized);

// SharedGhostSystem — replaces 5
if (!ent.Comp.CanGhostInteract)
    args.Add(MobCondition.Incorporeal);
```

Ghosts are a special case. `GetUiStatus` resolves them *before* consulting conditions, so
`Incorporeal` never affects UI access and a spectator still gets `ReadOnly`. `Incorporeal`
governs everything else, so a ghost still can't pick things up or emote.

The `LifeStage` guard matters. A component is still subscribed while it is being removed, so
without the guard, the refresh that wakes a mob would re-declare unconsciousness.
`SleepingSystem` already uses this idiom in `OnSeeAttempt`.

Because standing consults one cached value, `StandAttemptEvent` and `StandUpAttemptEvent` now
give the same answer without any change to knockdown's logic.

### Handlers that deliberately stay

Conditional logic is not boilerplate, so these handlers stay as they are:

- `SlipAttemptEvent` sets `NoSlip` rather than cancelling.
- `OnSeeAttempt` has its `LifeStage` guard. Unconsciousness deliberately does *not* blind,
  unlike in tg, because SS14 has never done that.
- `OnEquipAttempt`, `OnUnequipAttempt` and `OnUnbuckleAttempt` are self-only. They must not
  block a third party stripping a corpse.
- Cuffs' `HandleMoveAttempt` only blocks movement while the mob is being pulled.

`AllowNextCritSpeechComponent`, the "last words" escape hatch, is currently duplicated
identically in `MobStateSystem` and `SleepingSystem`. It moves to one central place.

It can't be replaced by the ignore mechanism: it is a stateful one-shot grant with a timeout,
not a property of the mob. Moving it has an ordering consequence (spec §5.4). The muted status
effect's Speak relay is ordered `after: [typeof(MobStateSystem)]`
(`StatusEffectSystem.Relay.cs:60`, #45977) so that a dead mime typing doesn't spam "you can't
speak" popups. That ordering must move to `MobConditionSystem` along with Speak.

## 5. UI status tiers

```csharp
public enum UiStatus : sbyte { Close = -1, Disabled = 0, ReadOnly = 1, Interactive = 2 }
```

`ActivatableUISystem.GetUiStatus` resolves access in this order. A missing
`ActivatableUIComponent` just means there is no per-UI policy.

1. **Ghost.** An admin gets `Interactive`. Anyone else gets `ReadOnly`, unless
   `BlockSpectators` is set. Observing is a spectator rule rather than a mob capability, so it
   resolves before any condition is consulted; tg does the same (`states.dm:25`).
2. **`AdminOnly` without admin rights** → `Close`.
3. **`UiAccess` (plus `RequiresComplex`) passes** → `Interactive`.
4. **Retry, forgiving the UI's `ReadOnlyConditions`.** If that passes → `ReadOnly`.
5. **Otherwise** → `Close`.

Enforcement stays where upstream already put it:
- `SharedInteractionSystem.OnBoundInterfaceInteractAttempt` (`:150`) hooks the engine's
  `BoundUserInterfaceMessageAttempt`. The engine raises it for every non-close BUI message.
- The tier gate replaces only the `CanInteract`/ghost special case (`:154–164`) and the
  `RequiresComplex` check (`:193`): `Open` is allowed at `>= ReadOnly`, and every other message
  requires `Interactive`.
- The range cancel (`:168`, made a hard cancel by #46045), the accessibility check and the
  single-user check stay as they are.
- `ActivatableUISystem.OnBoundInterfaceOpenAttempt` (`:184`, also #46045) keeps its
  open-attempt veto. Every hook only cancels, so no ordering between them is needed.

This is tg's rule that "`ui_act` requires `UI_INTERACTIVE`". Read-only is authoritative on the
server, not a client courtesy. `ActivatableUIStatusSystem` closes open UIs when
`MobConditionsChangedEvent` reports that the mob got worse off.

```yaml
- type: ActivatableUI
  key: enum.PaperUiKey.Key
  requiresComplex: false
  readOnlyConditions: Restrained   # cuffed users read; writes are rejected
```

A mouse still can't open a computer: it lacks `ComplexInteractionComponent`, and
`RequiresComplex` remains a hard `Close`. Creature properties stay components; this system
consumes them rather than replacing them.

## 6. Relationship to the action bar and ActionRequirements

Upstream added `ActionRequirementsComponent` / `ActionRequirementsSystem` (#46029,
`Content.Shared/Actions/`). It is a per-action, data-driven gate. An action prototype lists
`EntityCondition`s for the performer and/or the target ("20 chemicals", "mouth uncovered", a
`MobStateCondition`) and `EntityEffect`s to apply after a successful use. Today its only users
are changeling abilities.

It works on a different layer from this model, and the two shouldn't be merged:

- **ActionRequirements is consumer-side and open-ended.** Any predicate on any entity, polled
  and uncached: each check raises one event per condition and builds arrays with LINQ. That is
  a good fit for an action-bar click.
- **MobCondition is producer-side mob state.** It uses a closed vocabulary, is cached in two
  fields, and sits on the hot path: `CanInteract` runs per verb, per UI and per frame. Routing
  it through `EntityCondition` would bring back what §7 rejects: allocations and type dispatch
  on the hot path, no cascade, no ignore masks, and an unbounded vocabulary.

The real overlap is in `ActionComponent`. `CheckCanInteract` and `CheckConsciousness`
(`ActionComponent.cs:92/99`, consumed at `SharedActionsSystem.cs:348–349` and `:454`) are a
two-option ignore mask in disguise. Eleven action prototypes opt out of one or both (14
fields; `BaseMentalAction` passes its opt-out to every child). The opt-out is blunt. Changeling Biodegrade ("vomit acid over your restraints") sets
`checkCanInteract: false` to work in cuffs, which also makes it usable while stunned; the
freedom implant has the same problem.

This is the consumer-side half of the model, the counterpart of tg's `check_flags`. It should
use the same vocabulary as interactions:

```yaml
- type: Action
  requires: { action: Interact, ignoring: Restrained }   # works cuffed, not stunned
```

The two systems then meet at a small seam:
- A `HasMobConditionCondition` entity condition reads the cached bits, so ActionRequirements,
  entity effects and objectives can ask "is the performer restrained?" from YAML.
- `ActionRequirementsComponent.FailPopup` has a TODO asking for conditions that explain their
  own failure. MobCondition already knows *which* condition blocked an action, so it can supply
  "You can't do that while restrained."

## 7. Rejected alternatives

**A capability enum owned per mob** (`Base` + `Current` sets of things a mob *can* do).
- It bloated immediately once creature properties like writing and literacy were folded in.
- A central `[Flags]` enum is hostile to a fork ecosystem: every downstream fork edits the same
  bits, and every upstream merge conflicts.
- The mistake was conflating action verbs with creature properties. Separating them removes
  the pressure entirely.

**Prototype-identified capabilities** (`ProtoId<CapabilityPrototype>` sets, like
`TagComponent`).
- It is extensible and fork-safe, but it solves a bloat problem that doesn't exist: tg runs on
  9 mobility flags.
- It costs string-keyed hashing on the hot path and networked string sets.
- The extensibility it buys is for a vocabulary already bounded by `ActionBlockerSystem`'s own
  API: adding an action already means editing that C# file.

**Expressing blocking as EntityConditions** (folding into #46029's framework). This is the
same trade as prototype capabilities, plus per-check allocation. See §6 for where the two
systems meet instead.

**Each producer declaring the actions it blocks.** This was the first version of this design.
It is wrong for the same reason tg doesn't do it: it duplicates *policy*. "What does being
handcuffed prevent" would become an edit in cuffs, again in stun, and again in every future
restraint.

## 8. Not done yet

- **Side effects on conditions.** tg hangs `drop_all_held_items()` off `HANDS_BLOCKED` and
  `stop_pulling()` off `PULL_BLOCKED`. In SS14 the drop is `DropHandItemsEvent`, raised from
  `MobStateSystem.Down()` (`Subscribers.cs:55`), so it is tied to mob state specifically.
  Moving it would make cuffing drop items through the same path as crit. That is correct, but
  it is a live gameplay change with its own virtual-item handling (cuffs and vehicles fill
  hands with unremovable virtual items), so it belongs in its own change.
- **The remaining pure-cancel sources.** These are AdminFrozen, VentriloquistPuppet,
  KitchenSpike, BeingDisposed, the Brain/Borg `Point` blocks, Worm `StandUp`, and the Chasm and
  TetherGun movement blocks. They are listed in spec §6.3 as later slices. Until they migrate,
  the attempt events keep working exactly as they do today.
- **Status effects.** Stun, knockdown and sleep are hybrids: a status effect ensures a mob-side
  component, and that component is the condition source. The `KnockdownStatusEffect` also pins
  `StandUp` directly through a relay (`SharedStunSystem.cs:69/371`). Whether
  `RefreshMobConditionsEvent` should be relayed through status effects is open (spec §11).
- **Collapsing the two standing events.** Both still exist. Per the `Stand` / `StandUp` split
  above they aren't actually redundant; merging them would require knockdown to ask its
  recovery question a different way.
- **Perception.** Players in crit or asleep still receive chat. `ChatSystem.GetRecipients`
  (`Content.Server/Chat/Systems/ChatSystem.Utility.cs:189`) filters only by map, range and
  `GhostHearingComponent`.
  - The natural next step is a `Hear` action plus a recipient filter there, with tg's "you can
    almost hear something" flavour for the muffled case.
  - Put the check behind a component query so the healthy-crew path stays allocation-free.
- **Buckle and pacification are deliberately not migrated.**
  - Buckle pins posture in *both* directions: it cancels `DownAttemptEvent` as well as
    `StandAttemptEvent`. Migrating it would need a `Down` action that exists for one consumer.
    tg itself special-cases buckle in `on_floored_trait_gain` rather than folding it into the
    cascade.
  - Pacification blocks harmful attacks specifically. It is a gameplay rule about intent, not
    a loss of faculties.

## 9. Risks

- **A producer must not do both.** A system that cancels attempt events *and* declares a
  condition makes its condition unforgivable, which silently defeats ignore-masks. Migrate one
  producer per commit.
- **Blocking depends on the component being present.** Blocking now depends on
  `MobConditionsComponent` existing. Every producer `EnsureComp`s it rather than relying on
  every mob prototype inheriting it from `MobDamageable`.
- **Prediction.** All migrated producers are shared, so client and server derive the same
  conditions. On the client, conditions arrive through the networked fields: `Refresh` skips
  while `ApplyingState`, and `MobStateChangedEvent` is no longer raised client-side (since
  `0f46f12599`).
- **Re-entrancy.** `VehicleSystem.CanOperate` (`:315`) calls `CanInteract` from inside an
  `UpdateCanMoveEvent` handler. That handler runs during `Refresh`. It is safe only because
  `Refresh` commits the new state *before* calling `UpdateCanMove`, and the spec makes that an
  invariant.
- **Fork impact.** `MobCondition` and `MobAction` are additive, and no public component is
  deleted. `InteractionAttemptEvent` gains an `Ignoring` field with a default, so existing
  subscribers compile unchanged. `ActionComponent.Requires` keeps the old bools readable for
  one release (spec §7).

## 10. Status

A working prototype was built in July 2026 against upstream `3d9a18a43c`. It covers the seven
original producers, the UI tiers and paper, and is kept as a local stash. At that base, the
full solution built clean, `MobConditionTest` covered the cascade, forgiveness and the wake-up
path, and the surrounding suites passed.

It has not been rebased. [spec.md](spec.md) lists the drift since then and is the reference for
re-implementing it.
