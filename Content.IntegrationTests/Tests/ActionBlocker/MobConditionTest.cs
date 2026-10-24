#nullable enable
using Content.IntegrationTests.Fixtures.Attributes;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Chat.Systems;
using Content.Server.Cuffs;
using Content.Shared.Bed.Sleep;
using Content.Shared.ActionBlocker;
using Content.Shared.Cuffs.Components;
using System.Linq;
using Content.Shared.Damage.ForceSay;
using Content.Shared.Interaction.Components;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.StatusEffectNew;
using Content.Shared.Stunnable;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;

namespace Content.IntegrationTests.Tests.ActionBlocker;

/// <summary>
/// End-to-end tests for <see cref="MobCapabilitiesSystem"/> with its demo producers (mob state, stun, cuffs, sleep,
/// muting) and consumers (self-uncuff, speech, last words). Every integration test also runs with
/// <c>debug.mob_conditions_verify</c> on, which logs an error whenever a producer forgets to refresh.
/// </summary>
[TestFixture]
[TestOf(typeof(MobCapabilitiesSystem))]
public sealed class MobConditionTest : InteractionTest
{
    private const string TestMob = "MobConditionTestMob";
    private const string TestCuffs = "MobConditionTestCuffs";
    private const string MutedEffect = "StatusEffectMuted";
    private const string MimeMutedEffect = "StatusEffectMimeMuted";

    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: MobConditionTestMob
  parent: InteractionTestMob
  components:
  - type: Hands
    hands:
      hand_right:
        location: Right
      hand_left:
        location: Left
    sortedHands:
    - hand_right
    - hand_left
  - type: Physics
    bodyType: KinematicController # the default static body can never move
  - type: InputMover
  - type: Speech
  - type: Cuffable
  - type: MobState
  - type: MobCapabilities

- type: entity
  id: MobConditionTestCuffs
  components:
  - type: Handcuff
";

    protected override string PlayerPrototype => TestMob;

    [SidedDependency(Side.Server)] private readonly MobCapabilitiesSystem _sCapabilities = default!;
    [SidedDependency(Side.Server)] private readonly ChatSystem _sChat = default!;
    [SidedDependency(Side.Server)] private readonly CuffableSystem _sCuffable = default!;
    [SidedDependency(Side.Server)] private readonly MobStateSystem _sMobState = default!;
    [SidedDependency(Side.Server)] private readonly SleepingSystem _sSleeping = default!;
    [SidedDependency(Side.Server)] private readonly SharedStunSystem _sStun = default!;
    [SidedDependency(Side.Server)] private readonly StatusEffectsSystem _sStatus = default!;
    [SidedDependency(Side.Client)] private readonly MobCapabilitiesSystem _cCapabilities = default!;

    private static readonly InteractionRequirements ForgiveRestraint = new() { Ignoring = MobCondition.Restrained };

    private bool SCan(MobCapability required, EntityUid? target = null)
    {
        return _sCapabilities.CanPerform(SPlayer, new InteractionRequirements { Required = required }, target);
    }

    private EntityUid _cuffs;

    private async Task Cuff()
    {
        await Server.WaitPost(() =>
        {
            _cuffs = SSpawnAtPosition(TestCuffs, SEntMan.GetCoordinates(PlayerCoords));
            Assert.That(_sCuffable.TryAddNewCuffs(SPlayer, SPlayer, _cuffs), "Failed to cuff the player.");
        });
        await RunTicks(1);
        Assert.That(SComp<CuffableComponent>(SPlayer).CanStillInteract, Is.False);
    }

    private async Task Uncuff()
    {
        // Removing the cuffs from the container is what updates the cuff state.
        await Server.WaitPost(() => SEntMan.DeleteEntity(_cuffs));
        await RunTicks(1);
        Assert.That(SComp<CuffableComponent>(SPlayer).CanStillInteract, Is.True);
    }

    private async Task Stun(bool stunned)
    {
        await Server.WaitPost(() =>
        {
            if (stunned)
                Assert.That(_sStun.TryAddStunDuration(SPlayer, TimeSpan.FromMinutes(1)), "Failed to stun the player.");
            else
                _sStatus.TryRemoveStatusEffect(SPlayer, SharedStunSystem.StunId);
        });
        await RunTicks(1);
        Assert.That(HasComp<StunnedComponent>(Player), Is.EqualTo(stunned));
    }

    private async Task Mute(string effect, bool muted, TimeSpan? delay = null)
    {
        await Server.WaitPost(() =>
        {
            if (muted)
                Assert.That(_sStatus.TrySetStatusEffectDuration(SPlayer, effect, delay: delay), "Failed to mute the player.");
            else
                Assert.That(_sStatus.TryRemoveStatusEffect(SPlayer, effect), "Failed to unmute the player.");
        });
        await RunTicks(1);
    }

    private string SpeakReason()
    {
        var req = new InteractionRequirements { Required = MobCapability.Speak };
        Assert.That(_sCapabilities.CanPerform(SPlayer, req, null, null, out var reason), Is.False);
        return reason ?? string.Empty;
    }

    private async Task Sleep(bool asleep)
    {
        await Server.WaitPost(() =>
        {
            if (asleep)
                Assert.That(_sSleeping.TrySleeping(SPlayer), "Failed to put the player to sleep.");
            else
                Assert.That(_sSleeping.TryWaking(SPlayer, force: true), "Failed to wake the player.");
        });
        await RunTicks(1);
    }

    private async Task SetMobState(MobState state)
    {
        await Server.WaitPost(() => _sMobState.ChangeMobState(SPlayer, state));
        await RunTicks(1);
    }

    [Test, Description("Restraint can be forgiven, but forgiving it doesn't forgive an independent cause.")]
    public async Task RestraintIsForgivable()
    {
        await Cuff();

        await Server.WaitAssertion(() =>
        {
            Assert.That(SCan(MobCapability.Interact), Is.False);
            Assert.That(_sCapabilities.CanPerform(SPlayer, ForgiveRestraint), Is.True);
        });

        await SetMobState(MobState.Critical);

        await Server.WaitAssertion(() =>
        {
            Assert.That(_sCapabilities.CanPerform(SPlayer, ForgiveRestraint), Is.False,
                "Forgiving restraint must not forgive unconsciousness.");
        });
    }

    [Test, Description("A requirement is a set: the act is blocked if any capability in it is.")]
    public async Task RequirementIsASet()
    {
        await Cuff();

        await Server.WaitAssertion(() =>
        {
            Assert.That(SCan(MobCapability.Speak), Is.True);
            Assert.That(SCan(MobCapability.Speak | MobCapability.Interact), Is.False);
        });
    }

    [Test, Description("Complex interaction is a creature property, not a condition.")]
    public async Task ComplexIsACreatureProperty()
    {
        await Server.WaitAssertion(() =>
        {
            Assert.That(SCan(MobCapability.Interact | MobCapability.Complex), Is.True);

            SEntMan.RemoveComponent<ComplexInteractionComponent>(SPlayer);

            Assert.That(SCan(MobCapability.Interact | MobCapability.Complex), Is.False);
            Assert.That(SCan(MobCapability.Interact), Is.True);
            Assert.That(_sCapabilities.IsBlocked(SPlayer, MobCapability.Complex), Is.False,
                "No condition should ever block Complex.");
        });
    }

    [Test, Description("A missing capability explains a failure before any condition does.")]
    public async Task CapabilityExplainsFirst()
    {
        await Stun(true);

        await Server.WaitAssertion(() =>
        {
            SEntMan.RemoveComponent<ComplexInteractionComponent>(SPlayer);

            var req = new InteractionRequirements { Required = MobCapability.Interact | MobCapability.Complex };
            Assert.That(_sCapabilities.CanPerform(SPlayer, req, null, null, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo(Loc.GetString("mob-capability-lacking-complex")));
        });
    }

    [Test, Description("An unconscious mind gets no reason, unless the mask forgives the unconsciousness.")]
    public async Task UnconsciousGetsNoReason()
    {
        await Cuff();
        await SetMobState(MobState.Critical);

        await Server.WaitAssertion(() =>
        {
            Assert.That(_sCapabilities.CanPerform(SPlayer, new InteractionRequirements(), null, null, out var reason),
                Is.False);
            Assert.That(reason, Is.Null, "A crit mob was told why it can't interact.");

            SEntMan.RemoveComponent<ComplexInteractionComponent>(SPlayer);
            var complex = new InteractionRequirements { Required = MobCapability.Interact | MobCapability.Complex };
            Assert.That(_sCapabilities.CanPerform(SPlayer, complex, null, null, out reason), Is.False);
            Assert.That(reason, Is.Null, "A crit mob was told it lacks a capability.");

            // Forgiving the unconsciousness leaves the cuffs, which may explain themselves.
            var forgiven = new InteractionRequirements { Ignoring = MobCondition.Unconscious };
            Assert.That(_sCapabilities.CanPerform(SPlayer, forgiven, null, null, out reason), Is.False);
            Assert.That(reason, Is.EqualTo(Loc.GetString("mob-condition-blocked-restrained")));
        });
    }

    [Test, Description("Two sources: reviving a cuffed mob leaves it restrained.")]
    public async Task CuffCritReviveKeepsRestraint()
    {
        await Cuff();
        await SetMobState(MobState.Critical);
        await SetMobState(MobState.Alive);

        await Server.WaitAssertion(() =>
        {
            Assert.That(_sCapabilities.HasCondition(SPlayer, MobCondition.Restrained), Is.True);
            Assert.That(_sCapabilities.HasCondition(SPlayer, MobCondition.Unconscious), Is.False);
            Assert.That(SCan(MobCapability.Interact), Is.False);
        });

        await Uncuff();
        await Server.WaitAssertion(() => Assert.That(SCan(MobCapability.Interact), Is.True));
    }

    [Test, Description("A critical mob can't do anything, and the bookkeeper knows nothing of force-say grants.")]
    public async Task UnconsciousBlocksEverything()
    {
        await SetMobState(MobState.Critical);

        await Server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(SCan(MobCapability.Interact), Is.False);
                Assert.That(SCan(MobCapability.Conscious), Is.False);
                Assert.That(SCan(MobCapability.Speak), Is.False);
                Assert.That(SCan(MobCapability.Emote), Is.False);
                Assert.That(SCan(MobCapability.Drop), Is.False);
                Assert.That(SCan(MobCapability.ChangeDirection), Is.False);
                Assert.That(_sCapabilities.CanMove(SPlayer), Is.False);
                Assert.That(_sCapabilities.IsBlocked(SPlayer, MobCapability.StandUp), Is.True);
                Assert.That(SCan(MobCapability.Point), Is.False);
                Assert.That(SCan(MobCapability.Stand), Is.False);
                Assert.That(SCan(MobCapability.Down), Is.True, "Crit must still be able to lie the mob down.");
            });

            // The force-say grant is chat's to spend, at send time. A plain query must neither honor nor spend it.
            SEntMan.AddComponent<AllowNextCritSpeechComponent>(SPlayer).Timeout = STiming.CurTime + TimeSpan.FromMinutes(1);
            Assert.That(SCan(MobCapability.Speak), Is.False, "A speech query honored the force-say grant.");
            Assert.That(SEntMan.HasComponent<AllowNextCritSpeechComponent>(SPlayer), "A speech query spent the force-say grant.");
        });

        await SetMobState(MobState.Alive);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SCan(MobCapability.Interact), Is.True);
            Assert.That(_sCapabilities.CanMove(SPlayer), Is.True);
        });
    }

    [Test, Description("The force-say grant forgives unconsciousness for the next chat message only.")]
    public async Task ForceSayForgivesUnconsciousOnce()
    {
        await SetMobState(MobState.Critical);

        await Server.WaitAssertion(() =>
        {
            SEntMan.AddComponent<AllowNextCritSpeechComponent>(SPlayer).Timeout = STiming.CurTime + TimeSpan.FromMinutes(1);

            // Last words skip the hook entirely, so they must not spend a pending force-say grant.
            Assert.That(_sChat.CheckIgnoreSpeechBlocker(SPlayer, true, out var skipped), Is.True);
            Assert.That(skipped, Is.EqualTo(MobCondition.None));

            Assert.That(_sChat.CheckIgnoreSpeechBlocker(SPlayer, false, out var ignoring), Is.False);
            Assert.That(ignoring, Is.EqualTo(MobCondition.Unconscious));

            var req = new InteractionRequirements { Required = MobCapability.Speak, Ignoring = ignoring };
            Assert.That(_sCapabilities.CanPerform(SPlayer, req), Is.True, "The force-said line was blocked.");
        });

        await RunTicks(1);

        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.HasComponent<AllowNextCritSpeechComponent>(SPlayer), Is.False, "The grant was not spent.");
            _sChat.CheckIgnoreSpeechBlocker(SPlayer, false, out var ignoring);
            Assert.That(ignoring, Is.EqualTo(MobCondition.None), "The grant forgave a second message.");
        });
    }

    [Test, Description("A muted mob is Silenced: it can't speak, but can do everything else.")]
    public async Task MutedIsSilenced()
    {
        await Mute(MutedEffect, true);

        await Server.WaitAssertion(() =>
        {
            Assert.That(_sCapabilities.HasCondition(SPlayer, MobCondition.Silenced));
            Assert.That(SCan(MobCapability.Interact), Is.True);
            Assert.That(SpeakReason(), Is.EqualTo(Loc.GetString("speech-muted")));
        });

        // The mute comes from the server; give its state time to reach the client.
        await RunTicks(5);
        await Client.WaitAssertion(() =>
            Assert.That(_cCapabilities.HasCondition(CPlayer, MobCondition.Silenced), "The client didn't get Silenced."));

        await Mute(MutedEffect, false);

        await Server.WaitAssertion(() =>
        {
            Assert.That(_sCapabilities.HasCondition(SPlayer, MobCondition.Silenced), Is.False);
            Assert.That(SCan(MobCapability.Speak), Is.True);
        });

        await RunTicks(5);
        await Client.WaitAssertion(() =>
            Assert.That(_cCapabilities.HasCondition(CPlayer, MobCondition.Silenced), Is.False, "The client kept Silenced."));
    }

    [Test, Description("The effect that silences a mob words the reason, like a mime's vow.")]
    public async Task MimeReasonComesFromEffect()
    {
        await Mute(MimeMutedEffect, true);
        await Server.WaitAssertion(() => Assert.That(SpeakReason(), Is.EqualTo(Loc.GetString("mime-cant-speak"))));
    }

    [Test, Description("Last words forgive crit and muting; the force-say mask forgives only crit.")]
    public async Task LastWordsMask()
    {
        await Mute(MutedEffect, true);
        await SetMobState(MobState.Critical);

        await Server.WaitAssertion(() =>
        {
            var lastWords = new InteractionRequirements
            {
                Required = MobCapability.Speak,
                Ignoring = MobCondition.Unconscious | MobCondition.Silenced,
            };
            Assert.That(_sCapabilities.CanPerform(SPlayer, lastWords), Is.True, "Last words were blocked.");

            var forceSay = new InteractionRequirements { Required = MobCapability.Speak, Ignoring = MobCondition.Unconscious };
            Assert.That(_sCapabilities.CanPerform(SPlayer, forceSay, null, null, out var reason), Is.False,
                "A muted mob force-said a line.");
            Assert.That(reason, Is.EqualTo(Loc.GetString("speech-muted")));
        });
    }

    [Test, Description("A delayed mute silences once it starts, not when it's added.")]
    public async Task DelayedMuteSilencesOnStart()
    {
        await Mute(MutedEffect, true, TimeSpan.FromSeconds(1));
        await Server.WaitAssertion(() =>
            Assert.That(_sCapabilities.HasCondition(SPlayer, MobCondition.Silenced), Is.False, "Silenced before the mute started."));

        await RunSeconds(1.5f);
        await Server.WaitAssertion(() =>
            Assert.That(_sCapabilities.HasCondition(SPlayer, MobCondition.Silenced), "Not silenced after the mute started."));
    }

    [Test, Description("A sleeper is Unconscious; waking clears it and stands the mob up on the same tick.")]
    public async Task SleepIsUnconsciousAndWakeStandsUp()
    {
        await Sleep(true);

        await Server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(_sCapabilities.HasCondition(SPlayer, MobCondition.Unconscious));
                Assert.That(SCan(MobCapability.Speak), Is.False);
                Assert.That(SCan(MobCapability.Emote), Is.False);
                Assert.That(SCan(MobCapability.Point), Is.False);
                Assert.That(SCan(MobCapability.Conscious), Is.False);
                Assert.That(_sCapabilities.IsBlocked(SPlayer, MobCapability.StandUp), Is.True);
                Assert.That(SEntMan.HasComponent<KnockedDownComponent>(SPlayer), "Falling asleep didn't knock the mob down.");
            });
        });

        await Sleep(false);

        await Server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(_sCapabilities.HasCondition(SPlayer, MobCondition.Unconscious), Is.False);
                Assert.That(SCan(MobCapability.Speak), Is.True);
                Assert.That(SEntMan.HasComponent<StunnedComponent>(SPlayer), Is.False, "Waking didn't unstun the mob.");
                Assert.That(SEntMan.HasComponent<KnockedDownComponent>(SPlayer), Is.False, "Waking didn't stand the mob up.");
                Assert.That(_sCapabilities.CanMove(SPlayer), Is.True);
            });
        });
    }

    [Test, Description("Waking up while cuffed unblocks nothing the cuffs block.")]
    public async Task WakeWhileCuffed()
    {
        await Cuff();
        await Sleep(true);
        await Sleep(false);

        await Server.WaitAssertion(() =>
        {
            Assert.That(_sCapabilities.HasCondition(SPlayer, MobCondition.Restrained));
            Assert.That(SCan(MobCapability.Interact), Is.False);
            Assert.That(_sCapabilities.CanPerform(SPlayer, ForgiveRestraint), Is.True);
        });
    }

    [Test, Description("Last words work while asleep and crit, muted or not; force-say works there unmuted.")]
    public async Task LastWordsWhileAsleepInCrit()
    {
        await Sleep(true);
        await SetMobState(MobState.Critical);

        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.HasComponent<SleepingComponent>(SPlayer), "Going crit woke the mob.");

            var forceSay = new InteractionRequirements { Required = MobCapability.Speak, Ignoring = MobCondition.Unconscious };
            Assert.That(_sCapabilities.CanPerform(SPlayer, forceSay), Is.True, "A sleeping crit mob couldn't force-say.");
        });

        await Mute(MutedEffect, true);

        await Server.WaitAssertion(() =>
        {
            var lastWords = new InteractionRequirements
            {
                Required = MobCapability.Speak,
                Ignoring = MobCondition.Unconscious | MobCondition.Silenced,
            };
            Assert.That(_sCapabilities.CanPerform(SPlayer, lastWords), Is.True, "A sleeping crit mob couldn't say last words.");
        });
    }

    [Test, Description("The force-say on falling asleep goes through the same mask as crit's.")]
    public async Task ForceSayOnFallingAsleep()
    {
        await Sleep(true);

        await Server.WaitAssertion(() =>
        {
            Assert.That(SCan(MobCapability.Speak), Is.False);

            SEntMan.AddComponent<AllowNextCritSpeechComponent>(SPlayer).Timeout = STiming.CurTime + TimeSpan.FromMinutes(1);
            _sChat.CheckIgnoreSpeechBlocker(SPlayer, false, out var ignoring);
            var req = new InteractionRequirements { Required = MobCapability.Speak, Ignoring = ignoring };
            Assert.That(_sCapabilities.CanPerform(SPlayer, req), Is.True, "The force-said line was blocked.");
        });
    }

    [Test, Description("Stun blocks acting and moving, but not speaking or pointing.")]
    public async Task StunIsIncapacitatedNotUnconscious()
    {
        await Stun(true);

        await Server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(SCan(MobCapability.Interact), Is.False);
                Assert.That(_sCapabilities.CanMove(SPlayer), Is.False);
                Assert.That(SCan(MobCapability.Speak), Is.True);
                Assert.That(_sCapabilities.IsBlocked(SPlayer, MobCapability.Point), Is.False);
                Assert.That(_sCapabilities.CanPerform(SPlayer, ForgiveRestraint), Is.False);
            });
        });

        await Stun(false);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SCan(MobCapability.Interact), Is.True);
            Assert.That(_sCapabilities.CanMove(SPlayer), Is.True);
        });
    }

    [Test, Description("The client derives the same conditions from the networked declared set.")]
    public async Task ClientPredictionMatchesServer()
    {
        await Cuff();
        await Stun(true);
        await RunTicks(5);

        var sComp = SComp<MobCapabilitiesComponent>(SPlayer);
        var cComp = CComp<MobCapabilitiesComponent>(CPlayer);

        await Client.WaitAssertion(() =>
        {
            Assert.That(cComp.Declared, Is.EqualTo(sComp.Declared));
            Assert.That(cComp.Conditions, Is.EqualTo(sComp.Conditions));
            Assert.That(cComp.Blocked, Is.EqualTo(sComp.Blocked));

            // Stun is still an independent cause, so forgiving restraint must not help on the client either.
            Assert.That(_cCapabilities.IsBlocked(CPlayer, MobCapability.Interact, MobCondition.Restrained), Is.True);
        });

        await Stun(false);
        await RunTicks(5);

        await Client.WaitAssertion(() =>
        {
            Assert.That(_cCapabilities.IsBlocked(CPlayer, MobCapability.Interact), Is.True);
            Assert.That(_cCapabilities.IsBlocked(CPlayer, MobCapability.Interact, MobCondition.Restrained), Is.False);
        });
    }

    [Test, Description("Target vetoes apply after the user's own conditions are forgiven.")]
    public async Task VirtualItemVetoesInteraction()
    {
        await Cuff();

        await Server.WaitAssertion(() =>
        {
            var virtualItem = HandSys.EnumerateHeld(SPlayer).First(SEntMan.HasComponent<VirtualItemComponent>);

            Assert.That(_sCapabilities.CanPerform(SPlayer, ForgiveRestraint), Is.True);
            Assert.That(_sCapabilities.CanPerform(SPlayer, ForgiveRestraint, virtualItem), Is.False,
                "A virtual item accepted an interaction.");
        });
    }

    [Test, Description("A self-uncuff forgives the restraint, but not a stun.")]
    public async Task SelfUncuff()
    {
        await Cuff();

        await Server.WaitAssertion(() =>
        {
            var ev = new UncuffAttemptEvent(SPlayer, SPlayer);
            SEntMan.EventBus.RaiseEvent(EventSource.Local, ref ev);
            Assert.That(ev.Cancelled, Is.False, "A cuffed mob could not start uncuffing itself.");
            Assert.That(SComp<CuffableComponent>(SPlayer).CanStillInteract, Is.False);
        });

        await Stun(true);

        await Server.WaitAssertion(() =>
        {
            var ev = new UncuffAttemptEvent(SPlayer, SPlayer);
            SEntMan.EventBus.RaiseEvent(EventSource.Local, ref ev);
            Assert.That(ev.Cancelled, Is.True, "A stunned mob uncuffed itself.");

            Assert.That(_sCapabilities.TryGetBlockedReason(SPlayer, MobCapability.Interact, out var reason, MobCondition.Restrained));
            Assert.That(reason, Is.EqualTo(Loc.GetString("mob-condition-blocked-incapacitated")));
        });
    }
}
