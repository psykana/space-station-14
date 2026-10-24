# Mob capabilities: specification

This is the contract for how SS14 decides what an entity can do. It states the end state: what must
be true, and why, each once. It doesn't record history or status.

- [design.md](design.md) explains the choices and the rejected alternatives.
- [plan.md](plan.md) tracks the work left to reach this state. It is deleted when the work is done.
- [ui-tiers.md](ui-tiers.md) is a deferred phase built on this one.

Conventions:
- "MUST", "MUST NOT", "SHOULD" and "MAY" are used in the RFC 2119 sense.
- Every rule has a stable id, such as `PR-3`. Tests, the plan and code comments cite rules by id. An
  id is never reused for a different rule.
- Code is cited by file and symbol. Paths are relative to `Content.Shared/` unless they name another
  project.

**Open points.** These parts of the contract wait on a question in plan.md. Everything else is
settled. This list goes when plan.md does.
- TY-1: the folder and namespace (Q27).
- VC-5: the name `AdminMuted` (Q21).
- CAP-5: whether `Speak` and `Emote` join the eligible bits (Q16), and how grants and revokes written
  in C# are checked (Q25).
- IGN-9: how masks written in C# are checked (Q25).
- RL-9: whether server-only rules are a general rule (Q26).
- Sources that declare on another entity (Q18).
- RF-3: whether the lazy add runs on the client (Q28).

## 1. Model and scope

One system, the *bookkeeper* `MobCapabilitiesSystem`, answers every "can this entity do X?" question.

- **SC-1.** Sources declare what is true of an entity. Consumers ask the bookkeeper. No other system
  decides whether an action is blocked.
- **SC-2.** The bookkeeper owns four mechanisms, one per bucket:

  | Bucket | Question | Mechanism | Cached |
  |---|---|---|---|
  | 1. Conditions | What is wrong with the entity right now? | sources declare `MobCondition`s (§3, §6) | yes |
  | 2. Target vetoes | Can this target be acted on at all? | `MobActionTargetAttemptEvent`, raised on the target (§7) | no |
  | 3. Pair rules | Is this action allowed for this user against this target? | `MobActionAttemptEvent`, raised on the user (§7) | no |
  | 4. Capabilities | What can this kind of creature do at all? | a profile prototype, plus grants and revokes declared by sources (§8) | yes |

- **SC-3. Choosing a bucket.**
  - A block about the entity's own state is a condition. That holds whether or not a call site may
    forgive it: a condition that must never be forgiven is one that no mask names (IGN-9).
  - What the creature can or can't do at all is a capability, including a runtime change to it, such
    as a zombie losing its dexterity. Only the eligible bits (CAP-5) can be capabilities. No condition
    blocks `Complex` (INV-3), so even a temporary loss of it is a revoke from a removable source
    (CAP-13).
  - A block that depends on the target, or on this user acting on this target, is a target veto or a
    pair rule.
- **SC-4. Any entity.** The bookkeeper answers for any entity, mob or not: borgs, AI eyes and
  vehicles too. An entity without `MobCapabilitiesComponent` has no conditions and the default
  capabilities (CAP-7), and its movement is still computed (MOV-1). Buckets 2 and 3 still apply to
  it.
- **SC-5. No other blocking events.** `ActionBlockerSystem` and the typed attempt events
  (`InteractionAttemptEvent`, `UseAttemptEvent`, `UpdateCanMoveEvent`, `StandAttemptEvent`,
  `GettingPickedUpAttemptEvent` and the rest) don't exist. No system other than the bookkeeper
  raises an event to decide whether an action is blocked. The bookkeeper raises the events in §4,
  `CanMoveUpdatedEvent` (MOV-4) and the container exception (RL-8), and nothing else.
- **SC-6. Domain rules stay in their domain.** Buckling, slipping, seeing, climbing and stripping
  keep their own events. They are not blockers. A handler of such an event that needs "can the user
  act?" is a consumer and asks the bookkeeper.
- **SC-7. Consumers never re-implement blocking.** No consumer decides whether someone can act by
  checking `HasComp<StunnedComponent>`, `CuffableComponent.CanStillInteract` or `MobState`.
- **SC-8. No condition affects seeing.** Crit and death don't blind. Seeing stays a domain rule
  (`CanSeeAttemptEvent`), and Sleeping's own handler of it stays.

**Names.** The bookkeeper is `MobCapabilitiesSystem` and its component is `MobCapabilitiesComponent`.
The vocabulary of conditions, and the events that collect and report it, keep condition names:
`MobCondition`, `RefreshMobConditionsEvent`, `MobConditionsChangedEvent`.

**Out of scope:**
- Perception and chat filtering (design §9).
- UI access tiers ([ui-tiers.md](ui-tiers.md)). Until that phase, UIs ask for `Interact` and
  `Complex` like any other consumer.
- Side effects that hang off a condition, such as dropping held items on `HandsBlocked` (plan, open
  questions).
- Sources that declare on an entity other than their own, such as a vehicle that can't run while its
  operator can't operate (plan, open questions).

## 2. Vocabularies

Both enums live in `ActionBlocker/MobCondition.cs`.

- **VC-1.** `MobCondition` is a `[Flags, Serializable, NetSerializable] enum : ushort` with `None = 0`.
  Its 16 bits hold every member below and the UI phase's `UiBlocked`; widen it when a 17th is needed.
- **VA-1.** `MobCapability` is a `[Flags, Serializable, NetSerializable] enum : uint` with
  `None = 0`.
- **VC-2. Both vocabularies are closed.** A member exists only because something needs it, and it is
  added together with that thing:
  - a condition exists because a source declares it or the cascade (§3) derives it. A member MAY
    exist ahead of its source while that source's migration is scheduled;
  - a capability exists because a consumer asks for it.

  Creature traits in general, such as literacy, stay their own components.
- **VC-3. Adding a member.** A member listed in §2.1 or §2.2 takes the bit shown there; any other new
  member takes the next free bit. It brings, in the same change:
  - a policy row (POL-1), and a cascade row if something derives it (CAS-1);
  - if it is a declarable condition, a place in the reason priority (RSN-4);
  - its locale keys (RSN-9);
  - if it is a capability that profiles and sources may change, a place in the eligible list (CAP-5);
  - if no mask may forgive it, a place in IGN-9;
  - a test for any invariant it touches (§3.3).

### 2.1 `MobCondition`

| Member | Bit | Meaning | Kind | Declared by |
|---|---|---|---|---|
| `Unconscious` | 0 | The mind is unavailable: asleep, critical, dead. | declarable | MobState (Critical, Dead), Sleeping |
| `Incapacitated` | 1 | Generally unable to act. No cause is implied. | declarable; also derived | Stun, AdminFrozen, the ventriloquist puppet, the kitchen-spike hook |
| `Restrained` | 2 | Bound but awake. | declarable | Cuffs |
| `HandsBlocked` | 3 | Can't use manipulators. | declarable; also derived | being disposed, `BlockMovement` |
| `Immobilized` | 4 | Can't move voluntarily. | declarable; also derived | Stun, AdminFrozen, the puppet, paralysed legs, a chasm fall, a tether, buckle, climbing, piloting, cuffs while pulled, `BlockMovement` |
| `Floored` | 5 | Can't be upright. | declarable; also derived | Knockdown |
| `PullBlocked` | 6 | Can't pull. | derived only | none |
| `Incorporeal` | 7 | No physical presence, like a spectating ghost. | declarable | Ghost |
| `Silenced` | 8 | Can't speak: muted, a mime's vow. | declarable | the muted status effect |
| `Pinned` | 9 | Can't try to rise. | declarable | the knockdown status effect, Worm |
| `PostureLocked` | 10 | Can't change posture. | declarable | buckle (while buckled), the cryo-pod occupant |
| `AdminMuted` | 11 | Muted by an admin. Never in a mask. | declarable | AdminFrozen, while `Muted` |

The "Declared by" column is informative. It lists the sources of the end state and doesn't restrict
who may declare a member.

- **VC-4.** Only a declarable member may be passed to `Add`. `PullBlocked` MUST only be derived. It
  exists so that `Pull` has one policy row, and so that `HasCondition` and a future stop-pull side
  effect can name it. No mask can forgive it (IGN-9).
- **VC-5.** `AdminMuted` is never forgiven: no mask names it (IGN-9).

### 2.2 `MobCapability`

| Member | Bit | Meaning | Notes |
|---|---|---|---|
| `Interact` | 0 | Hands-on interaction with a target, or with nothing. | the default `Required` |
| `Conscious` | 1 | An act that needs a mind but not hands. | |
| `UseHeld` | 2 | Use the held item. | |
| `Pickup` | 3 | Pick up the target. | |
| `Drop` | 4 | Drop a held item. | |
| `Attack` | 5 | Attack, armed or not. | carries a payload (RL-7) |
| `Throw` | 6 | Throw the used item. | |
| `Speak` | 7 | Say or whisper. | |
| `Emote` | 8 | Emote. | |
| `Point` | 9 | Point at something. | capability-eligible |
| `Move` | 10 | Move voluntarily. | answered only by the movement cache (MOV-1). Never in `Required`, a profile or a source. |
| `Stand` | 11 | Be upright at all. | |
| `Pull` | 12 | Start pulling. | |
| `ChangeDirection` | 13 | Turn. | |
| `StandUp` | 14 | Try to rise from a knockdown. | |
| `Down` | 15 | Lie down. | crit and death lie the mob down (INV-5) |
| `Complex` | 16 | Have the hands and wits for complex interaction. | capability-only: no condition blocks it (INV-3); capability-eligible |
| `Equip` | 17 | Equip or unequip something on yourself. | self only (user == target), so a body's conditions don't stop a third party stripping it |

- **VA-2.** `Stand` asks whether the entity may be upright at all. `StandUp` asks whether it may try
  to rise. They are separate so that a condition can floor a mob without pinning it (INV-1).

## 3. Cascade, policy and invariants

### 3.1 Cascade: `MobCapabilitiesSystem.Expand`

- **CAS-1.** The cascade is applied until nothing changes, so the order of its rules doesn't matter.

| If present | Add |
|---|---|
| `Unconscious` | `Incapacitated \| Immobilized \| Floored \| HandsBlocked` |
| `Restrained` | `HandsBlocked` |
| `Incapacitated` or `HandsBlocked` | `PullBlocked` |

### 3.2 Policy: `MobCapabilitiesSystem.GetBlocked`

- **POL-1.** `GetBlocked` is the single place where a condition blocks a capability.

| Condition | Blocks |
|---|---|
| `Unconscious` | `Conscious \| Speak \| Emote \| Point \| StandUp` |
| `Incapacitated` | `Interact \| UseHeld \| Pickup \| Drop \| Attack \| Throw \| ChangeDirection \| Equip` |
| `HandsBlocked` | `Interact \| UseHeld \| Pickup \| Drop \| Attack \| Equip` |
| `Incorporeal` | `Interact \| UseHeld \| Pickup \| Drop \| Emote` |
| `Immobilized` | `Move` |
| `Floored` | `Stand` |
| `PullBlocked` | `Pull` |
| `Silenced` | `Speak` |
| `Pinned` | `StandUp` |
| `PostureLocked` | `Stand \| Down` |
| `AdminMuted` | `Speak \| Emote` |
| `Restrained` | nothing directly |

### 3.3 Invariants

The tables MUST preserve these. Each has a test (plan, tests).

- **INV-1. `Floored` MUST NOT block `StandUp`.** Knockdown asks `StandUp` to decide whether a mob may
  recover, so a floored mob that couldn't attempt to rise would stay down forever.
- **INV-2. `Incapacitated` MUST NOT block `Point`.** A stunned but awake mob can still gesture.
- **INV-3. No condition blocks `Complex`.** It is a capability (§8).
- **INV-4. `Silenced` MUST NOT block `Emote`.** Mimes emote. Muffling vocal emotes is not a gate.
- **INV-5. `Unconscious` MUST NOT block `Down`.** Crit and death lie the mob down. Only
  `PostureLocked` blocks `Down`.
- **INV-6. `Restrained` blocks nothing directly.** It acts only through `HandsBlocked`, so a call site
  can forgive restraint alone.
- **INV-7.** `Unconscious` blocks `StandUp`. So a mob knocked down while crit keeps its knockdown
  until it is revived, and then recovers through the normal knockdown path.

## 4. Types

- **TY-1.** The bookkeeper's types live in `Content.Shared/ActionBlocker/`, namespace
  `Content.Shared.ActionBlocker`.

### 4.1 `MobCapabilitiesComponent`

```csharp
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
[Access(typeof(MobCapabilitiesSystem))]
public sealed partial class MobCapabilitiesComponent : Component
{
    [DataField, AutoNetworkedField] public ProtoId<MobCapabilityProfilePrototype>? Profile;
    [ViewVariables, AutoNetworkedField] public MobCondition Declared;  // union of Add, before the cascade
    [ViewVariables, AutoNetworkedField] public MobCapability Granted;  // union of Grant
    [ViewVariables, AutoNetworkedField] public MobCapability Revoked;  // union of Revoke
    [ViewVariables] public MobCondition Conditions;                    // Expand(Declared)
    [ViewVariables] public MobCapability Blocked;                      // GetBlocked(Conditions)
    [ViewVariables] public MobCapability Capabilities;                 // CAP-2
}
```

- **CMP-1.** `Profile` is the only data field. `Declared`, `Granted` and `Revoked` are networked but
  not saved: the sources that declare them are saved, and the startup refresh (PRES-2) rebuilds them.
- **CMP-2.** The derived fields are never networked. The bookkeeper recomputes them in
  `AfterAutoHandleStateEvent`, so they can't drift from what the server sent.
- **CMP-3.** The component holds nothing beyond the profile, the three declarations and what is
  derived from them, because it sits on every mob.
- **CMP-4.** Its YAML type is `MobCapabilities`.

### 4.2 Events

```csharp
/// Raised on the entity to rebuild its declarations. Handlers call Add, Grant and Revoke.
[ByRefEvent]
public record struct RefreshMobConditionsEvent
{
    public MobCondition Conditions { get; private set; }
    public MobCapability Granted { get; private set; }
    public MobCapability Revoked { get; private set; }
    public void Add(MobCondition conditions);
    public void Grant(MobCapability capabilities);
    public void Revoke(MobCapability capabilities);
}

[ByRefEvent]
public readonly record struct MobConditionsChangedEvent(EntityUid Uid, MobCondition Old, MobCondition New)
{
    public MobCondition Added => New & ~Old;
    public MobCondition Removed => Old & ~New;
}

[ByRefEvent]
public readonly record struct MobCapabilitiesChangedEvent(EntityUid Uid, MobCapability Old, MobCapability New)
{
    public MobCapability Added => New & ~Old;
    public MobCapability Removed => Old & ~New;
}

/// Raised on the entity when a failure is explained by Condition, so whatever declared it can word it.
[ByRefEvent]
public record struct GetMobConditionReasonEvent(MobCondition Condition, MobCapability Required)
{
    public LocId? Reason;
}
```

- **EV-1.** `RefreshMobConditionsEvent` is a by-ref struct, so a refresh allocates nothing. Only
  `Refresh` (§5.3) and the cache check (§5.8) raise it.
- **EV-2.** `MobConditionsChangedEvent` is raised when `Conditions` changed, and
  `MobCapabilitiesChangedEvent` when `Capabilities` changed. Each is raised by ref on the entity and
  broadcast, after the new state is committed.
  - They are also raised on the client when server state changes the derived set. Their handlers
    then run while `ApplyingState`, so a `Refresh` from them does nothing.
- **EV-3.** Subscribe to the changed events directed on your own component, or to the broadcast. A
  system MUST NOT subscribe to them directed on `MobCapabilitiesComponent`: only one system may hold
  that subscription.
- **EV-4.** `GetMobConditionReasonEvent` is raised only on failure, after the condition has been
  picked (RSN-4). Status effects relay it (SE-7). A handler MUST leave a `Reason` that is already set.

### 4.3 `InteractionRequirements`

```csharp
[DataDefinition, Serializable, NetSerializable]
public partial record struct InteractionRequirements()
{
    [DataField] public MobCapability Required = MobCapability.Interact; // every capability the act needs
    [DataField] public MobCondition Ignoring = MobCondition.None;       // declared conditions forgiven (§5.4)
}
```

YAML: `requires: { required: Interact, ignoring: Restrained }`.

- **REQ-1.** `Required` is a mask. The act is blocked if any capability in it is missing (§8) or
  blocked by a condition (§3).
- **REQ-2.** `default(InteractionRequirements)` skips the field initializers, so its `Required` is
  `None` and it requires nothing. Code MUST build requirements with `new()`, an initializer or a
  preset, and a field of this type MUST have an initializer.
- **REQ-3.** The type provides named presets for the common requirements, at least `Interact` and
  `Conscious`.
- **REQ-4.** `Required` MUST NOT contain `Move` (MOV-5). `Ignoring` follows IGN-9.
- **REQ-5.** The name `ActionRequirements` MUST NOT be used. It collides with
  `ActionRequirementsComponent` (#46029), a different feature (design §7).

### 4.4 Rule events

```csharp
[ByRefEvent]
public record struct MobActionAttemptEvent(EntityUid User, MobCapability Required, EntityUid? Target,
    EntityUid? Used, MobActionPayload Payload)
{
    public bool Cancelled { get; private set; }
    public string? Reason { get; private set; }  // localized
    public void Cancel(string? reason = null);    // keeps the first reason
}

[ByRefEvent]
public record struct MobActionTargetAttemptEvent(EntityUid User, MobCapability Required, EntityUid? Used,
    MobActionPayload Payload)
{
    public bool Cancelled { get; private set; }
    public string? Reason { get; private set; }
    public void Cancel(string? reason = null);
}

/// Per-capability data. A field is meaningful only when its capability is in Required.
public readonly record struct MobActionPayload(
    Entity<MeleeWeaponComponent>? Weapon = null,  // Attack
    bool Disarm = false);                         // Attack
```

- **EVT-1.** "Mob" in both names refers to the user. The target can be any entity.
- **EVT-2.** A handler can't un-cancel: `Cancelled` has no public setter. The rules for handlers are
  in §7.

## 5. The bookkeeper

### 5.1 Public API

```csharp
public sealed partial class MobCapabilitiesSystem : EntitySystem
{
    // Sources
    public void Refresh(Entity<MobCapabilitiesComponent?> ent);

    // Consumers
    public bool CanPerform(EntityUid user, in InteractionRequirements req,
        EntityUid? target = null, EntityUid? used = null);
    public bool CanPerform(EntityUid user, in InteractionRequirements req,
        EntityUid? target, EntityUid? used, out string? reason);
    public bool CanPerform(EntityUid user, in InteractionRequirements req,
        EntityUid? target, EntityUid? used, in MobActionPayload payload, out string? reason);
    public bool CanMove(EntityUid uid, InputMoverComponent? mover = null);

    // Cached queries
    public bool HasCondition(Entity<MobCapabilitiesComponent?> ent, MobCondition condition);
    public bool HasCapability(Entity<MobCapabilitiesComponent?> ent, MobCapability capabilities);
    public bool IsBlocked(Entity<MobCapabilitiesComponent?> ent, MobCapability required);
    public bool IsBlocked(Entity<MobCapabilitiesComponent?> ent, MobCapability required, MobCondition ignoring);
    public MobCondition GetBlockingConditions(Entity<MobCapabilitiesComponent?> ent, MobCapability required,
        MobCondition ignoring = MobCondition.None);
    public bool TryGetBlockedReason(Entity<MobCapabilitiesComponent?> ent, MobCapability required,
        out string reason, MobCondition ignoring = MobCondition.None);

    // Pure
    public static MobCondition Expand(MobCondition conditions);                       // §3.1
    public static MobCapability GetBlocked(MobCondition conditions);                  // §3.2
    public static MobCondition Effective(MobCondition declared, MobCondition ignoring); // §5.4
    public static MobCapability GetCapabilities(MobCapabilityProfilePrototype? profile,
        MobCapability granted, MobCapability revoked);                                // CAP-2
    public static MobCondition GetBlockingConditions(MobCondition declared, MobCapability required,
        MobCondition ignoring = MobCondition.None);                                   // RSN-1
    public static MobCondition PickReason(MobCondition conditions);                   // RSN-4
    public static string GetLocalizedName(MobCondition conditions);                   // EC-2
}
```

- **API-1.** On an entity without the component, `IsBlocked` and `HasCondition` return false, and
  `HasCapability` answers from the default (CAP-7). `CanMove` returns false for an entity without
  `InputMoverComponent`.
- **API-2.** `HasCondition` is true if any bit of `condition` is in the expanded `Conditions`.
- **API-3.** `HasCapability` is true only if the entity has every capability in the mask. It reads
  the capability layer only and ignores conditions.
- **API-4.** The static functions are pure, so the tables can be tested without a server.
- **API-5.** Nothing in this API has side effects: no popups, nothing spent, nothing written, apart
  from `Refresh` committing the cache.

### 5.2 `CanPerform`

- **CP-1. Order.** The checks run in this order. The first failure wins and supplies `reason`.
  1. **Validation.** If `Required` contains `Move`, log an error and fail with no reason (MOV-5).
  2. **Capabilities.** Fail if `(Required & ~Capabilities) != 0`. The reason names the lowest missing
     bit (RSN-6).
  3. **Conditions.** Fail if `IsBlocked(user, Required, Ignoring)`, read from the cache. The reason
     comes from §5.7.
  4. **Built-in container rule** (RL-8), when `Required` contains `Attack`. It fails with no reason.
  5. **Pair rule.** Raise `MobActionAttemptEvent` on the user.
  6. **Target veto.** If `target` is set and isn't the user, raise `MobActionTargetAttemptEvent` on
     the target. Separately, whether or not `target` is set: if `Required` contains `UseHeld` or
     `Throw` and `used` is set, raise it on `used`.

  RSN-7 overrides the reason of every step.
- **CP-2.** Capabilities come first because a missing capability is more basic than any condition:
  a mouse is told it lacks the dexterity, not that it is stunned.
- **CP-3.** The overloads without a payload pass an empty `MobActionPayload`.

### 5.3 `Refresh`

Steps, in order:

1. **RF-1.** If `IGameTiming.ApplyingState`, or the entity is `TerminatingOrDeleted`, return. The
   declarations arrive with the server state.
2. **RF-2.** Poll: raise `RefreshMobConditionsEvent` by ref on the entity.
3. **RF-3.** If the entity has no component and any of the polled conditions, grants or revokes is
   non-empty, add the component and return: its startup refreshes and does steps 4–7. If they are
   all empty, go to step 6.
4. **RF-4.** If the polled `Declared`, `Granted` and `Revoked` all equal the stored ones, go to
   step 6. Compare the declarations, not the derived sets: two declared sets can expand alike but
   forgive differently (`Restrained` alone versus `Restrained | HandsBlocked`).
5. **RF-5.** Commit: store the three declarations, derive `Conditions`, `Blocked` and
   `Capabilities`, and `Dirty` the component.
6. **RF-6.** If the entity has an `InputMoverComponent`, recompute the movement answer (MOV-1). If it
   changed, write it and raise `CanMoveUpdatedEvent`.
7. **RF-7.** Only if this call committed (step 5): if `Conditions` changed, raise
   `MobConditionsChangedEvent`; if `Capabilities` changed, raise `MobCapabilitiesChangedEvent`. Both
   compare against the sets from before this call's commit.

Invariants:
- **RF-8.** The commit happens before steps 6 and 7, because their handlers read the cache.
- **RF-9. Refresh handlers.** A `RefreshMobConditionsEvent` handler may read components (`TryComp`
  and the like). On the event, it MUST only call `Add`, `Grant` and `Revoke`. It MUST NOT call
  `Refresh`, `CanPerform`, `CanMove` or any other `CanX`, and MUST NOT raise events, except a relay
  that re-raises this event to its sources and does nothing else (SE-7, PR-12).
- **RF-10. Re-entry** is a `Refresh` of an entity while that entity is being polled (step 2). It logs
  an error and skips the poll. The guard is per entity: polling A never blocks a refresh of B.
- **RF-11.** Handlers of the events raised after the commit (steps 6 and 7) may query the bookkeeper
  and may refresh other entities. A refresh of the same entity from them is not re-entry, because
  its poll has finished.

### 5.4 Ignore masks

```csharp
public static MobCondition Effective(MobCondition declared, MobCondition ignoring)
    => Expand(declared & ~ignoring);
```

- **IGN-1.** A mask forgives declared conditions only. It is applied before the cascade and never
  after it.
- **IGN-2.** With a mask, `IsBlocked` is `(GetBlocked(Effective(Declared, ignoring)) & required) != 0`.
  Without one, it reads the cached `Blocked`.
- **IGN-3.** A mask reads only the stored `Declared` and never polls. So it costs a few bit
  operations, and the client gets the same answer as the server.
- **IGN-4.** Forgiving a declared condition also forgives what it alone implies. Ignoring
  `Restrained` removes the `HandsBlocked` that restraint derives.
- **IGN-5.** A mask never forgives a condition that another declared condition implies:
  - ignoring `HandsBlocked` doesn't free a cuffed mob, because `Restrained` re-derives it;
  - ignoring `Incapacitated` doesn't free a crit mob, because `Unconscious` re-derives it;
  - so a call site that needs a hand and ignores a stun still fails while cuffed, and while crit;
  - what `Unconscious` implies stays blocked unless the mask names `Unconscious` itself. Blocks from
    other declared conditions follow their own masks: a crit, buckled mob with
    `Ignoring = PostureLocked` may lie down, because only `PostureLocked` blocked `Down` (INV-5).
- **IGN-6.** Masks name conditions, not sources. A cause that must be forgiven on its own gets its
  own condition.
- **IGN-7.** Masks forgive conditions only. A missing capability, a pair rule and a target veto
  can't be forgiven.
- **IGN-8. Only an act declares what it ignores.** The mask belongs to the act: it is written at the
  call site that performs the act, or it is data on the act's own definition, such as an action's
  `Requires` (AB-1), an auto-emote prototype (SPK-4) or a UI's `ReadOnlyConditions` (ui-tiers.md). No
  component on the actor, and no event handler or other hook, adds to another act's mask.
- **IGN-9.** A mask MUST NOT name a derived-only condition (`PullBlocked`), which is never declared,
  so naming it does nothing. A mask MUST NOT name `AdminMuted`, which is never forgiven. A prototype
  test checks every mask in YAML.

### 5.5 Component presence

- **PRES-1.** Sources never add the component. `Refresh` adds it lazily (RF-3).
- **PRES-2.** The component's `ComponentStartup` calls `Refresh`. That covers mobs spawned already
  dead or cuffed.
- **PRES-3.** `- type: MobCapabilities` sits on `MobDamageable` in
  `Resources/Prototypes/Entities/Mobs/base.yml`, so ordinary mobs have it from spawn. Anything else
  gets it lazily.

### 5.6 Movement

- **MOV-1.** The bookkeeper owns `InputMoverComponent.CanMove` for every entity with an
  `InputMoverComponent`, with or without `MobCapabilitiesComponent`. It is true when the entity's
  conditions don't block `Move` (an entity without the component has none) and it has a physics body
  that isn't static.
- **MOV-2. When it is computed.** On every `Refresh` (RF-6). The movement system's existing handlers
  on `InputMoverComponent`, for `ComponentStartup` and `PhysicsBodyTypeChangedEvent`
  (`SharedMoverController`), call `Refresh`, so a new mover and a body-type change are covered
  without a new subscription (PR-4).
- **MOV-3. Networking.** `CanMove` stays networked in `InputMoverComponentState`. The bookkeeper
  `Dirty`s the mover when it writes a new value. There is one writer at a time: `Refresh` (RF-6) on
  the server and during client prediction, and the mover's state handling
  (`SharedMoverController.OnMoverHandleState`) while the client applies server state. The
  bookkeeper's own state handling doesn't write `CanMove`.
- **MOV-4.** `CanMoveUpdatedEvent` is raised only when the value changes, by whichever of the two
  wrote it.
- **MOV-5.** `CanPerform` doesn't answer `Move`. It logs an error and fails. Ask `CanMove`.

### 5.7 Failure reasons

- **RSN-1.** `GetBlockingConditions` returns every declared condition that alone blocks one of the
  required capabilities, once `ignoring` is forgiven:
  `for each declared bit b: if ((GetBlocked(Effective(b, ignoring)) & required) != 0) result |= b`.
- **RSN-2.** It is not "the conditions whose removal would unblock the act". With several causes,
  removing any one unblocks nothing, so that set is empty exactly when an explanation is most needed.
- **RSN-3.** It iterates the declarable conditions only.
- **RSN-4.** `PickReason` picks one bit by this priority: `Unconscious`, `Incorporeal`,
  `Incapacitated`, `Restrained`, `HandsBlocked`, `Immobilized`, `Floored`, `Pinned`,
  `PostureLocked`, `AdminMuted`, `Silenced`. The list MUST contain every declarable member.
- **RSN-5.** For the picked bit, the bookkeeper raises `GetMobConditionReasonEvent` (EV-4). If no
  handler sets `Reason`, the reason is the key `mob-condition-blocked-<member>`.
- **RSN-6.** For a missing capability, the reason is the key `mob-capability-lacking-<member>`.
- **RSN-7.** `CanPerform`'s reason is null, whichever step failed, while the user is still
  `Unconscious` after the mask. An unconscious mind gets no feedback, not even about a missing
  capability.
- **RSN-8.** For a rule event, the reason is the first non-null one passed to `Cancel`.
- **RSN-9.** Locale keys live in `Resources/Locale/en-US/mob-conditions/mob-conditions.ftl`: a
  `mob-condition-name-<member>` for every condition, a `mob-condition-blocked-<member>` for every
  declarable condition, and a `mob-capability-lacking-<member>` for every capability-eligible bit
  (CAP-5). `<member>` is the member name in lower case.

### 5.8 Cache verification

Every answer comes from a cache, so a missed `Refresh` silently leaves an entity wrongly blocked or
wrongly free.

- **CC-1.** When the cvar `debug.mob_conditions_verify` is on, every cached read re-polls the
  sources before its value is used: `IsBlocked`, `HasCondition`, `HasCapability` and the capability
  step of `CanPerform`. A poll that differs from the stored `Declared`, `Granted` or `Revoked` logs an
  error. `CanMove` likewise logs an error when its stored value differs from a fresh MOV-1
  computation.
- **CC-2.** The check is skipped on the client, while `ApplyingState`, during a poll of the same
  entity, and on a terminating entity.
- **CC-3.** The integration-test pool turns the cvar on, so every suite doubles as a staleness test.

## 6. Sources

A source is whatever declares conditions, grants or revokes on an entity: a component, a status
effect, and later an item or an implant.

- **PR-1.** Subscribe to `RefreshMobConditionsEvent` on the source's own component. Declare while the
  source applies, and do nothing else (RF-9).
- **PR-2.** A source whose component can be removed at runtime guards with
  `LifeStage <= ComponentLifeStage.Running`. The component is still subscribed while it is being
  removed, and the removal refresh must not re-declare.
- **PR-3.** Call `Refresh` when the source starts applying, when it is removed, and whenever its own
  state flips.
- **PR-4.** Refresh from lifecycle hooks on the source's own component (`ComponentInit`,
  `ComponentStartup`, `ComponentShutdown` or `ComponentRemove`) and from the code paths that change
  its state. Reuse a hook the system already has. Don't add a lifecycle subscription to a widely used
  component, such as `MobStateComponent` or `CuffableComponent`, just for the bookkeeper: the engine
  allows one directed subscription per component and event, a duplicate crashes at init, and a new
  hook can collide with one a fork already has.
- **PR-5. Refresh first.** In the code path that changes a source's state, `Refresh` MUST run before
  any further event is raised and before any query. Every check reads the cache, so anything that
  runs between the change and the refresh sees stale blocking.
- **PR-6. No unrefreshed writes.** A field the handler reads MUST be written only through a setter
  that refreshes, and restricted with `[Access]`. A VV edit that bypasses the setter is caught by
  CC-1 in tests, not in production.
- **PR-7.** Removing a source component by hand (VV, `rmcomp`) without a removal hook leaves the
  cache stale until the next refresh. That is accepted.
- **PR-8. Shared and client-derivable.** The source's system lives in `Content.Shared`, and every
  field its handler reads is networked or derivable on the client from networked state. A predicted
  `Refresh` on the client polls only what the client can see, so a server-only source would vanish
  from the client's declarations until the next server state.
- **PR-9.** Something that can't meet PR-8 is not a source. If it describes what the creature is, it
  is a profile (§8). If it is evaluated at query time, it is a pair rule or a target veto (§7), which
  may be server-only only for actions the client never predicts (RL-9). Otherwise it is not a gate.
- **PR-10.** A source MUST NOT also block a capability it already blocks through a condition, for
  example with a rule event. That makes the condition unforgivable.
- **PR-11.** These rules apply unchanged to sources that grant and revoke.
- **PR-14.** No source or consumer relies on `MobStateChangedEvent` as a client signal: on the client
  it is raised only through predicted damage thresholds. Code that needs conditions on the client uses
  the changed events (EV-2).

### 6.1 Status effects

A status effect lives on its own entity, so it declares through the relayed event.

- **SE-1.** Subscribe to `StatusEffectRelayedEvent<RefreshMobConditionsEvent>` on the effect's
  component, and declare while `StatusEffectComponent.Applied` is set. `Applied` is the effect's
  guard in place of PR-2.
- **SE-2.** A delayed effect declares only once it starts. `StatusEffectAppliedEvent` is raised when
  the effect starts, not when it is added, so SE-4's refresh happens then.
- **SE-3.** `Args` is a property of the relay wrapper. Add to a copy and assign it back, or the add is
  lost. A handler of the relayed `GetMobConditionReasonEvent` writes through a copy the same way, and
  leaves a reason that is already set (EV-4).
- **SE-4.** Refresh `args.Target` on `StatusEffectAppliedEvent` and on `StatusEffectRemovedEvent`.
  The removed event is raised after the effect has left the entity's container, so that refresh no
  longer sees it.
- **SE-5.** `StatusEffectsSystem` sets `Applied` before it raises `StatusEffectAppliedEvent`, so the
  refresh in that event sees the effect as applied.
- **SE-6.** `Applied` is computed on the client from the networked start time, so PR-8 holds.
- **SE-7.** `StatusEffectsSystem` relays `RefreshMobConditionsEvent` and `GetMobConditionReasonEvent`
  with `RefRelayStatusEffectEvent` (`StatusEffectNew/StatusEffectSystem.Relay.cs`).
- **SE-8.** The effect's own refresh MUST run before any other `StatusEffectAppliedEvent` handler on
  the effect entity that queries the bookkeeper. When another system subscribes to that event on the
  same effect, order the refresh with `before:`.

### 6.2 Sources on items and implants

- **PR-12.** The relays that carry `RefreshMobConditionsEvent` and `GetMobConditionReasonEvent`
  through worn items, held items and implants are added together with the first source of that kind.
- **PR-13.** The system that moves a source (equip and unequip, pickup and drop, implant and extract)
  refreshes the holder in that code path, following PR-5.

## 7. Pair rules and target vetoes

- **RL-1. Cancel only.** A handler calls `Cancel` or does nothing. The type enforces it (EVT-2), so
  the outcome doesn't depend on handler order.
- **RL-2.** A handler tests the `Required` bits it cares about. One subscription covers every
  capability the rule cares about.
- **RL-3. Subscribe on your rule's own component.** The engine allows one directed subscription per
  component and event, so a rule subscribed on a widely shared component, such as
  `MobStateComponent`, locks every other rule out of it.
- **RL-4. No popups in handlers.** A handler passes its explanation to `Cancel`. The consumer decides
  whether to show it, and owns any throttling.
- **RL-5. No side effects.** Rule events are for asking, not reacting. A handler writes nothing.
- **RL-6.** Rules and vetoes are evaluated at query time and not cached. Measure before optimizing.
- **RL-7. Payload.** Per-capability data travels in `Payload`. A handler reads a field only when its
  capability is in `Required`. `Attack` carries the weapon and the disarm flag.
- **RL-8. The built-in container rule.** When `Required` contains `Attack`:
  - a target inside a container can't be attacked;
  - a user inside a container can attack only the owner of its outermost container, unless
    `CanAttackFromContainerEvent` allows it.

  That event is the rule's only exception hook. It allows and never blocks.
- **RL-9.** A rule MAY be server-only when the client never predicts the action, as for NPCs.

## 8. Capabilities

Two layers answer "can it?":
- **capabilities:** what this kind of creature can do at all. A mouse can't use a computer, and a
  brain can't point;
- **conditions** (§3): what is stopping it right now.

```
ProfileSet   = (Default | profile.Innate) & ~profile.Lacking
Capabilities = (ProfileSet | Granted) & ~Revoked
CanPerform   requires (Required & ~Capabilities) == 0
             and (GetBlocked(Effective(Declared, Ignoring)) & Required) == 0
             and no rule or veto cancels (§7)
```

- **CAP-1.** That formula is the contract. `GetCapabilities` computes `Capabilities`.
- **CAP-2. Revoke beats grant.**
- **CAP-3. Grants don't override conditions.** A grant gives a creature something its kind lacks.
  It doesn't uncuff anyone.
- **CAP-4.** Masks forgive conditions only (IGN-7).
- **CAP-5. Eligible bits.** Only `Complex` and `Point` may appear in a profile's `Innate` or
  `Lacking`, or in a `Grant` or `Revoke`. `Move` is never eligible (MOV-1). A prototype test checks
  every profile and every grant or revoke written in YAML.

**Profiles:**

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
  id: Dexterous
  innate: [Complex]

- type: mobCapabilityProfile
  id: Brain
  lacking: [Point]

# An entity names its profile on its component. A child prototype sets it the same way.
- type: entity
  id: ExampleDexterousMob
  components:
  - type: MobCapabilities
    profile: Dexterous
```

- **CAP-6.** The baseline is a profile prototype, so nothing can change it at runtime.
- **CAP-7. The built-in default** is every capability except `Complex`: everything is allowed unless
  something blocks it, and `Complex` is opt-in. A component without a profile, and an entity without
  the component, both get the default.
- **CAP-8.** Profiles are differences from the default, so a capability added later reaches every
  profile without an edit.
- **CAP-9.** The profile id never changes at runtime. It travels with the initial state and is saved
  as an id. Writes are restricted with `[Access]`, and there is no setter.
- **CAP-10.** Profiles are prototype data, so the client always knows them.

**Sources of change:**

- **CAP-11.** Every runtime change is declared by a source on the entity through `Grant` or
  `Revoke`, following §6.
- **CAP-12.** A permanent change is a source that stays, saved with the entity. Nothing stores what
  the baseline used to be, and every capability has a visible cause: the profile or a source.
- **CAP-13.** A removable source MUST be its own component. Sources that share a component share
  their grants, so removing one would take away the other's.
- **CAP-14.** `Granted` and `Revoked` are cached and networked next to `Declared` (§4.1), and CC-1
  verifies them.
- **CAP-15.** Movement is not a capability. It stays a check on the body (MOV-1).

## 9. Consumers

- **CON-1.** A call site forgives a cause through `Ignoring`. It never mutates a source's state to
  get past it.
- **CON-2. Only consumers show feedback.** Checks are side-effect free (API-5, RL-4). The consumer
  that knows a person tried to act shows the reason. Code acting on its own shows nothing. CON-3,
  CON-4 and SPK-5 fix the behaviour of particular consumers; every other consumer follows this rule.
- **CON-3. Pickups.** `SharedHandsSystem.TryPickup` and `TryPickupAnyHand` take `showPopup`, which
  defaults to false. Player-initiated paths pass true: clicking an item
  (`SharedItemSystem.OnHandInteract`) and ejecting from an item slot (`ItemSlotsSystem.Eject`).
  `CanPickupToHand(…, showPopup)` shows the reason when asked.
- **CON-4.** The activate key shows the `CanPerform` reason when an activation fails (in
  `SharedInteractionSystem.HandleActivateItemInWorld`). It is interaction feedback.
- **CON-5. One popup.** A consumer in shared code shows the reason with `PopupPredicted`, so the
  player sees it once. `PopupClient` is only for paths the client always predicts. A server-only consumer, such as
  chat, uses `PopupEntity`.
- **CON-6. No reason, no popup.** When the reason is null, a consumer shows nothing. It MUST NOT
  substitute text of its own, which would break RSN-7.

### 9.1 Speech

- **SPK-1.** Chat checks `CanPerform(source, { Required = Speak, Ignoring = mask })` once per message,
  at send time. The mask comes from the call site only (IGN-8). No component, flag or event skips the
  check.
- **SPK-2. Force-say.** `DamageForceSaySystem` force-says on heavy damage, on a stun, when a mob goes
  from alive to crit or dead, and when it falls asleep: it sends `DamageForceSayEvent`, asking the
  client to send what the player had typed. Only going crit or dead and falling asleep also grant
  `AllowNextCritSpeechComponent`, for three times the ping.
  - The client wraps the line with the suffix and sends it as a force-say message:
    a message type of its own, on the channel the player typed in (Local or Whisper).
  - The server sends every force-said line as speech. While the grant is present, it removes the
    grant and sends the line with `Ignoring = Unconscious`. Otherwise it sends it with the normal
    checks and no mask. A line is never dropped for lack of a grant; it fails only where the normal
    checks fail.
  - A grant that times out is removed by `DamageForceSaySystem`'s update, so a late line is ordinary
    speech.
  - Ordinary messages never consult the grant, a query never spends it, and the bookkeeper knows
    nothing about it.
- **SPK-3. Last Words.** `CritMobActionsSystem` sends the line through
  `ChatSystem.TrySendInGameICMessage` with `ignoring: Unconscious | Silenced`. It forgives being crit
  and, deliberately, being muted. Lacking speech and `AdminMuted` still apply. A sleeping crit mob can
  still say them, because both causes are `Unconscious`.
- **SPK-4. Involuntary emotes.** A gasp (`RespiratorSystem`), a deathgasp (`DeathgaspSystem`) and an
  auto-emote pass their mask at the call site, normally `Ignoring = Unconscious`. An auto-emote
  prototype names its mask in YAML. The deathgasp skips a `Silenced` mob: a muted mob makes no vocal
  deathgasp.
- **SPK-5.** When a message is blocked, chat shows the `CanPerform` reason at send time. RSN-7 keeps
  an unconscious mind silent: a dead mime who types sees nothing, and a dying mime whose line is
  force-said sees the vow, because `Unconscious` is forgiven and `Silenced` is not.
- **SPK-6.** Other speech checks, such as the typing indicator, magic and the laws UI, show nothing.
- **SPK-7. Fake death** (`CritMobActionsSystem`) is a crit action. It forgives `Unconscious`, refuses
  a `Silenced` mob and says why (`fake-death-muted`). RSN-7 allows that reason, because after the
  action's mask the user isn't unconscious.

### 9.2 Action bar

```csharp
// ActionComponent
[DataField, AutoNetworkedField]
public InteractionRequirements Requires = new();
```

- **AB-1.** `ActionComponent.Requires` is what the performer must be capable of. It defaults to
  `{ Required = Interact }`.
- **AB-2.** `SharedActionsSystem.OnValidate` and `SharedActionsSystem.ValidateEntityTarget` check
  `CanPerform(user, Requires, target)`.
- **AB-3.** `SharedActionsSystem.ValidateWorldTarget` checks no requirement, and `Requires` doesn't
  change that.
- **AB-4.** `SharedActionsSystem` MAY put the `CanPerform` reason into `ActionAttemptEvent.Reason`.

### 9.3 Entity conditions

File: `EntityConditions/Conditions/Body/MobConditionEntityConditionSystem.cs`.

```csharp
public sealed partial class MobConditionEntityConditionSystem
    : EntityConditionSystem<MobCapabilitiesComponent, HasMobConditionCondition>;

public sealed partial class HasMobConditionCondition : EntityConditionBase<HasMobConditionCondition>
{
    [DataField(required: true)] public MobCondition Condition;
}
```

- **EC-1.** The system answers with `HasCondition`. It tests the expanded set, so "is restrained"
  needs a declared `Restrained`, while "hands blocked" is true for any cause.
- **EC-2.** The guidebook text uses `MobCapabilitiesSystem.GetLocalizedName(Condition)`, which joins
  `mob-condition-name-<member>` over the set bits. Passing the enum itself would print raw member
  names.
- **EC-3.** The guidebook key `entity-condition-guidebook-has-mob-condition` lives in
  `Resources/Locale/en-US/guidebook/entity-effects/conditions.ftl`, next to
  `entity-condition-guidebook-mob-state-condition`.
- **EC-4.** The condition is usable from `ActionRequirementsComponent.Conditions`, entity effects and
  objectives. It is additive: `MobStateCondition` stays.
