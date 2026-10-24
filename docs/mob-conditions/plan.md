# Mob capabilities: plan

This file tracks the work left between the code on branch `mob-conditions` and the end state in
[spec.md](spec.md). It is deleted when the refactor is done. [design.md](design.md) has the
rationale.

Conventions:
- Status lives here only. `[x]` means done in the code at `66df885e0c`; `[ ]` means not done.
- Items cite the spec rule they implement, such as `IGN-1`.
- Where a count would rot, the plan gives the `git grep` that produces it.

**No deprecation window.** The demo removes old APIs, events, relays and YAML fields outright, as their
replacements land. `ActionBlockerSystem` and the five typed target events are `[Obsolete]` at
`66df885e0c`; §1.6 deletes them, and nothing else is obsoleted. The refactor goes upstream later, in
pieces, and each piece plans its own compatibility then.

## 1. Migration

### 1.1 Done

- [x] The bookkeeper core: `MobCondition`, `MobCapability`, the cached component, `Refresh` with the
  re-entrancy guard, the cascade and the policy (`MobCapabilitiesSystem`).
- [x] The cache check behind `debug.mob_conditions_verify`, on in the integration-test pool. It checks
  `IsBlocked` only.
- [x] `Refresh` adds the component lazily. Producers add no component and hook no lifecycle event for
  the bookkeeper.
- [x] `ActionBlockerSystem` forwards every method to the bookkeeper. The bookkeeper raises the typed
  attempt events after its condition and `Complex` checks and before the rule events
  (`MobCapabilitiesSystem.RaiseLegacyAttempts`).
- [x] `ActionBlockerSystem` and the typed target events (`GettingInteractedWithAttemptEvent`,
  `GettingPickedUpAttemptEvent`, `GettingUsedAttemptEvent`, `GettingAttackedAttemptEvent`,
  `ThrowItemAttemptEvent`) are `[Obsolete]`.
- [x] `StandingStateSystem` (`Stand`, `Down`), knockdown (`StandUp`), `PullingSystem` and
  `SharedPointingSystem` ask the bookkeeper instead of raising their own attempt events.
- [x] Target vetoes moved to `MobActionTargetAttemptEvent` (§3.2).
- [x] Producers: MobState, Stun, Cuffs, Sleeping and the muted status effect (§3.1).
- [x] Requirements are capability masks. `Complex` is a bit, still read from
  `ComplexInteractionComponent`.
- [x] Self-uncuff forgives `Restrained` by mask (`SharedCuffableSystem.OnUncuffAttempt`).
- [x] `SharedStationAiSystem.OnHeldInteraction` only ever cancels.
- [x] Failure reasons: `TryGetBlockedReason` and `GetMobConditionReasonEvent`, relayed through status
  effects. Chat, the activate key and self-uncuff show them.
- [x] Last Words send with `Unconscious | Silenced`.
- [x] `StatusEffectsSystem` sets `Applied` before it raises `StatusEffectAppliedEvent` (SE-5).
- [x] `CanShiver`, `CanSweat`, `ShiverAttemptEvent` and `SweatAttemptEvent` are deleted.
- [x] `InteractionAttemptEvent` has no `Ignoring` field.

### 1.2 Core changes

- [ ] Rename `MobConditionsComponent` to `MobCapabilitiesComponent`: class, file, YAML type
  `MobCapabilities` on `MobDamageable` in `Mobs/base.yml`, and the test prototypes (CMP-4, PRES-3).
- [ ] Masks forgive declared conditions only: `Effective = Expand(declared & ~ignoring)` (IGN-1). Update
  the table tests (§8).
- [ ] `CanPerform` checks capabilities before conditions. Today `CheckSelf` checks conditions first,
  and a missing `Complex` gives no reason. Add `mob-capability-lacking-<member>` (CP-1, RSN-6).
- [ ] `CanPerform` returns no reason while the user is still `Unconscious` after the mask. Then delete
  chat's own check in `ChatSystem.CanSpeak` (RSN-7). This also silences the activate key while
  unconscious (CON-4).
- [ ] The bookkeeper never shows popups: delete `MobCapabilitiesSystem.CanPickup` and its
  `showPopup` path (CON-2).
- [ ] `SharedHandsSystem.TryPickup` and `TryPickupAnyHand` take `showPopup = false`; the player paths
  pass true; `CanPickupToHand` shows the reason when asked (CON-3).
- [ ] Popups leave the rule handlers for their consumers: the guardian's throttled popup
  (`GuardianSystem.OnGuardianAttackAttempt`, `OnPilotAttackAttempt`), pacification's
  (`PacificationSystem.OnAttackAttempt`), and Worm's stand-up message, which becomes `Pinned`'s reason
  (RL-4). Each lands with its row in §3.
- [ ] Self-uncuff (`SharedCuffableSystem.OnUncuffAttempt`) drops its fallback text
  `cuffable-component-cannot-interact-message`, so a null reason shows nothing, and uses a predicted
  popup (CON-5, CON-6).
- [ ] Rule events get `Cancelled { get; private set; }` and `Cancel(string? reason)`, keeping the first
  non-null reason (EVT-2, RL-1, RSN-8).
- [ ] Add `MobActionPayload` (weapon, disarm), the payload overload of `CanPerform`, and pass it to
  both rule events. Delete `MobCapabilitiesSystem.CanAttack` and `LegacyArgs` (RL-7, CP-3).
- [ ] `OnHandleState` raises `MobConditionsChangedEvent` when the derived `Conditions` change. Today it
  only updates movement (EV-2).
- [ ] The cache check also covers `HasCondition` (CC-1).
- [ ] `InteractionRequirements` presets (REQ-3). Audit zero-valued uses with
  `git grep -nE "default\(InteractionRequirements\)|InteractionRequirements [A-Za-z_]+;" -- '*.cs'` (REQ-2).
- [ ] Locale: add `mob-condition-name-pullblocked`, which is missing, and each new member's keys as
  it lands (RSN-9).
- [ ] Prototype test: no YAML mask names `PullBlocked` or `AdminMuted` (IGN-9), and no `Required`
  names `Move` (REQ-4).
- [ ] The entity-condition bridge: `MobConditionEntityConditionSystem`, `HasMobConditionCondition`,
  `MobCapabilitiesSystem.GetLocalizedName` and the guidebook key (spec §9.3).
- [ ] With the first item or implant source: relay `RefreshMobConditionsEvent` and
  `GetMobConditionReasonEvent` through inventory, hands or implants, and refresh the holder wherever
  the source moves (PR-12, PR-13, Q22). No such source is planned yet.

The capability half of the core (`Granted`, `Revoked`, `HasCapability`, `MobCapabilitiesChangedEvent`
and the eligible-bit test) needs the capability fields, so it is in §6.

### 1.3 Vocabulary additions

Each lands with its first source or consumer (VC-2).

- [ ] `AdminMuted` (name open, Q21). AdminFrozen declares it while `Muted`. `FreezeAndMute` and
  every other write to `Muted` refresh. Delete `AdminFrozenSystem.OnSpeakAttempt` and
  `OnEmoteAttempt`. Add it to the policy (`Speak | Emote`) and to the reason priority.
- [ ] `Pinned`, with the knockdown status effect and Worm (§3.1).
- [ ] `PostureLocked`, with buckle and the cryo pod (§3.1).
- [ ] `Equip`, consumed by the inventory's self-equip and self-unequip path. Delete the self-only
  `IsEquippingAttemptEvent`/`IsUnequippingAttemptEvent` handlers in MobState, Stun and Cuffs.

### 1.4 Speech

- [ ] Force-say becomes its own message type (SPK-2):
  - `ChatUIController.OnDamageForceSay` sends the wrapped line as a force-say message on its channel,
    Local or Whisper, instead of force-submitting the chat box;
  - the server sends every force-said line as speech. With `AllowNextCritSpeechComponent` present it
    removes the grant and forgives `Unconscious`; without it the line takes the normal checks. Lines
    from stuns and damage, which never had a grant, keep working;
  - delete `DamageForceSaySystem.OnCheckIgnoreSpeech` and `CheckIgnoreSpeechBlockerEvent.Ignoring`.
- [ ] Remove chat's `ignoreActionBlocker` parameters in favour of masks from the act (IGN-8, SPK-4).
  Find them with `git grep -n "ignoreActionBlocker\|IgnoreActionBlocker" -- '*.cs' Resources`.
  - `RespiratorSystem` (gasp) and `DeathgaspSystem` pass `Ignoring = Unconscious`.
  - `AutoEmotePrototype.IgnoreActionBlocker` becomes a mask field (`Voice/auto_emotes.yml`).
  - The `HasCondition(Silenced)` checks in `DeathgaspSystem.Deathgasp` and
    `CritMobActionsSystem.OnFakeDeath` stay (SPK-4, SPK-7).
- [ ] Redesign `UnblockableSpeech` (Q20). Delete its blanket bypass (`UnblockableSpeechSystem.OnCheck`).
  `CheckIgnoreSpeechBlockerEvent` then has no subscriber: delete it and
  `ChatSystem.CheckIgnoreSpeechBlocker`.

### 1.5 Movement cache

These items don't wait for any open question:
- [ ] `Refresh` computes movement for every entity with an `InputMoverComponent`, with or without
  the bookkeeper's component: RF-3 goes on to RF-6 instead of returning (MOV-1).
- [ ] `SharedMoverController`'s existing `ComponentStartup` and `PhysicsBodyTypeChangedEvent` handlers
  on `InputMoverComponent` call `Refresh`, and its `OnCanMove` body check moves into the movement
  answer (MOV-1, MOV-2).
- [ ] `CanMoveUpdatedEvent` is raised only when the value changes. Today `UpdateCanMove` raises it on
  every call (MOV-4).
- [ ] The server `Dirty`s the mover when the value changes; `InputMoverComponentState` keeps carrying
  it (MOV-3).
- [ ] On the client, `SharedMoverController.OnMoverHandleState` raises `CanMoveUpdatedEvent` when it
  changes `CanMove`, and the bookkeeper's state handling stops writing it (MOV-3, MOV-4).

These finish only once the vehicle (Q18) and the replay spectator (Q19) are decided:
- [ ] The cache check covers `CanMove` (CC-1). Until every `UpdateCanMoveEvent` subscriber is a source,
  the stored value includes legacy cancels that a fresh MOV-1 computation doesn't, so the check would
  log false errors.
- [ ] Every `UpdateCanMoveEvent` subscriber becomes a source (§3.1). Every `UpdateCanMove` caller
  becomes `Refresh`: `git grep -n "\.UpdateCanMove(" -- '*.cs'`.
- [ ] Make `UpdateCanMove` private to the bookkeeper.

### 1.6 Removing the old API

Each item goes once its last subscriber or caller has moved.

- [ ] `ActionBlockerSystem`: move its callers (`git grep -ln "ActionBlockerSystem" -- '*.cs'`), one
  area per commit, then delete it:

  | `ActionBlockerSystem` | Bookkeeper |
  |---|---|
  | `CanInteract(user, target)` | `CanPerform(user, InteractionRequirements.Interact, target)` |
  | `CanConsciouslyPerformAction(user)` | `CanPerform(user, InteractionRequirements.Conscious)` |
  | `CanUseHeldEntity(user, used)` | `CanPerform(user, { Required = UseHeld }, used: used)` |
  | `CanPickup(user, item, showPopup)` | `CanPerform(user, { Required = Pickup }, item)`; the caller shows the reason (CON-3) |
  | `CanDrop(user)` | `CanPerform(user, { Required = Drop })` |
  | `CanThrow(user, item)` | `CanPerform(user, { Required = Throw }, used: item)` |
  | `CanAttack(user, target, weapon, disarm)` | `CanPerform(user, { Required = Attack }, target, weapon?.Owner, new MobActionPayload(weapon, disarm), out _)` |
  | `CanSpeak`, `CanEmote`, `CanChangeDirection` | `CanPerform` with that capability |
  | `CanMove(user)` | `CanMove(user)` |
  | `UpdateCanMove(user)` | `Refresh(user)`: the caller is a source whose state changed |
  | `CanComplexInteract(user)` | `HasCapability(user, Complex)`, or `Complex` in the mask |

- [ ] The typed events, their relays and the bookkeeper code that raises them:

  | Capability | User event | Target event | Waits for |
  |---|---|---|---|
  | `Interact` | `InteractionAttemptEvent` | `GettingInteractedWithAttemptEvent` | Ghost, AdminFrozen, puppet, disposal, `BlockMovement`, StationAi, Q19 |
  | `Conscious` | `ConsciousAttemptEvent` | none | nothing: it has no subscriber |
  | `UseHeld` | `UseAttemptEvent` | `GettingUsedAttemptEvent` | bucket 1 sources, Q19 |
  | `Pickup` | `PickupAttemptEvent` | `GettingPickedUpAttemptEvent` | bucket 1 sources, Q19 |
  | `Drop` | `DropAttemptEvent` | none | bucket 1 sources, Q19 |
  | `Attack` | `AttackAttemptEvent` | `GettingAttackedAttemptEvent`, and its mech relay | bucket 3 rules, Q19 |
  | `Throw` | `ThrowAttemptEvent` | `ThrowItemAttemptEvent` | bucket 1 sources, Q19 |
  | `Speak` | `SpeakAttemptEvent`, and its status-effect relay | none | AdminFrozen, Q16 |
  | `Emote` | `EmoteAttemptEvent` | none | AdminFrozen, Ghost, puppet, Q16 |
  | `Point` | `PointAttemptEvent` | none | brain, borg brain, DnaImprint |
  | `Move` | `UpdateCanMoveEvent`, and its status-effect relay | none | §1.5 |
  | `Stand` | `StandAttemptEvent` | none | knockdown, buckle |
  | `Pull` | `StartPullAttemptEvent` | none | buckle |
  | `ChangeDirection` | `ChangeDirectionAttemptEvent` | none | bucket 1 sources, Q11, Q19 |
  | `StandUp` | `StandUpAttemptEvent`, and its status-effect relay | none | Worm, the knockdown status effect |
  | `Down` | `DownAttemptEvent` | none | buckle, cryo pod |

- [ ] In the bookkeeper: `RaiseLegacyAttempts`, `RaiseConsciousAttempt`, the event half of
  `RaiseAttackAttempts` (the container rule stays, RL-8), `CanStandUp(uid, ref StandUpAttemptEvent)`,
  `CanComplexInteract` and `GetPropertyBlocked`.
- [ ] `ComplexInteractionComponent` (§6).
- [ ] `ActionComponent.CheckCanInteract` and `CheckConsciousness` (§5).
- [ ] `MakeSentientEntityEffect.AllowSpeech` and the `allowSpeech` parameter of
  `SharedMindSystem.MakeSentient` (§6).

## 2. PR slicing

For the demo branch:

1. **Core changes** (§1.2), one commit per item.
2. **Speech** (§1.4).
3. **Bucket 1 sources**, one source per commit, in §3.1 order. Each commit deletes the handlers its
   condition covers (PR-10) and adds the vocabulary it needs (§1.3). A commit with a delta (§4) says
   so in its message.
4. **Bucket 3 rules**, one per commit (§3.3).
5. **The movement cache** (§1.5).
6. **Capabilities** (§6).
7. **The action bar at parity** (§5), then the precision follow-ups as a separate commit.
8. **The entity-condition bridge** (§1.2, spec §9.3).
9. **Removal** (§1.6).

Ordering:
- The steps can interleave. The capability half of the core waits for §6's fields, and each removal
  in §1.6 waits for the subscribers in its "Waits for" column.
- A mask forgives a cause only once that cause's source row has landed. While a source still cancels
  a typed event, no mask can forgive it. So a new mask, such as the §5 precision follow-ups, waits for
  the rows it means to forgive.

For upstream, later: the work goes in pieces. Each piece decides then whether downstream code needs a
deprecation step, lists its gameplay deltas from §4, and links design.md.

## 3. Attempt-event inventory

Every subscriber of the events `ActionBlockerSystem` raised, of the standing, stand-up, down, pull and
point events, and of their target-side counterparts. The self-only equip and unbuckle checks that
duplicate them are included.

### 3.1 Bucket 1: conditions

| Source (file) | Today | Declares | Refresh on | Status |
|---|---|---|---|---|
| MobState (`Mobs/Systems/MobStateSystem.Subscribers.cs`) | producer; self-only `OnEquipAttempt`, `OnUnequipAttempt`, `OnUnbuckleAttempt` | `Unconscious` (Critical, Dead). Equip moves to `Equip`; unbuckle becomes a buckle consumer asking `Interact`. `Down` and `OnIncapCuffCheck` (Q3) stay. | `OnStateEnteredSubscribers`, before standing and dropping | [x] producer; [ ] equip; [ ] unbuckle |
| Stun (`Stunnable/SharedStunSystem.cs`, `StunnedComponent`) | producer; self-only `OnEquipAttempt`, `OnUnequipAttempt` | `Incapacitated \| Immobilized`. Equip moves to `Equip`. | `StunnedComponent` startup and shutdown | [x] producer; [ ] equip |
| Knockdown (`Stunnable/SharedStunSystem.Knockdown.cs`, `KnockedDownComponent`) | `OnStandAttempt` (Stand) | `Floored`. `TryStand` and `CanStand` keep asking `StandUp`. | knockdown start; shutdown, before standing | [ ] |
| Knockdown status effect (`SharedStunSystem.OnStandUpAttempt`) | relayed StandUp | `Pinned`, through the relay (SE-1) | effect applied and removed (SE-4) | [ ] |
| Sleeping (`Bed/Sleep/SleepingSystem.cs`) | producer; self-only `OnUnbuckleAttempt` | `Unconscious`. Unbuckle becomes a buckle consumer. `OnSeeAttempt` (SC-8), the `StunEndAttemptEvent` handler and `OnIncapCuffCheck` (Q3) stay. Sleeping also ensures `StunnedComponent` and `KnockedDownComponent`, so a sleeper also declares stun's `Incapacitated \| Immobilized` and knockdown's `Floored`: forgiving `Unconscious` still leaves the stun. | `OnCompInit`, `OnComponentRemoved`; the refresh runs before `SleepStateChangedEvent`, whose handler stands the mob up | [x] producer; [ ] unbuckle |
| Cuffs (`Cuffs/SharedCuffableSystem.cs`) | producer; `HandleMoveAttempt`; self-only equip and unequip | `Restrained` while `!CanStillInteract`; `Immobilized` while restrained and pulled. Equip moves to `Equip`. `HandleStopPull` and the buckle checks stay domain rules. | `UpdateCuffState`, before `CuffedStateChangeEvent`; `OnPull` for the pulled case | [x] `Restrained`; [ ] `Immobilized`; [ ] equip |
| Ghost (`Ghost/Systems/SharedGhostSystem.cs`) | `OnAttempt`, `OnAttemptInteract`: Use, Emote, Drop, Pickup, Interaction | `Incorporeal` while `!CanGhostInteract` | `SetCanGhostInteract`. Don't add a shared `GhostComponent, ComponentStartup` subscription: client and server already have one. `canInteract` is YAML (`admin_ghost.yml`), so runtime writes go through the setter. | [ ] |
| Paralysed legs (`Traits/Assorted/LegsParalyzedSystem.cs`) | Move | `Immobilized`. Its buckle and throw handlers stay. | startup, shutdown | [ ] |
| AdminFrozen (`Administration/AdminFrozenSystem.cs`) | Use, Pickup, Throw, Interaction, Attack, ChangeDirection, Move; Speak and Emote while `Muted` | `Incapacitated \| Immobilized`; `AdminMuted` while `Muted`. Its `PullAttemptEvent` handler (a frozen entity can't be pulled) and its `InGameOocMessageAttemptEvent` handler (the LOOC mute) stay as domain rules (SC-6). | startup, shutdown, every write to `Muted` | [ ] |
| Ventriloquist puppet (`Puppet/SharedVentriloquistPuppetSystem.cs`) | Use, Interaction, Drop, Pickup, Move, ChangeDirection, Emote | `Incapacitated \| Immobilized`. Emote moves to bucket 4: check that the puppet prototype (`Objects/Fun/toys.yml`) has no enabled emoting. | startup, shutdown | [ ] |
| Kitchen-spike hook (`Kitchen/SharedKitchenSpikeSystem.cs`, `KitchenSpikeHookedComponent`) | ChangeDirection, Use, Throw, Drop, Attack, Pickup, equip, unequip | `Incapacitated`. Check whether its equip handlers are self-only before folding them into `Equip`. | hook, unhook | [ ] |
| Being disposed (`Disposal/Unit/SharedBeingDisposedSystem.cs`) | Interaction, Attack | `HandsBlocked` | startup, shutdown | [ ] |
| Worm (`Movement/Systems/WormSystem.cs`) | StandUp, with a message, turning autostand off | `Pinned`. The message becomes its reason; autostand is set on the worm's own knockdown state. | startup, shutdown | [ ] |
| Chasm (`Chasm/ChasmSystem.cs`, `ChasmFallingComponent`) | Move | `Immobilized` | startup, shutdown | [ ] |
| Tether gun (`Weapons/Misc/SharedTetherGunSystem.cs`, `TetheredComponent`) | Move | `Immobilized` | startup, shutdown | [ ] |
| Buckle (`Buckle/SharedBuckleSystem.Buckle.cs`) | Down, Stand, Move while buckled | `PostureLocked`; `Immobilized` unless the entity has `RelayInputMoverComponent` | buckle, unbuckle | [ ] |
| Cryo pod (`Medical/Cryogenics/SharedInsideCryoPodSystem.cs`) | Down | `PostureLocked` | insert, eject | [ ] |
| Climbing (`Climbing/Systems/ClimbSystem.cs`) | Move while `NextTransition != null` | `Immobilized` mid-climb | climb start, climb end | [ ] |
| Piloting (`Shuttles/Systems/SharedShuttleConsoleSystem.cs`, `PilotComponent`) | Move | `Immobilized` | startup, shutdown | [ ] |
| `BlockMovement` (`Interaction/SharedInteractionSystem.Blocking.cs`) | Interaction, Drop, Pickup if `BlockInteraction`; Use if `BlockUse`; Move unless relaying input; ChangeDirection | `HandsBlocked` for the interaction flags; `Immobilized` unless it relays input. ChangeDirection is Q11. | startup, shutdown | [ ] |
| Muted status effect (`Speech/Muting/MutedStatusEffectSystem.cs`) | none left | `Silenced`, through the relay; it words the mime's vow through `GetMobConditionReasonEvent` | effect applied and removed | [x] |
| Vehicle (`Vehicle/Systems/VehicleSystem.cs`, `OnVehicleUpdateCanMove`, `OnOperatorUpdateCanMove`) | Move when the vehicle can't run, or its operator can't operate | keeps its handlers until Q18 | n/a | blocked on Q18 |
| Replay spectator (`Content.Client/Replay/Spectator/ReplaySpectatorSystem.Blockers.cs`) | Use, Pickup, Throw, Interaction, Attack, Drop, Move, ChangeDirection | open (Q19) | n/a | blocked on Q19 |
| Body check (`Movement/Systems/SharedMoverController.cs`, `OnCanMove`) | Move without a non-static physics body | the movement answer (MOV-1) | mover startup and body-type changes (MOV-2) | [ ] |

### 3.2 Bucket 2: target vetoes (`MobActionTargetAttemptEvent`)

| System (file) | Rule | Status |
|---|---|---|
| `SharedVirtualItemSystem` (`Inventory/VirtualItem/`) | A virtual item can't be interacted with. | [x] |
| `SharedSubFloorHideSystem` (`SubFloor/`) | Things hidden under the floor can't be touched or attacked. Users with `BypassInteractionChecksComponent` may touch them. | [x] |
| `MultiHandedItemSystem` (`Item/`) | Picking up needs enough free hands. The message is the reason. | [x] |
| `SharedMechSystem.Relay` (`Mech/EntitySystems/`) | Relays the `Attack` veto to the pilot as `MechPilotRelayedEvent<MobActionTargetAttemptEvent>`. The guardian's relayed handler moved with it. | [x] veto; [ ] delete the `GettingAttackedAttemptEvent` relay |
| client `DragDropSystem.ValidDragDrop` (`Content.Client/Interaction/`) | Asks `CanPerform` about the target and the dragged entity. | [x] |
| none | `GettingUsedAttemptEvent` and `ThrowItemAttemptEvent` have no subscribers. | [ ] delete (§1.6) |

### 3.3 Bucket 3: pair rules (`MobActionAttemptEvent`)

| System (file) | Rule | Status |
|---|---|---|
| `PacificationSystem.OnAttackAttempt` (`CombatMode/Pacification/`) | A pacified user can't make harmful attacks. It reads the weapon and the disarm flag from the payload. Its popup moves to the melee consumer. Its own `AttemptPacifiedAttackEvent` stays internal to the rule. | [ ] |
| `SharedStationAiSystem.OnHeldInteraction` (`Silicons/StationAi/SharedStationAiSystem.Held.cs`) | The AI can only interact with whitelisted targets. | [ ] |
| `GuardianSystem.OnGuardianAttackAttempt` (`Guardian/`) | A guardian can't attack its host. Its throttled popup moves to the consumer. | [ ] |
| `SharedBuckleSystem.OnPullAttempt` (`Buckle/SharedBuckleSystem.Buckle.cs`) | You can't pull what you are buckled to, unless it has a `PullStrap`. | [ ] |
| `VehicleSystem.OnOperatorAttackAttempt` | An operator can't attack their own vehicle when the vehicle `CanAttack`. | [ ] |
| `VehicleSystem.OnOperatorCanAttackFromContainer` | Stays: it is the built-in container rule's exception hook (RL-8). | [x] |
| `DnaImprintEntityEffectSystem.OnAttackAttempt` (`Content.Server/EntityEffects/Effects/`, `NPCImprintedComponent`) | An imprinted NPC won't attack its friends. Server-only (RL-9). | [ ] |

### 3.4 Bucket 4: capabilities

| System (file) | Today | Becomes | Status |
|---|---|---|---|
| `SpeechSystem` (`Speech/`) | cancels Speak, broadcast, without an enabled `SpeechComponent` | Q16 | blocked on Q16 |
| `EmoteSystem` (`Emoting/`) | cancels Emote without `EmotingComponent` | Q16 | blocked on Q16 |
| `BrainSystem` (`Body/Systems/`), `SharedBorgSystem.OnBrainPointAttempt` (`Silicons/Borgs/`) | cancel Point | the `Brain` profile lacks `Point` | [ ] |
| `CanComplexInteract` | reads `ComplexInteractionComponent` | the `Dexterous` profile; readers ask `HasCapability(Complex)` | [ ] |
| `ZombieSystem.Transform` (`Content.Server/Zombies/`) | removes `ComplexInteractionComponent` | `ZombieComponent` revokes `Complex` | [ ] |

### 3.5 Not gates

| System | Today | Destination | Status |
|---|---|---|---|
| `DnaImprintEntityEffectSystem.OnPointAttempt` (`DNALeaderComponent`) | Point, never cancels: clears follower targets as a side effect | its existing `AfterPointedAtEvent` handler, which clears the targets first and then sets the new ones (RL-5) | [ ] |
| `MutedStatusEffectSystem` | relayed `EmoteEvent`, `EmoteActionEvent` | stays: it muffles vocal emotes, it doesn't gate them | n/a |

## 4. Gameplay deltas

Each goes in its PR's description. Q7 asks maintainers whether they are acceptable.

| Change | Cause |
|---|---|
| A mob knocked down while crit keeps the knockdown until revived, then recovers normally. | `Unconscious` blocks `StandUp` (INV-7). |
| Cuffed mobs are `PullBlocked`. Nothing changes in play: `PullingSystem` already denied it through `Interact`. | the cascade |
| An admin-frozen player can't drop. | `Incapacitated` |
| A ventriloquist puppet can't attack or throw. | `Incapacitated` |
| A hooked mob on a kitchen spike can't interact. | `Incapacitated` |
| A mob in disposals can't use held items, pick up or drop. | `HandsBlocked` |
| A cryo-pod occupant can't stand. It is already upright and can't move, so nothing observable changes. | `PostureLocked` |
| A player's own pickup explains any block, conditions included. A programmatic pickup shows nothing, not even the multi-handed message. | CON-3 |
| The activate key explains a failed activation, except to an unconscious user. | CON-4, RSN-7 |
| Drag-drop applies the user's pair rules to the dragged entity too, such as the AI whitelist. | `ValidDragDrop` asks `CanPerform` |
| An admin-muted borg can't speak, and an admin-muted mob doesn't gasp into chat. | `UnblockableSpeech` and `ignoreActionBlocker` no longer bypass the check (IGN-8) |
| Ignoring a derived condition no longer forgives it. A call site that ignored `HandsBlocked` to tolerate cuffs must ignore `Restrained`. | IGN-1, IGN-5 |

## 5. Action-bar migration

- [ ] Add `ActionComponent.Requires` and check it in `SharedActionsSystem.OnValidate` and
  `ValidateEntityTarget` (AB-1, AB-2).
- [ ] Translate every prototype, then delete `CheckCanInteract` and `CheckConsciousness`. No YAML shim.
- [ ] The precision follow-ups, as a separate balance commit:
  - **Biodegrade** and the **freedom implant**: `{ required: Interact, ignoring: Restrained }`. They
    work cuffed but not while stunned; today they work while stunned.
  - **Scream** and **Wagging** keep `Conscious`.

Translation:

| Today | `requires` |
|---|---|
| `checkCanInteract: true`, `checkConsciousness: true` (the default) | `{ required: Interact }` (the default) |
| `checkCanInteract: false`, `checkConsciousness: true` | `{ required: Conscious }` |
| `checkCanInteract: false`, `checkConsciousness: false` | `{ required: None }` |
| `checkCanInteract: true`, `checkConsciousness: false` | `{ required: Interact }`. Interacting already needs consciousness, so this pair never behaved differently. |

The prototypes that set the fields, by their *effective* values after inheritance. Re-grep with
`git grep -nE "checkCanInteract:|checkConsciousness:" -- Resources/Prototypes` and resolve each hit
against its parents before migrating.

| Prototype | File | Today (effective) | `required` |
|---|---|---|---|
| `BaseMentalAction` and every child (crit, mapping, observer toggles, laws, pAI, `ActionSleep`, `ActionWake`, suicide implants and others) | `Actions/types.yml` | both false | `None` |
| `ActionScream` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionActivateFreedomImplant` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionActivateEmpImplant` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionActivateScramImplant` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionToggleWagging` and its child `ActionToggleWaggingVulpkanin` | `Actions/types.yml` | interact false | `Conscious` |
| `ActionChangelingBiodegrade` | `Actions/changeling.yml` | interact false | `Conscious` |
| `ActionChangelingStasis` and its child `ActionChangelingStasisWeak` | `Actions/changeling.yml` | both false | `None` |
| `ActionChangelingLastResort` | `Actions/changeling.yml` | both false | `None` |
| `ActionStethoscope` | `Entities/Clothing/Neck/misc.yml` | interact false | `Conscious` |
| `ActionGhostBoo` | `Entities/Mobs/Player/observer.yml` | both false: it sets `checkCanInteract: false` and inherits `checkConsciousness: false` | `None` |

Notes:
- Today the entity-target check (`ValidateEntityTarget`) tests only `CheckCanInteract`. At parity, an
  action with `required: Conscious` does no target-interaction check, matching today's
  `checkCanInteract: false`.
- World targets check neither field today (AB-3).

## 6. Capability rollout

- [ ] Add `MobCapabilityProfilePrototype`, the `Dexterous` and `Brain` profiles, the component's
  `Profile` field, and `GetCapabilities` (spec §8).
- [ ] Add `Grant` and `Revoke` to `RefreshMobConditionsEvent`, and `Granted` and `Revoked` to the
  component (CAP-11, CAP-14).
- [ ] `Refresh` compares `Declared`, `Granted` and `Revoked`, and adds the component when any of them
  is non-empty (RF-3, RF-4).
- [ ] Add `MobCapabilitiesChangedEvent` (EV-2).
- [ ] The cache check covers `HasCapability` and the capability step of `CanPerform` (CC-1).
- [ ] Add `HasCapability` and move every reader of `CanComplexInteract` and
  `ComplexInteractionComponent` to it: `git grep -n "CanComplexInteract\|ComplexInteractionComponent" -- '*.cs'`.
- [ ] Every prototype with `ComplexInteraction` names the `Dexterous` profile instead:
  `git grep -n "type: ComplexInteraction" -- Resources/Prototypes`. `Body/species_appearance.yml`
  covers every humanoid. Then delete `ComplexInteractionComponent`.
- [ ] Zombies: `ZombieComponent` declares `Revoke(Complex)` instead of removing the marker. Revoke beats
  grant, so no item gives a zombie its hands back.
- [ ] Brains and borg brains name the `Brain` profile. Delete their Point handlers.
- [ ] The eligible-bit prototype test, for profiles and YAML grants and revokes (CAP-5).
- [ ] Cognizine, through `SharedMindSystem.MakeSentient`. **Blocked on Q16:** CAP-5 allows no `Speak`
  or `Emote` grant until Q16 is answered.
  - `MakeSentient` ensures a `SentienceComponent` (name open), adds the caller's grants to its
    `Grants` and refreshes. The component declares `Grant(Grants)` and is saved with the mob.
    `SpeechComponent` is still ensured, for its sounds and verb.
  - `MakeSentientEntityEffect`'s `allowSpeech` becomes `grants`, removed outright. The effect still
    removes the squeak accents and adds the ghost role.
  - A reagent that also grants `Complex` uplifts a monkey enough to use computers, with no code.
  - Every caller shares `SentienceComponent` and its grants: ghost roles, the admin verb, polymorph,
    zombification, the changeling slug. That is fine, because sentience is never taken away (CAP-13).
  - Conditions still apply, and a zombie's revokes still win.
  - The `Speak`/`Emote` grant depends on Q16. Under opt-out, the default already has them: a mouse,
    which has an enabled `SpeechComponent` and squeaks today, changes only its accent, and a creature
    whose profile lacks `Speak` gains it. Under opt-in, every creature that speaks today names a
    profile with `Speak`, mice included, and the grant matters only for creatures without one.

## 7. Open questions

Answered questions are folded into spec rules. Numbers are kept stable.

| # | Question | Notes |
|---|---|---|
| Q3 | Should `CheckIncapacitatedCuffEvent` become `HasCondition(target, Unconscious)`? | MobState and Sleeping map to `Unconscious`. Stamina crit (`SharedStaminaSystem`) has no condition: it applies stun, which #45296 deliberately excluded. Add stamcrit as an explicit source, or keep the event. Ask maintainers. |
| Q4 | May a cuffed mob struggle out of a container? | `EscapeInventorySystem` asks `Interact` on the container. `Ignoring = Restrained` is semantically right, since thrashing needs no hands, but it changes containment balance. Its own PR. |
| Q6 | Should dropping held items and stopping pulls hang off `HandsBlocked` and `PullBlocked`? | Later, as its own gameplay change. The drop is `DropHandItemsEvent` from `MobStateSystem.Down`, and it interacts with the virtual items of cuffs and vehicles. |
| Q7 | Are the §4 deltas acceptable? | Each is defensible, and each is a gameplay change. |
| Q11 | Should `BlockMovement` block `ChangeDirection`? | `Immobilized` doesn't: paralysed legs, chasms and tethers let you turn. Options: a new direction-lock condition, or `Incapacitated` if its users never need hands. Check its prototype users (`pda.yml`, `silicon.yml`, `mothershipcore.yml` and others) first. |
| Q16 | Are `Speak` and `Emote` opt-out (the default has them) or opt-in, like `Complex`? | Opt-out matches "everything unless blocked", but needs an audit of every mob without `SpeechComponent` or `EmotingComponent`. Until it is decided, `SpeechSystem` and `EmoteSystem` keep their checks, and `SpeakAttemptEvent` and `EmoteAttemptEvent` stay. |
| Q18 | How do sources on another entity work? | The vehicle can't run while its operator can't operate, and it can't run when `VehicleCanRunEvent` says so. `VehicleSystem.CanOperate` asks the operator's `CanInteract`, whitelist and hand count, so it can't be a refresh handler under RF-9. Decided with the vehicle migration. The vehicle keeps its `UpdateCanMoveEvent` handlers until then. |
| Q19 | Where does the replay spectator's lock go? | `ReplaySpectatorSystem` is client-only, and it puts its component on recorded entities, so recorded state overwrites any client-only declaration, and `Refresh` does nothing while state applies. Mapping it to `Incorporeal \| Immobilized` would also stop blocking Attack, Throw and ChangeDirection. |
| Q20 | What does `UnblockableSpeech` become? | Borgs have it. Its documented purpose is speech that speech-blocking effects can't block. A component may not add to a mask (IGN-8). One option: the speech-blocking sources skip entities that have it. |
| Q21 | What is the admin mute's condition called? | `AdminMuted` is a working name. |
| Q22 | Which item and implant sources count? | When the first one lands (PR-12): whether a source nested in a bag counts, and which inventory slots relay. |
| Q23 | Can a capability source word its own failure reason? | Today a missing capability reads `mob-capability-lacking-<member>` (RSN-6), whatever revoked it. |
| Q24 | Which reason wins when two sources word the same condition? | Two muting effects, such as a mute toxin and a mime's vow, both handle `GetMobConditionReasonEvent`. The first to set `Reason` wins, so the relay order decides. |
| Q25 | How are IGN-9 and CAP-5 enforced for masks, grants and revokes written in C#? | The prototype tests cover YAML only. `ZombieComponent`'s `Revoke(Complex)` is one such C# declaration. Options: rely on review; have `Effective` strip `AdminMuted` from every mask; have `Grant` and `Revoke` log an error for an ineligible bit. |
| Q26 | Is RL-9 (server-only rules for unpredicted actions) a general rule? | It exists for the DnaImprint NPC rule. |
| Q27 | Where do the types live once `ActionBlockerSystem` is gone? | The folder and namespace are still `ActionBlocker`. |
| Q28 | May the lazy add (RF-3) run on the client during prediction? | Today it runs on both sides. A predicted add of a networked component before the server's state arrives may not be safe. |

## 8. Tests

Pure-table tests are plain NUnit tests against the static functions, in
`Content.Tests/Shared/ActionBlocker/MobConditionTablesTest.cs`. Integration tests are in
`Content.IntegrationTests/Tests/ActionBlocker/MobConditionTest.cs`, on `InteractionTest`, with their
`[TestPrototypes]` inline. Every integration test runs with the cache check on (CC-3).

### 8.1 Rule → test index

| Rule | Test | Kind | Status |
|---|---|---|---|
| CAS-1 | `CascadeFixedPoint` | table | [x] |
| POL-1 | `UnconsciousBlocksEverything` (table and integration): every capability but `Down` and `Complex` | both | [x]; [ ] add `Equip` when it lands |
| POL-1 | `StunIsIncapacitatedNotUnconscious` | integration | [x] |
| INV-1 | `StandUpOnlyFromUnconscious`, to be replaced by `FlooredDoesNotBlockStandUp` before `Pinned` lands | table | [ ] |
| INV-1 | `FlooredStillAllowsStandingUp`: knocked down, `Stand` blocked, `StandUp` not, the mob recovers | integration | [ ] |
| INV-2 | `StunnedCanStillPoint` | table | [x] |
| INV-3 | `NoConditionBlocksComplex` | table | [x] |
| INV-4 | `SilencedAllowsEmote` | table | [ ] |
| INV-5 | `OnlyPostureLockedBlocksDown` | table | [ ] |
| INV-6 | `RestraintIsForgivable` (table): forgiving `Restrained` unblocks `Interact` | table | [x]; [ ] assert that `Restrained` alone blocks nothing |
| INV-7 | `ReviveAfterCritKnockdownStandsUp` | integration | [ ] |
| IGN-4 | `RestraintIsForgivable` (table and integration; the integration case uses cuffed and crit) | both | [x] |
| IGN-5 | `IgnoringDerivedBit`: invert it, so `Effective(Restrained, HandsBlocked)` still blocks `Interact` | table | [ ] |
| IGN-5 | `IgnoringStunKeepsCuffsAndCrit`: needs a hand, ignores stun; fails while cuffed and while crit | table | [ ] |
| IGN-5 | `UnconsciousIsForgivenOnlyByName`: for every mask without `Unconscious`, `GetBlocked(Effective(Unconscious, m)) == GetBlocked(Expand(Unconscious))` | table | [ ] |
| IGN-5 | `CritBuckledMayLieDownIgnoringPostureLock`: `Unconscious \| PostureLocked` with `Ignoring = PostureLocked` doesn't block `Down` | table | [ ] |
| IGN-9, REQ-4 | `MasksUseValidBits` | prototype | [ ] |
| CAP-5 | `ProfilesUseEligibleBits` | prototype | [ ] |
| IGN-3, CMP-2 | `ClientPredictionMatchesServer`; extend it to capabilities | integration | [x]; [ ] capabilities |
| REQ-1 | `RequirementIsASet` | integration | [x] |
| RSN-1, RSN-2 | `BlockingConditionsMultiCause` | table | [x] |
| RSN-4 | `ReasonPriorityCoversDeclarable` | table | [ ] |
| RSN-5 | `MimeReasonComesFromEffect` | integration | [x] |
| RSN-7, SPK-5 | `DeadMimeNoPopup`: needs the client's popup labels | integration | [ ] |
| RSN-7, CON-4 | `UnconsciousActivateNoPopup` | integration | [ ] |
| RSN-7 | `UnconsciousLackingCapabilityHasNoReason` | integration | [ ] |
| RSN-8 | `RuleKeepsFirstNonNullReason` | integration | [ ] |
| CON-1 | `SelfUncuff` | integration | [x] |
| CON-6 | `UnconsciousSelfUncuffNoPopup` | integration | [ ] |
| CON-3 | `ProgrammaticPickupNoPopup` | integration | [ ] |
| PR-5 | `SleepIsUnconsciousAndWakeStandsUp` | integration | [x] |
| PR-1, RF-4 | `WakeWhileCuffed`, `CuffCritReviveKeepsRestraint`: sources don't refcount | integration | [x] |
| SE-1, SE-2 | `MutedIsSilenced`, `DelayedMuteSilencesOnStart` | integration | [x] |
| SPK-2 | `ForceSayForgivesUnconsciousOnce`, `ForceSayOnFallingAsleep`: rewrite for the force-say message | integration | [x]; [ ] rewrite |
| SPK-2 | `ForceSayWithoutGrantIsOrdinarySpeech`: a stun force-say is sent with the normal checks | integration | [ ] |
| SPK-2 | `UnconsciousBlocksEverything` (integration): a query neither honours nor spends the grant | integration | [x] |
| SPK-3 | `LastWordsMask`, `LastWordsWhileAsleepInCrit` | integration | [x] |
| VC-5, SPK-3 | `AdminMuteIsUnforgivable`: Last Words fail while admin-muted | integration | [ ] |
| SPK-4 | `GaspWhileCrit` | integration | [ ] |
| CAP-1 | `ComplexIsACreatureProperty`; update it for profiles | integration | [x]; [ ] profiles |
| CAP-1, CAP-2 | `CapabilityFormula`: profile differences, revoke beats grant | table | [ ] |
| EV-2 | `ChangedEventsOnClientState` | integration | [ ] |
| RL-1 | `StationAiDoesNotUncancel`, until EVT-2 lands; after that the type enforces it and no test is needed | integration | [ ] |
| RF-8, Q18 | `VehicleReentrancy`: stunning an operator stops the vehicle in the same tick. Needed while the vehicle's `UpdateCanMoveEvent` handler runs inside RF-6. | integration | [ ] |
| MOV-1, MOV-2 | `MoverWithoutComponentStillChecksBody`: a mover without the bookkeeper's component can't move with a static body, and can once it changes | integration | [ ] |
| MOV-4 | `CanMoveUpdatedOnlyOnChange` | integration | [ ] |
| EC-1, EC-2 | `HasMobConditionCondition`: answers from the expanded set; the guidebook text uses member names | integration | [ ] |
| §3.2 | `VirtualItemVetoesInteraction` | integration | [x] |
| AB-2 | `ActionParity`: each §5 translation row validates in exactly the states it did, awake, stunned, cuffed and crit | integration | [ ] |
| CC-1 | `CacheConsistency`: stun, cuff, sleep, crit and ghost transitions, with no error logged | integration | [ ] |

### 8.2 Suites that must stay green

Cuffs, paper, ghost, UI, sleep, mob state, interaction, stun, buckle, pulling, hands, actions,
changeling, vehicle, speech and muting.
