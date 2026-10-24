# Centralized mob action-blocking: design

This doc covers how SS14 decides what a mob can do, why that needs to change, and the model that
replaces it. The model is adapted from tgstation's condition and trait architecture, not ported from
it.

The end goal is one bookkeeper system, `MobCapabilitiesSystem`, that replaces `ActionBlockerSystem`.
Sources tell it what is wrong with an entity, consumers ask it what the entity can do, and nothing
else decides. [spec.md](spec.md) is the contract and owns every table and rule; this document is the
rationale and cites rules by id. [plan.md](plan.md) tracks the remaining work, and
[ui-tiers.md](ui-tiers.md) is a deferred phase.

## 1. The problem

On upstream master, `ActionBlockerSystem` is a stateless dispatcher. Each `CanX()` raises a
cancellable attempt event, and every system that might block it subscribes on its own. Nothing owns
the decision, so the wiring is N sources × M actions of hand-written cancel handlers. Counts at
upstream `6a9a8abe96`:

| System | Attempt-event subscriptions | Of which pure cancels |
|---|---|---|
| `MobStateSystem.Subscribers` | **13** | 12. Eleven route to one `CheckAct`, which cancels when Dead or Critical. The Speak handler adds the force-say exception. |
| `SharedStunSystem` | **8**, plus a status-effect `StandUpAttemptEvent` relay | 8 |
| `SleepingSystem` | **5** | 4. Speak carries the force-say exception. |
| `SharedCuffableSystem` | **6** | 5, gated on `!CanStillInteract`. The sixth blocks movement only while restrained *and* pulled. |
| `SharedGhostSystem` | **5** | 5, gated on `!CanGhostInteract` |
| `AdminFrozenSystem` | **9** | 7, plus Speak and Emote gated on `Muted` |
| `SharedStunSystem.Knockdown` | **1** | 1 |
| `LegsParalyzedSystem` | **1** | 1 |

The puppet, kitchen-spike, disposal, brain, borg, worm, chasm and tether-gun systems also cancel
unconditionally. Adding an action means editing every blocker, and adding a blocker means enumerating
every action.

The game shows the consequences:

- **"Can this mob stand?" has two answers.** `StandAttemptEvent` and `StandUpAttemptEvent` have
  disjoint subscriber sets.
- **Nothing records why something is blocked**, which forces destructive workarounds:
  - the zombie transform does `RemComp<ComplexInteractionComponent>`, with no record of who removed it;
  - self-uncuff flipped `CanStillInteract` true, checked, and flipped it back, to sneak past its own
    blocker;
  - the action bar's `CheckCanInteract` and `CheckConsciousness` bools are all-or-nothing, so an
    ability that must work in handcuffs also works while stunned.
- **Two systems can't agree on "is this mob incapacitated".** `CheckIncapacitatedCuffEvent` (#45296)
  is a fresh parallel query, answered separately by MobState, Sleeping and Stamina.
- **Handcuffed players can't read paper, and observers can't see most UIs.** Manipulating and
  perceiving are the same check, and ghosts are special-cased. The fix is the deferred UI phase
  ([ui-tiers.md](ui-tiers.md)).

## 2. What tgstation does

tg has three tiers.

**Producers add condition traits.** Sleep, oxyloss (`carbon_defense.dm`), hardcrit (`carbon.dm`) and
deathcoma all add `TRAIT_KNOCKEDOUT`, each under its own source. None of them names an action.

**Conditions cascade.**

```dm
on_restrained_trait_gain:     ADD_TRAIT(src, TRAIT_HANDS_BLOCKED, TRAIT_RESTRAINED)                       // init_signals.dm
on_handsblocked_start:        add_traits(list(TRAIT_UI_BLOCKED, TRAIT_PULL_BLOCKED), TRAIT_HANDS_BLOCKED)  // living.dm
on_incapacitated_trait_gain:  add_traits(list(TRAIT_UI_BLOCKED, TRAIT_PULL_BLOCKED), TRAIT_INCAPACITATED)  // init_signals.dm
```

**Terminal conditions translate** to `mobility_flags` bits; `on_ui_blocked_trait_gain` clears
`MOBILITY_UI`. Side effects hang off the *condition*, not the producer: `on_handsblocked_start` drops
held items and closes storage, and `on_pull_blocked` calls `stop_pulling()`.

**Consumers say what they tolerate.** Interactions use `INCAPACITATED_IGNORING(mob, flags)`: the mob's
incapacitation minus the causes the call site forgives. Actions carry `check_flags`
(`AB_CHECK_HANDS_BLOCKED | IMMOBILE | LYING | CONSCIOUS | INCAPACITATED | PHASED`), checked against the
same traits in `datums/actions/action.dm`.

Two properties make it work:
- **Traits are sourced.** `TRAIT_HANDS_BLOCKED` comes from stun (`debuffs.dm`), paralysis, stasis,
  stop-drop-and-roll, alien nests, vehicles, cuffs and unconsciousness. A mob stunned while cuffed and
  then uncuffed keeps the trait, because the stun's source remains.
- **Both vocabularies are small and closed:** 9 `mobility_flags` and a 4-bit `incapacitated` field,
  unchanged for years. What actually grows is *creature properties* (`TRAIT_ADVANCEDTOOLUSER`,
  `TRAIT_LITERATE`), and that is a different axis.

## 3. The model

Two closed enums, one component and one system, all in `Content.Shared/ActionBlocker/`. `MobCondition`
is the trait layer and `MobCapability` the `mobility_flags` layer. Spec §2 and §3 own the members, the
cascade and the policy.

**Policy is separate from producers.** A source says *what is wrong* (`Restrained`), never *what that
blocks*. `GetBlocked` is the single place where a condition blocks something (POL-1). "What does being
handcuffed prevent" is one table row, not an edit in cuffs, again in stun, and again in every future
restraint. The rows keep upstream's event sets where a source maps onto one: `HandsBlocked` blocks
exactly what cuffs cancelled, so a cuffed mob may still throw, and `Incapacitated` blocks what stun
cancelled.

**Closed vocabularies.** A member exists only because something needs it (VC-2). A member nothing
declares, derives or reads is one a call site could mask against and silently get nothing. So tg's
stasis and aggressive-grab bits are deliberately absent, because SS14 has neither mechanic: cryo pods
only lock posture, changeling regenerative stasis (#45104) is `MobState.Dead` and already arrives as
`Unconscious`, and pulling has no grab tiers. A member may exist ahead of its source while that source
is scheduled, or as a derived condition. `PullBlocked` is the derived case: no mask can forgive it, but
it gives `Pull` one policy row, lets `HasCondition` name it, and is where a stop-pull side effect would
hang (plan Q6), as tg's `on_pull_blocked` does.

**Stand and StandUp are separate** (VA-2, INV-1): knockdown asks `StandUp` to decide recovery, so
`Floored` can stop a mob being upright without pinning it forever. Both answers come from one
cached policy, so "can this mob stand?" has one answer for each question.

**The component stores the declared set, before the cascade.** That is what makes ignore masks cheap
and predictable: forgiving a condition is bit math on a stored field, so no source is re-polled on a
hot path, and the client gets the same answer as the server, which could only re-poll what it sees.

**Multiple sources need no refcounts.** `Refresh` re-polls every source and stores the union. A source
that stops applying stops declaring on the next refresh. Waking up while still cuffed unblocks
nothing, because the cuffs re-declare in the same refresh. This is tg's sourced traits, expressed the
way SS14 already handles multi-source state: the poll-and-cache shape of `UpdateCanMove` and
`RefreshMovementSpeedModifiers`.

**A cache has a price.** Every source must refresh at the right moment. Live evaluation can't go stale;
a cache can. The spec makes "refresh before anything else runs" a rule (PR-5), restricts writes to
refreshing setters (PR-6), and checks the cache against a fresh poll in every integration test
(CC-1). Live evaluation was considered and rejected (§8).

**The condition is also the reason.** There is no separate reason enum. A failure is explained by the
declared condition that blocks it (RSN-1), and whatever declared it may word it (EV-4), so a mime's vow
reads differently from a plain mute.

**Masks forgive declared conditions only** (IGN-1). A call site names the conditions it tolerates, and
the bookkeeper recomputes from the declared set minus those. That is tg's `INCAPACITATED_IGNORING`.
- Forgiving `Restrained` frees a cuffed mob but not an unconscious one, because unconsciousness
  derives `HandsBlocked` on its own.
- Applying the mask after the cascade too would let a call site forgive a derived condition, and with
  it every cause of that condition: ignoring `Incapacitated` would forgive crit, and ignoring `Floored`
  would let an unconscious mob stand. So masks name conditions, not causes, and a cause that must be
  forgiven on its own gets its own condition (IGN-6).
- A mask belongs to the act (IGN-8): its call site, or the act's own data, such as an action's
  `Requires`. Nothing on the actor can widen it.

A source looks like this:

```csharp
// In Initialize:
SubscribeLocalEvent<SleepingComponent, RefreshMobConditionsEvent>(OnRefreshConditions);

private void OnRefreshConditions(Entity<SleepingComponent> ent, ref RefreshMobConditionsEvent args)
{
    // Still subscribed while being removed; the removal refresh must not re-declare.
    if (ent.Comp.LifeStage <= ComponentLifeStage.Running)
        args.Add(MobCondition.Unconscious);
}
```

## 4. Capabilities

Conditions say what is stopping a mob right now. They can't say what its kind can't do at all: a
mouse can't use a computer, and a brain in a jar can't point. Upstream, those facts are a scatter of
marker components, such as `ComplexInteractionComponent`, and attempt-event cancels.

**Why two layers.** They answer different questions. A condition is what is wrong with the creature
now, and a call site may forgive it. A capability is what the creature can do at all, and nothing
forgives its absence (IGN-7). The question decides the layer, not how long the change lasts: a
capability can be lost for a while, through a revoke from a removable source (CAP-13). Only the
eligible bits can be capabilities (CAP-5), and no condition blocks `Complex` (INV-3), which settles
the borderline cases (SC-3).

**Why the baseline is immutable** (CAP-6, CAP-11). Every runtime change has an owner that declares it,
the same rule conditions follow. Nothing has to remember what a mob used to be, so nothing can drift,
and "why can this mouse talk?" always has an answer on the entity. A permanent change, such as
cognizine sentience, is a source that stays. Revoke beats grant (CAP-2), so a zombie's lost dexterity
can't be handed back by an item.

**Why the eligible bits are listed** (CAP-5). The default is "everything except `Complex`", so a
profile could otherwise lack `Conscious` or revoke `StandUp`, which mean nothing as creature traits.
Only bits that describe a kind of creature are eligible. `Move` is excluded: movement is a check on the
body that feeds the movement cache, and a profile that lacked it would silently do nothing.

**Example: cognizine.** Sentience grants through a component saved with the mob. A reagent that also
grants `Complex` uplifts a monkey enough to use computers, with no code. The mob's conditions still
apply, and a zombie's revokes still win. What the speech grant means depends on plan Q16: under
opt-out, a mouse already speaks (it squeaks through an accent) and only loses the accent; under opt-in,
every speaking creature names a profile with `Speak`, and the grant matters only for creatures
without one.

## 5. The other buckets

**Pair rules and target vetoes are separate and uncached.** Pacification is about intent against a
particular target, not a loss of faculties; the AI whitelist and the guardian-host rule likewise. Things
that can't be acted on at all, such as virtual items and things hidden under the floor, are vetoes on
the target. Both depend on the pair, so caching them per entity would be wrong, and there are few
handlers per event, so evaluating them at query time is cheap (RL-6).

**Two generic events, not a dozen typed ones.** Each rule tests the `Required` bits it cares about, so
one subscription covers every action. The cost is honest: the engine allows one directed subscription
per component and event, so rules must subscribe on their own component (RL-3) rather than on a shared
one. Per-action data, such as the weapon and the disarm flag, travels in a payload (RL-7). The events
are cancel-only by construction (EVT-2), so handler order can't un-block anything.

**The admin mute is a condition no mask names.** It is about the creature's state, so it is a condition
(SC-3). It must never be forgiven, unlike `Silenced`, which Last Words forgive. A pair rule was
considered, but the mute has no target. A capability revoke was considered, but the mute doesn't change
what the creature is. So `AdminMuted` is a condition, and IGN-9 keeps it out of every mask.

**The target events keep their names.** "Mob" in `MobActionTargetAttemptEvent` refers to the user; the
target can be any entity (EVT-1).

## 6. Speech

**Force-say is a send-time grant, not a condition.** On heavy damage, a stun, going crit or falling
asleep, the server asks the client to send whatever was typed. When the mob goes crit or falls asleep,
a short grant also lets that one line past unconsciousness. The grant is a stateful one-shot with a
timeout, not a property of the mob, so no source can declare it. Spending it inside a query would let
any speech check use it up. So the force-said line is its own message, and the server spends the grant
there; a line without a grant is ordinary speech (SPK-2).

**Only the act declares what it ignores** (IGN-8). Borgs' `UnblockableSpeech` and chat's
`ignoreActionBlocker` skip every check, conditions, capabilities and rules alike, so an admin-muted borg
talks and an admin-muted mob gasps into chat. A component on the actor can't know what each act
should tolerate; the act can.

**Terminology.** The damage force-say is often called "last words", but it is not the Last Words action.
The action is a deliberate whisper while crit, sent with its own mask (SPK-3).

## 7. Relationship to the action bar and ActionRequirements

Upstream added `ActionRequirementsComponent` and `ActionRequirementsSystem` (#46029,
`Content.Shared.Actions`). It is a per-action, data-driven gate: an action prototype lists
`EntityCondition`s for the performer or the target ("20 chemicals", "mouth uncovered", a
`MobStateCondition`) and `EntityEffect`s to apply after a successful use.

It works on a different layer from this model, and the two shouldn't be merged:
- **ActionRequirements is consumer-side and open-ended.** Any predicate on any entity, polled and
  uncached: each check raises one event per condition and builds arrays with LINQ. That fits an
  action-bar click.
- **MobCondition is source-side mob state.** It has a closed vocabulary, is cached, and sits on the hot
  path: the interaction check runs per verb, per UI and per frame. Routing it through `EntityCondition`
  would bring back what §8 rejects: allocations and type dispatch on the hot path, no cascade, no
  masks, and an unbounded vocabulary.

The real overlap is in `ActionComponent`. `CheckCanInteract` and `CheckConsciousness` are a two-option
ignore mask in disguise. Eleven action prototypes opt out of one or both, and `BaseMentalAction` passes
its opt-out to every child. The opt-out is blunt: Changeling Biodegrade ("vomit acid over your
restraints") sets `checkCanInteract: false` to work in cuffs, which also makes it usable while stunned,
and the freedom implant has the same problem.

This is the consumer-side half of the model, the counterpart of tg's `check_flags`, and it uses the
same vocabulary as interactions:

```yaml
- type: Action
  requires: { required: Interact, ignoring: Restrained }   # works cuffed, not stunned
```

The two systems meet at a small seam:
- `HasMobConditionCondition` reads the cached bits, so ActionRequirements, entity effects and
  objectives can ask "is the performer restrained?" from YAML (spec §9.3).
- `ActionRequirementsComponent.FailPopup` has a TODO asking for conditions that explain their own
  failure. The bookkeeper knows *which* condition blocked an action, so it can supply "You can't do that
  while restrained."

The struct is named `InteractionRequirements`, because `ActionRequirements` would collide with #46029
(REQ-5).

## 8. Rejected alternatives

**A capability set owned per mob** (`Base` and `Current` sets of things a mob *can* do).
- It bloated immediately once creature properties like writing and literacy were folded in.
- A central `[Flags]` enum that keeps *growing* is hostile to forks: every fork adds bits in the same
  place, and every upstream merge conflicts.
- `MobCondition` and `MobCapability` are central enums too. What makes them acceptable is that they are
  closed: they track the bookkeeper's own API, and tg's equivalents haven't changed in years.
- The mistake was conflating action verbs with creature properties.
- **Revisited.** The capability layer (§4) brings back a baseline without what sank this one. Its
  vocabulary is the closed `MobCapability`, with a short list of eligible bits; literacy stays a
  component. The baseline is an immutable prototype. And no history is stored: only the last poll's
  grants and revokes, rebuilt from sources on every refresh.

**Prototype-identified capabilities** (`ProtoId<CapabilityPrototype>` sets, like `TagComponent`). It is
extensible and fork-safe, but it solves a bloat problem that doesn't exist: tg runs on 9 mobility flags.
It costs string-keyed hashing on the hot path and networked string sets, and buys extensibility for a
vocabulary already bounded by the bookkeeper's API.

**Expressing blocking as EntityConditions** (folding into #46029's framework). The same trade as
prototype capabilities, plus per-check allocation. §7 says where the two meet instead.

**Each producer declaring the actions it blocks.** This was the first version of this design. It
duplicates *policy*, for the reason tg doesn't do it either (§3).

**Live evaluation instead of a cache.** Checking every source on every query is safer when a source
forgets to refresh: only reactions, such as movement, would lag. But it re-polls every source on every
check, and the interaction check is hot. The cache stays, with the safety net of CC-1.

**Masks applied after the cascade too.** Rejected for the holes it opened (§3).

**The admin mute as a pair rule, or as a capability revoke.** Rejected in §5.

**Keeping old APIs for a deprecation window.** It meant writing more code to preserve the old API than
to introduce the new one. They are removed outright instead; compatibility is planned per piece when the
work goes upstream (plan.md).

## 9. Deferred

- **Perception.** Players in crit or asleep still receive chat: `ChatSystem.GetRecipients` filters only
  by map, range and `GhostHearingComponent`. The natural next step is a `Hear` capability plus a
  recipient filter there, with tg's "you can almost hear something" for the muffled case. Put the check
  behind a component query so the healthy-crew path stays allocation-free.
- **Side effects on conditions.** tg hangs `drop_all_held_items()` off `HANDS_BLOCKED` and
  `stop_pulling()` off `PULL_BLOCKED`. In SS14 the drop is `DropHandItemsEvent`, raised from
  `MobStateSystem.Down`, so it is tied to mob state. Moving it would make cuffing drop items through the
  same path as crit. That is correct, but it is a gameplay change with its own virtual-item handling
  (cuffs and vehicles fill hands with virtual items), so it is its own change (plan Q6).
- **Merging the `Stand` and `StandUp` capabilities.** They aren't redundant (§3), and merging them
  would require knockdown to ask its recovery question differently.
- **Sources on other entities.** A vehicle that can't run while its operator can't operate needs a
  cross-entity source. Its shape is decided with the vehicle migration (plan Q18).
- **UI access tiers** ([ui-tiers.md](ui-tiers.md)).

## 10. Decisions

| Decision | Where |
|---|---|
| Conditions are cached; live evaluation is rejected. | §3, §8, CC-1 |
| UI access tiers are a separate, deferred phase. | ui-tiers.md |
| Capabilities are an immutable profile plus grants and revokes declared by sources. | §4, CAP-6, CAP-11 |
| Old APIs are removed outright, with no deprecation window, and `CanShiver`/`CanSweat` stay deleted; compatibility is planned per upstream piece. | plan.md |
| The vocabularies are closed; a member may exist for a planned source or as a derived condition. | VC-2 |
| Masks forgive declared conditions only; they name conditions, not sources. | IGN-1, IGN-6 |
| A mask belongs to the act: its call site or the act's own data. Nothing on the actor adds to it. | IGN-8 |
| An unforgivable block about the creature is a condition no mask names; the admin mute is one. | SC-3, VC-5 |
| The two generic rule events stay, with a payload; rules subscribe on their own component. | RL-3, RL-7 |
| Only consumers show feedback, and a user still unconscious after the act's mask gets none. | CON-2, RSN-7 |
| The component is `MobCapabilitiesComponent`. | CMP-4 |
| Item and implant relays come with the first such source. | PR-12 |
| The capability-eligible bits are listed; `Move` is not one. | CAP-5 |
| Capability changes get their own event. | EV-2 |
| The target events keep their names; "Mob" refers to the user. | EVT-1 |
| Cross-entity sources and the replay spectator are deferred. | plan Q18, Q19 |
