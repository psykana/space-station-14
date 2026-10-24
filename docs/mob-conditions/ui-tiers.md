# UI access tiers

**Status: deferred.** This phase reintroduces the `UiAccess` capability and the `UiBlocked` condition.
Its first implementation was commit `8b64a50b6b`, reverted in `c95b397a8c`. Until it lands, UIs
behave as on master.

This is the design for that phase. It builds on [spec.md](spec.md), and its rules use the same
RFC 2119 keywords. Rule ids here have the prefix `UI-`. Code is cited by file and symbol; §10 lists
what must change before the reverted commit can be reused.

## 1. Rationale

Today a UI is either usable or refused:
- **Handcuffed players can't read paper.** Cuffs block `Interact`, so manipulating an object and
  perceiving its contents can't be told apart.
- **Observers can't see most UIs.** Ghost access is a `HasComp<GhostComponent>` special case in
  `SharedInteractionSystem.OnBoundInterfaceInteractAttempt` and in `ActivatableUISystem`.
  `RequiresComplex` defaults to true, so ghosts are rejected from every machine.

Tiers separate looking from using. A UI can be closed, open but disabled, open read-only, or fully
interactive. This is tg's rule that `ui_act` requires `UI_INTERACTIVE`. Read-only is enforced on the
server; it is not a client courtesy.

Design points:
- **Spectators are a spectator rule, not a condition.** Spectators are resolved before any condition
  is consulted, so `Incorporeal` never affects UI access. A spectating ghost still gets `ReadOnly`;
  `Incorporeal` governs everything else, so a ghost still can't pick things up or emote. tg does the
  same (`states.dm`).
- **Ghosts inspect; they don't use.** Opening a UI is not free of side effects: triggers fire with
  the opener as user, shuttle consoles make the opener a pilot, arcade machines seat a player, the
  power monitor resets a focus every viewer shares, and paper resets its shared write mode. So a
  read-only open must be visible to the systems that react to it (§7).
- **Stun and cuffs don't throw windows away.** Nothing in SS14 closes UIs on stun or cuffs today, and
  a 2-second stun shouldn't discard a console window. Only unconsciousness closes them (§5).
- **A mouse still can't open a computer.** `RequiresComplex` asks `HasCapability(Complex)`, and a
  missing capability is a hard `Close`.

## 2. Types

- **UI-1.** `UiStatus.cs`: `public enum UiStatus : sbyte { Close = -1, Disabled = 0, ReadOnly = 1, Interactive = 2 }`.
  It is ordered, so `>=` comparisons are meaningful.

  | Status | Meaning | Open | Input | An already-open window |
  |---|---|---|---|---|
  | `Close` | Policy denial: `AdminOnly`, `BlockSpectators`, `RequiresComplex`. | rejected | rejected | closed by the sweep only if the cause is `Unconscious` (§5) |
  | `Disabled` | It would be accessible, but the user's conditions block it now: stunned or cuffed. | rejected | rejected | stays open |
  | `ReadOnly` | Inspect: a spectator, or a condition the UI forgives (`ReadOnlyConditions`). | allowed | rejected | stays open |
  | `Interactive` | Full use. | allowed | allowed | stays open |

- **UI-2.** `MobCapability.UiAccess` is added. `MobCondition.UiBlocked` is added: derived only,
  implied by `Incapacitated` and `HandsBlocked`, and it blocks `UiAccess`.
- **UI-3.** `ActivatableUIComponent` gains
  `[DataField, AutoNetworkedField] public MobCondition ReadOnlyConditions;`: the conditions under which
  the UI opens read-only instead of refusing.
- **UI-4.** `BeforeActivatableUIOpenEvent` and `AfterActivatableUIOpenEvent` gain
  `public UiStatus Status`. Their handlers MUST NOT apply use side effects unless
  `Status == Interactive` (§7).
- **UI-5.** `ActivatableUISystem` gains
  `public bool IsInteractiveViewer(EntityUid uiEntity, Enum key, EntityUid actor)`, for
  `BoundUIOpenedEvent` handlers, which have no `Status`. It returns
  `GetUiStatus(actor, uiEntity, key) == Interactive`.

The UI types live in `Content.Shared/UserInterface/`, namespace `Content.Shared.UserInterface`.

## 3. `ActivatableUISystem.GetUiStatus`

```csharp
public UiStatus GetUiStatus(EntityUid user, Entity<ActivatableUIComponent?> target, Enum key)
```

- **UI-6. The key is required.** The ActivatableUI policy (`RequiresComplex`, `SingleUser`) applies
  only to the BUI key that matches `ActivatableUIComponent.Key`. Other BUIs on the same entity, such as
  a PDA's secondary keys, are gated only by `Interact`. Let `comp` be the component if `comp.Key == key`,
  and null otherwise.
  - Exception: `BlockSpectators` is read whatever the key, as today.
- **UI-7. Resolution order**, first match wins:
  1. **A spectator**: the user has `GhostComponent` and `!CanGhostInteract`. `Close` if
     `BlockSpectators`, otherwise `ReadOnly`.
  2. **`comp?.AdminOnly` and the user is not an admin**: `Close`.
  3. **`comp?.RequiresComplex` and `!HasCapability(user, Complex)`**: `Close`.
  4. **`CanPerform(user, { Required = UiAccess }, target)` passes**: `Interactive`.
  5. **`comp?.ReadOnlyConditions` is not `None`**, and the same check with
     `Ignoring = ReadOnlyConditions` passes: `ReadOnly`.
  6. **Otherwise**: `Disabled`.
- **UI-8. The spectator rule keys on `CanGhostInteract`, not admin status.** Admin ghosts
  (`canInteract: true` in `admin_ghost.yml`) fall through to the normal rules. Keying on admin status
  would give an admin observing as an ordinary ghost `Interactive` everywhere, and
  `ISharedAdminManager.IsAdmin` mispredicts for anyone but the local player.
- **UI-9.** `GetUiStatus` lives in shared code and reads only networked state, so the client can call
  it for the local player.
- **UI-21.** Steps 1 and 2 are UI policy, not action blocking: they decide who may look at a UI, and
  every act inside it still goes through the bookkeeper. They are this phase's one exception to spec
  SC-1 and SC-7. `ReadOnlyConditions` is the UI's own mask, data on the act being performed, so spec
  IGN-8 allows it.

## 4. Enforcement

**`SharedInteractionSystem.OnBoundInterfaceInteractAttempt`**, the engine's
`BoundUserInterfaceMessageAttempt` hook:

- **UI-10. The BUI gate.**
  - Closing always succeeds: return early for `CloseBoundInterfaceMessage`.
  - The gate replaces the `CanInteract`/ghost special case. `Open` requires `status >= ReadOnly`;
    every other message requires `status == Interactive`; otherwise cancel.
  - It applies to every BUI, not only ActivatableUI ones: the engine raises the attempt for every
    non-close message on a BUI with `RequireInputValidation`, the default. That includes server-side
    `OpenUi`, which arrives as an `OpenBoundInterfaceMessage`.
  - Keep the range cancel (#46045), the accessibility check and the key match.
  - The single-user lock applies only when `status == Interactive`. A `ReadOnly` viewer neither holds
    nor respects it, so a ghost can't lock the crew out of a console.
  - Delete the separate `RequiresComplex` check; UI-7 step 3 covers it.
  - A raw client `Open` used to skip `AdminOnly`, which only `InteractUI` checked. The gate now
    enforces it through step 2.

**`SharedInteractionSystem.InteractionActivate`:**

- **UI-11. The read-only carve-out.** When the interaction check fails and `used` has an
  `ActivatableUIComponent` with non-`None` `ReadOnlyConditions`, call
  `ActivatableUISystem.TryOpenReadOnly(user, used)`, a public wrapper around `InteractUI` that succeeds
  only at `ReadOnly`, and return its result.
  - It MUST NOT fall through to raising `ActivateInWorldEvent`. That event reaches every handler on the
    entity, and they would run for a user whose checks just failed.
  - It is reached through the activate key (`HandleActivateItemInWorld`) and the verb. It MUST NOT be
    extended to `UserInteraction`, where left-click on a world item means pick up.

**`ActivatableUISystem`:**

- **UI-12.**
  - `ShouldAddVerb` computes the status first, before the `RequiresComplex` branch that returns early
    without hands and would hide the verb from every ghost. It returns false below `ReadOnly`, does the
    hand and `InHandsOnly` checks only at `Interactive`, and keeps
    `RaiseCanOpenEventChecks(..., silent: true)`.
  - `InteractUI` replaces its interaction/ghost and `RequiresComplex` checks with
    `if (GetUiStatus(user, (uiEntity, aui), aui.Key) < UiStatus.ReadOnly) return false;`. The
    `InHandsOnly` checks, the single-user popup and `SetCurrentSingleUser` apply only at
    `Interactive`. It passes the status into both open events.
  - `OnBoundInterfaceOpenAttempt` (#46045) is unchanged.
- **UI-13.** Every enforcement hook only cancels, so no ordering between `SharedInteractionSystem` and
  `ActivatableUISystem` is needed.

## 5. The close sweep: `ActivatableUIStatusSystem`

Today nothing closes a user's UIs on stun, cuffs or crit: the window stays open and the gate rejects
its input. The engine's `SharedUserInterfaceSystem.CheckRange` checks only map and range.

- **UI-14.** A `sealed partial` system subscribes to `MobConditionsChangedEvent`, broadcast (spec
  EV-3). When `Added` contains `Unconscious`, it collects `GetActorUis(actor)` into a list, and for
  each `(entity, key)`:
  - skips BUIs whose interface data has `RequireInputValidation == false` (borg and AI laws, the pAI
    store, vending machines): they opted out of the gate and must not be swept either;
  - calls `CloseUi(entity, key, actor)` if `GetUiStatus(actor, entity, key) < ReadOnly`.
- **UI-15.** There is no sweep for other conditions. A stunned or cuffed user's window stays open at
  `Disabled`.
- **UI-16.** There is no `MobStateChangedEvent` backstop. Crit already arrives as `Unconscious`, and on
  the client that event is raised only through damage thresholds, so it is not a reliable signal.
- **UI-17.** The sweep relies on spec RF-8: the cache is committed before the changed event, so
  `GetUiStatus` sees the new state.

The precedent is `ActivatableUIRequiresVisionSystem.OnBlindnessChanged`, which already walks
`GetActorUis` on a condition change.

## 6. Paper

- **UI-18. Data.** Set `readOnlyConditions: Restrained` on every `ActivatableUI` block with
  `key: enum.PaperUiKey.Key`. Each is its own block, not inherited: `Paper` and the envelope in
  `Entities/Objects/Misc/paper.yml`, `BookBase` in `Entities/Objects/Misc/books.yml`, and
  `HoloparasiteInfo` in `Catalog/Fills/Paper/manuals.yml`. `HoloparasiteInfo` keeps the default
  `requiresComplex: true`, so read-only applies only to complex users. Re-grep `PaperUiKey` first.
- **UI-19. Preconditions.**
  - `PaperSystem.BeforeUIOpen` resets `Mode = Read`, and `Mode` is one field shared by every viewer. A
    read-only open MUST NOT reset it, or a ghost or a cuffed reader kicks the writer out of write mode.
    Gate it on `args.Status == Interactive`.
  - `PaperSystem.OnInputTextMessage` never checks `Mode == Write`. The gate rejects `ReadOnly` input,
    so this doesn't block the phase, but it is a latent bug for interactive viewers. Fix it separately.

```yaml
- type: ActivatableUI
  key: enum.PaperUiKey.Key
  requiresComplex: false
  readOnlyConditions: Restrained   # cuffed users read; writes are rejected
```

## 7. Open side effects

- **UI-20. Rule.** Any handler of `BeforeActivatableUIOpenEvent`, `AfterActivatableUIOpenEvent` or
  `BoundUIOpenedEvent` that does anything beyond pushing UI state MUST gate on
  `Status == Interactive` or `IsInteractiveViewer`, unless the table below allows it. The phase MUST
  resolve every row, and new handlers MUST follow the rule.

| Handler | Effect on open | For `ReadOnly` |
|---|---|---|
| `TriggerSystem.Interaction` | fires a trigger with the actor as user | gate |
| `ShuttleConsoleSystem` (After) | `TryPilot`: adds `PilotComponent`, blocks movement | gate: spectators watch, they don't pilot |
| `ShuttleConsoleSystem.Drone` (After) | rebinds the drone console | gate |
| `SurveillanceCameraMonitorSystem` | `AddViewer` | allow: viewing is inspection |
| `BlockGameArcadeSystem` (After) | seats the player | route to the existing `Spectators` list |
| `SpaceVillainArcadeSystem` (After) | creates a game | gate |
| `PowerMonitoringConsoleSystem` | resets the focus shared by all viewers | gate |
| `ActivatableUISystem.Power` | toggles the item on and drains its cell | gate |
| `UIPowerStateSystem` | sets the working (power draw) state | gate |
| `GasAnalyzerSystem` | activates the analyzer | gate |
| `StationMapSystem` | uses cell charge, adds `StationMapUserComponent` | gate the charge; allow the map |
| `CrewMonitoringConsoleSystem` | uses cell charge | gate |
| `AirAlarmSystem` (After) | registers the interface, device-network sync | allow: needed to show data |
| `InstrumentSystem` | `EnsureComp<ActiveInstrumentComponent>` | gate; placeable instruments already block spectators |
| `SharedEmitSoundSystem` (After) | plays a sound | gate |
| `MagicMirrorSystem` (Before) | sets the mirror's target | gate |
| `PaperSystem.BeforeUIOpen` (Before) | resets `Mode` | gate (UI-19) |

Each handler is in `Content.Server/` or `Content.Shared/`, whichever has the file. The list came from a
sweep of open-event subscribers at upstream `6a9a8abe96`; re-sweep before implementing.

## 8. Known gaps and open questions

- **Client inspect mode is opt-in per window.** A `ReadOnly` viewer sees the normal window, and the
  server rejects its input. A BUI MAY call `GetUiStatus` for the local player and disable its controls.
  Until it does, buttons appear to do nothing.
- **A raw `Open` still skips `InHandsOnly`, `VerbOnly` and `RequiredItems`.** Only `InteractUI` checks
  them.
- **`InHandsOnly` is skipped for `ReadOnly`**, so a ghost can inspect a PDA lying on the floor.
- **Q8.** Should spectators be able to inspect `InHandsOnly` UIs? If not, UI-7 step 1 returns `Close`
  for spectators when `comp.InHandsOnly`. Ask maintainers.
- **Q9.** Should `BlockSpectators` stay entity-wide or become per key like the rest of the policy?
  Keep it entity-wide for parity, and revisit if a multi-key entity needs a mixed policy.

## 9. Tests

| Test | Asserts |
|---|---|
| `GhostReadOnly` | A non-admin ghost opens a `RequiresComplex` machine UI as `ReadOnly`; an input message is rejected server-side; `BlockSpectators` gives `Close`. An admin observing as an ordinary ghost also gets `ReadOnly`. |
| `GhostNoSingleUserLock` | A ghost opens a `SingleUser` console; a crew member can still open and use it. |
| `GhostOpenNoSideEffects` | A ghost opening a trigger-on-open entity fires no trigger, and opening a shuttle console adds no `PilotComponent`. |
| `CuffedPaperReadOnly` | A cuffed user activates paper and the UI opens; a write is rejected; after uncuffing, the write succeeds. |
| `StunKeepsWindowDisabled` | An open machine UI stays open while the user is stunned, and its input is rejected. After the stun it works again. |
| `CritClosesWindow` | An open machine UI closes when the user goes crit; the borg laws UI (`requireInputValidation: false`) does not. |

## 10. Changes needed before reusing `8b64a50b6b`

- UI-7 step 3 asks `HasCapability(Complex)`, not `CanComplexInteract` or `ComplexInteractionComponent`,
  which are gone (spec §8).
- The sweep subscribes to the broadcast `MobConditionsChangedEvent`, not directed on the bookkeeper's
  component (spec EV-3). The component is now `MobCapabilitiesComponent`.
- Masks forgive declared conditions only (spec IGN-1). `ReadOnlyConditions` MUST name declarable
  conditions: `UiBlocked` is derived only, so naming it forgives nothing (spec IGN-9).
- `MobConditionsChangedEvent` now also fires on the client while server state applies (spec EV-2).
  Decide whether the sweep runs there.
- The bookkeeper never shows popups (spec CON-2). Any feedback for a refused open belongs to the UI
  consumer.
- `UiBlocked` and `UiAccess` take bits 12 and 18, and get their locale keys (spec VC-3,
  RSN-9). `UiBlocked` is derived only, so it stays out of the reason priority (spec RSN-3).
- The bookkeeper's movement and refresh rules changed (spec RF-9 to RF-11, MOV-1 to MOV-4). Handlers
  of the changed events may refresh other entities.
- The reverted commit cites code by line. Re-sweep every reference by file and symbol.
