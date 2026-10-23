# Centralized mob action-blocking

This doc covers how SS14 decides what a mob can do, why that needs to change, and the model
proposed to replace it. The model is adapted from tgstation's condition/trait architecture
rather than ported from it.

**The end goal** is one bookkeeper system that replaces `ActionBlockerSystem`. Producers tell it
what is wrong with an entity, consumers ask it what the entity can do, and nothing else decides.
The conditions model below is its core. Spec §15 describes the end state, and spec §16 sorts
every current attempt-event subscriber into the bucket it moves to.

The normative details (types, tables, migration steps, tests) are in [spec.md](spec.md).
This document is the rationale. All code citations are against upstream `6a9a8abe96`
(2026-10-20).

## 1. The problem

`ActionBlockerSystem` is a stateless dispatcher. Each `CanX()` raises a cancellable attempt
event, and every system that might block it subscribes individually. Nothing owns the
decision, so the wiring is N sources × M actions of hand-written cancel handlers:

| System | Attempt-event subscriptions | Of which pure cancels |
|---|---|---|
| `Mobs/Systems/MobStateSystem.Subscribers.cs` | **13** | 12. Eleven route to one `CheckAct`, whose body is `switch (CurrentState) { case Dead: case Critical: args.Cancel(); }`. The Speak handler adds the force-say exception. |
| `Stunnable/SharedStunSystem.cs` | **8**, plus a status-effect `StandUpAttemptEvent` relay | 8 |
| `Bed/Sleep/SleepingSystem.cs` | **5** | 4. Speak carries the force-say exception. |
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

[Flags] public enum MobCapability : uint        // the mobility_flags layer
{ Interact, Conscious, UseHeld, Pickup, Drop, Attack, Throw, Speak, Emote,
  Point, Move, Stand, StandUp, Pull, ChangeDirection, UiAccess }
```

**Cascades** (`MobCapabilitiesSystem.Expand`, applied to a fixed point):

| Condition | Implies |
|---|---|
| `Unconscious` | `Incapacitated \| Immobilized \| Floored \| HandsBlocked` |
| `Restrained` | `HandsBlocked` |
| `Incapacitated`, `HandsBlocked` | `UiBlocked \| PullBlocked` |

**Policy** (`MobCapabilitiesSystem.GetBlocked`) is the single place where any condition blocks
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

The mask is applied before the cascade *and* after it: `Expand(declared & ~ignoring) & ~ignoring`.
- Before the cascade, forgiving `Restrained` also forgives the `HandsBlocked` it implies.
- After the cascade, forgiving a derived condition works too. `Ignoring = HandsBlocked`
  tolerates every hands-only cause.
- With a single pass, the second case would silently do nothing, because `Restrained` would
  re-derive `HandsBlocked`.

**The component** stores the declared set, pre-cascade, and nothing else of its own, because it
sits on every mob:

```csharp
public sealed partial class MobConditionsComponent : Component
{
    [AutoNetworkedField] public MobCondition Declared;  // union of producer Adds
    public MobCondition Conditions;                     // Expand(Declared), recomputed on state
    public MobCapability Blocked;                           // GetBlocked(Conditions), recomputed on state
}
```

Storing `Declared` rather than the expanded set is what makes ignore masks cheap. Forgiving a
condition is bit math on a stored field, so no source is ever re-polled on a hot path. It also
gives the client the same answer as the server: a client could only re-poll the sources it can
see.

**Multiple sources need no refcounts.** `MobCapabilitiesSystem.Refresh` raises
`RefreshMobConditionsEvent` and re-polls every source. Only the union is stored, so a source
that stops applying simply stops declaring on the next refresh. Waking up while still cuffed
unblocks nothing, because the cuffs re-declare on the same refresh.

This is tg's sourced traits, expressed the way SS14 already handles multi-source state: the
same poll-and-cache shape as `ActionBlockerSystem.UpdateCanMove` and
`RefreshMovementSpeedModifiers`.

The price of a cache is that every producer must refresh it at the right moment. Today the
blockers are evaluated live, so a stale value can't happen. Spec §6.1 makes "refresh before
anything else runs" a rule, and spec §4.5 asserts it in every integration test.

`UiBlocked` and `PullBlocked` only come from the cascade. Today they block nothing that
`Interact` doesn't already block: `CanPull` and the BUI gate both call `CanInteract`. They are
kept for tg parity, and so that a call site can forgive them on their own.

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

Ghosts are a special case. `GetUiStatus` resolves spectators (ghosts with `!CanGhostInteract`)
*before* consulting conditions, so `Incorporeal` never affects UI access and a spectator still
gets `ReadOnly`. `Incorporeal` governs everything else, so a ghost still can't pick things up or
emote. Admin ghosts have `CanGhostInteract`, so they declare nothing and are judged like anyone
else.

The `LifeStage` guard matters. A component is still subscribed while it is being removed, so
without the guard, the refresh that wakes a mob would re-declare unconsciousness.
`SleepingSystem` already uses this idiom in `OnSeeAttempt`.

When the refresh happens matters as much as the guard.
- `SleepingSystem` reacts in `ComponentRemove`, and its wake handler immediately calls
  `TryUnstun` and `TryStanding` (`SleepingSystem.cs:142–164`). The refresh has to come first, or
  `TryStanding` reads a cache that still says the mob is unconscious.
- Every producer has an anchor like this, listed in spec §6.2.

Because standing consults one cached value, `StandAttemptEvent` and `StandUpAttemptEvent` now
agree with each other without any change to knockdown's logic.

The slice-1 producers preserve today's behaviour, with one exception (spec §3.2). Each mapping
was checked against the events it replaces.
- **The exception:** `Unconscious` also blocks `StandUp`, which MobState never cancelled. A mob
  knocked down while crit now keeps its knockdown until it is revived, and then recovers
  normally.
- **Cuffs:** the cascade adds `Pull`, which cuffs never blocked through the event. `CanPull`
  already refuses through `CanInteract`, so this changes nothing in practice.

### Handlers that deliberately stay

Conditional logic is not boilerplate, so these handlers stay as they are:

- `SlipAttemptEvent` sets `NoSlip` rather than cancelling.
- `OnSeeAttempt` has its `LifeStage` guard. Unconsciousness deliberately does *not* blind,
  unlike in tg, because SS14 has never done that.
- `OnEquipAttempt`, `OnUnequipAttempt` and `OnUnbuckleAttempt` are self-only. They must not
  block a third party stripping a corpse.
- Cuffs' `HandleMoveAttempt` only blocks movement while the mob is being pulled.

`AllowNextCritSpeechComponent`, the damage force-say grant, was duplicated identically in
`MobStateSystem` and `SleepingSystem`. It is often called "last words", but it isn't the Last
Words action. When a mob goes crit, the server asks the client to send whatever was typed, and
the grant lets that one ordinary chat message through.

It is a stateful one-shot grant with a timeout, not a property of the mob, so no producer can
declare it. It doesn't belong to the bookkeeper either: spending it inside a query lets any
speech check use it up. Chat spends it as the message is sent, and forgives `Unconscious` for
that message through an ignore mask (spec §5.4).

Muting works the same way. The muted status effect declares `Silenced`, and the Last Words
action forgives `Unconscious | Silenced`. Chat shows the popup when a message is blocked, except
to an unconscious mind, which replaces the relay ordering that kept a dead mime from spamming
"you can't speak" (#45977).

## 5. UI status tiers

> **A later phase.** This is a refactor of its own. It was prototyped on `mob-conditions` and
> reverted, so the bookkeeper can land first. UIs behave as on master until then.

```csharp
public enum UiStatus : sbyte { Close = -1, Disabled = 0, ReadOnly = 1, Interactive = 2 }
```

`ActivatableUISystem.GetUiStatus(user, target, key)` resolves access in this order.
- **The key matters.** Today the ActivatableUI policy applies only to the BUI key it names, and
  other BUIs on the same entity are gated by `CanInteract` alone.
- A missing `ActivatableUIComponent`, or one for a different key, just means there is no
  per-UI policy.

1. **Spectator** (a ghost without `CanGhostInteract`): `ReadOnly`, unless `BlockSpectators` is
   set. Observing is a spectator rule rather than a mob capability, so it resolves before any
   condition is consulted; tg does the same (`states.dm:25`).
   - It keys on `CanGhostInteract`, not admin rights, so an admin watching as an ordinary
     ghost is still a spectator.
   - Admin ghosts fall through to the normal rules.
2. **`AdminOnly` without admin rights**: `Close`.
3. **`RequiresComplex` without `ComplexInteractionComponent`**: `Close`. This is a creature
   property, not a condition.
4. **`UiAccess` passes**: `Interactive`.
5. **Retry, forgiving the UI's `ReadOnlyConditions`.** If that passes: `ReadOnly`.
6. **Otherwise**: `Disabled`. The user could use it in principle, but their conditions block it
   right now.

`Close` and `Disabled` both refuse to open and refuse input. They differ for a window that is
already open:
- **Unconscious:** the window is closed.
- **Stunned or cuffed:** it stays open at `Disabled` and ignores input until the user recovers.

That is today's behaviour; nothing in SS14 closes UIs on stun or cuffs now. A 2-second stun
shouldn't throw away a console window.

Enforcement stays where upstream already put it:
- `SharedInteractionSystem.OnBoundInterfaceInteractAttempt` (`:150`) hooks the engine's
  `BoundUserInterfaceMessageAttempt`. The engine raises it for every non-close BUI message.
- The tier gate replaces only the `CanInteract`/ghost special case (`:154–164`) and the
  `RequiresComplex` check (`:193`): `Open` is allowed at `>= ReadOnly`, and every other message
  requires `Interactive`.
- The range cancel (`:168`, made a hard cancel by #46045) and the accessibility check stay as
  they are.
- The single-user lock applies only to interactive users. A spectator never holds it, so a ghost
  can't lock the crew out of a console.
- `ActivatableUISystem.OnBoundInterfaceOpenAttempt` (`:184`, also #46045) keeps its
  open-attempt veto. Every hook only cancels, so no ordering between them is needed.

This is tg's rule that "`ui_act` requires `UI_INTERACTIVE`". Read-only is authoritative on the
server, not a client courtesy. `ActivatableUIStatusSystem` closes open UIs only when the mob
becomes unconscious. It skips BUIs that opted out of input validation, such as borg laws and
vending machines.

**Ghosts inspect; they don't use.** Opening a UI is not free of side effects:
- triggers fire with the opener as user;
- shuttle consoles make the opener a pilot;
- arcade machines seat a player;
- the power monitor resets a focus that every viewer shares;
- paper resets its shared write mode.

So a read-only open has to be visible to the systems that react to it.
`Before/AfterActivatableUIOpenEvent` carry the `UiStatus`, and `IsInteractiveViewer` serves the
`BoundUIOpenedEvent` handlers. Each side-effect handler decides whether to skip, allow, or
treat the viewer as a spectator. Spec §8.5 lists every current handler and its required
behaviour.

On the client, a window MAY call the shared `GetUiStatus` to render an inspect mode. Until it
does, a spectator sees normal controls that the server ignores.

```yaml
- type: ActivatableUI
  key: enum.PaperUiKey.Key
  requiresComplex: false
  readOnlyConditions: Restrained   # cuffed users read; writes are rejected
```

A cuffed reader opens paper through the activate key or the verb. The read-only carve-out
calls the UI system directly, so the entity's other activation handlers don't run for a user
whose blockers just failed.

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
- A central `[Flags]` enum that keeps *growing* is hostile to a fork ecosystem: every downstream
  fork adds bits in the same place, and every upstream merge conflicts.
- The chosen `MobCondition` and `MobCapability` are also central enums. What makes them acceptable
  is that they are closed: they track `ActionBlockerSystem`'s own API, and tg's equivalents
  haven't changed in years.
- The mistake was conflating action verbs with creature properties. Separating them removes
  the pressure to grow.

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
  - Unlike slice 1, most of these are *not* parity. No closed condition matches their
    hand-picked event sets.
  - Examples: a frozen player would also lose `Drop`, and a spiked mob would lose `Interact`.
  - Spec §6.3 lists each delta. Each one ships as a stated gameplay change, or keeps a residual
    event.
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
- **Buckle, pacification and the other "situational" gates.** An earlier version of this
  design left these as attempt events. Replacing `ActionBlockerSystem` means every gate needs
  a home. Spec §16 gives each one a bucket:
  - **Buckle is a condition.** It pins posture in both directions, so it declares a new
    `PostureLocked`, which blocks a new `Down` action as well as `Stand`. Cryo pods use the
    same condition.
  - **Pacification is a pair rule.** It is about intent against a particular target, not a
    loss of faculties. The same goes for the AI whitelist and the guardian-host rule.
  - **Target-side checks are target vetoes.** Examples are virtual items and things hidden
    under the floor.

  Pair rules and target vetoes each reduce to one event that the bookkeeper raises. They
  replace the dozen typed attempt events.

## 9. Risks

- **A producer must not do both.** A system that cancels attempt events *and* declares a
  condition makes its condition unforgivable, which silently defeats ignore-masks. Migrate one
  producer per commit.
- **Blocking depends on the component being present.** Blocking now depends on
  `MobConditionsComponent` existing.
  - Mobs get it from `MobDamageable`.
  - Anything else gets it from the bookkeeper the first time a producer declares a condition on
    it.
  - Producers never add it, and never hook lifecycle events like `MapInit` for it. That matters
    for forks: the engine allows one subscription per component and event pair, so a new
    lifecycle hook on a popular component can collide with one a fork already has.
- **Stale cache.** Today every blocker is evaluated live. After the change, a producer that
  forgets to refresh, or refreshes too late, leaves a mob wrongly blocked or wrongly free.
  - The same goes for a field that is changed without its setter, for example through VV.
  - Spec §6.1 requires a refresh before anything downstream runs, and setters that refresh.
  - Spec §4.5 adds a test-time assertion that the cache matches a fresh poll.
- **Prediction.** Every producer MUST be shared and read only networked state. The client
  receives `Declared` through the network: `Refresh` skips while `ApplyingState`, and the
  derived fields are recomputed when the state is applied.
  - A server-only source would vanish from the client's view on the next predicted refresh.
    That is why `BrainComponent` (not networked) and `DnaImprint` (server-only) stay attempt
    events.
  - `MobStateChangedEvent` is still raised on the client, but only through predicted damage
    thresholds. After `0f46f12599` it is not a reliable client signal, and nothing here depends
    on it.
- **Handlers that un-cancel.** Ignore masks assume every attempt handler only ever cancels.
  `SharedStationAiSystem.OnHeldInteraction` (`SharedStationAiSystem.Held.cs:151`) instead assigns
  `Cancelled`, so it can undo an earlier cancel. The Core PR fixes it.
- **Re-entrancy.** `VehicleSystem.CanOperate` (`:315`) calls `CanInteract` from inside an
  `UpdateCanMoveEvent` handler. That handler runs during `Refresh`. It is safe only because
  `Refresh` commits the new state *before* calling `UpdateCanMove`, and the spec makes that an
  invariant.
- **Fork impact.** `MobCondition` and `MobCapability` are additive, and no public component is
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

A fresh demo now covers three producers (MobState, Stun, Cuffs) and three consumers (self-uncuff, paper
read-only with the UI tiers, failure popups). Spec §12 "Demo status" lists what it leaves out.

The spec was reviewed adversarially against `6a9a8abe96` on 2026-10-20 (spec Appendix B). The
review changed the stored state, the ignore-mask semantics, the UI tiers and the close policy.
Those parts of the prototype are superseded and shouldn't be copied.
