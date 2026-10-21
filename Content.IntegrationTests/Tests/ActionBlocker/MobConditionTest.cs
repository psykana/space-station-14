#nullable enable
using Content.IntegrationTests.Fixtures.Attributes;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Cuffs;
using Content.Shared.ActionBlocker;
using Content.Shared.Cuffs.Components;
using Content.Shared.Damage.ForceSay;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Paper;
using Content.Shared.StatusEffectNew;
using Content.Shared.Stunnable;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;

namespace Content.IntegrationTests.Tests.ActionBlocker;

/// <summary>
/// End-to-end tests for <see cref="MobConditionSystem"/> with its demo producers (mob state, stun, cuffs) and
/// consumers (self-uncuff, paper read-only, the UI tiers). Every integration test also runs with
/// <c>debug.mob_conditions_verify</c> on, which logs an error whenever a producer forgets to refresh.
/// </summary>
[TestFixture]
[TestOf(typeof(MobConditionSystem))]
public sealed class MobConditionTest : InteractionTest
{
    private const string TestMob = "MobConditionTestMob";
    private const string TestCuffs = "MobConditionTestCuffs";
    private const string PaperProto = "Paper";

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
  - type: MobConditions

- type: entity
  id: MobConditionTestCuffs
  components:
  - type: Handcuff
";

    protected override string PlayerPrototype => TestMob;

    [SidedDependency(Side.Server)] private readonly ActionBlockerSystem _sBlocker = default!;
    [SidedDependency(Side.Server)] private readonly MobConditionSystem _sConditions = default!;
    [SidedDependency(Side.Server)] private readonly CuffableSystem _sCuffable = default!;
    [SidedDependency(Side.Server)] private readonly MobStateSystem _sMobState = default!;
    [SidedDependency(Side.Server)] private readonly SharedStunSystem _sStun = default!;
    [SidedDependency(Side.Server)] private readonly StatusEffectsSystem _sStatus = default!;
    [SidedDependency(Side.Client)] private readonly MobConditionSystem _cConditions = default!;

    private static readonly InteractionRequirements ForgiveRestraint = new() { Ignoring = MobCondition.Restrained };

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

    private async Task SetMobState(MobState state)
    {
        await Server.WaitPost(() => _sMobState.ChangeMobState(SPlayer, state));
        await RunTicks(1);
    }

    private async Task OpenPaper()
    {
        await Activate();
        Assert.That(IsUiOpen(PaperComponent.PaperUiKey.Key), Is.True, "Paper UI did not open.");
    }

    private async Task<string> Write(string text)
    {
        await SendBui(PaperComponent.PaperUiKey.Key, new PaperComponent.PaperInputTextMessage(text));
        return Comp<PaperComponent>().Content;
    }

    [Test, Description("Restraint can be forgiven, but forgiving it doesn't forgive an independent cause.")]
    public async Task RestraintIsForgivable()
    {
        await Cuff();

        await Server.WaitAssertion(() =>
        {
            Assert.That(_sBlocker.CanInteract(SPlayer, null), Is.False);
            Assert.That(_sBlocker.CanInteract(SPlayer, null, ForgiveRestraint), Is.True);
        });

        await SetMobState(MobState.Critical);

        await Server.WaitAssertion(() =>
        {
            Assert.That(_sBlocker.CanInteract(SPlayer, null, ForgiveRestraint), Is.False,
                "Forgiving restraint must not forgive unconsciousness.");
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
            Assert.That(_sConditions.HasCondition(SPlayer, MobCondition.Restrained), Is.True);
            Assert.That(_sConditions.HasCondition(SPlayer, MobCondition.Unconscious), Is.False);
            Assert.That(_sBlocker.CanInteract(SPlayer, null), Is.False);
        });

        await Uncuff();
        await Server.WaitAssertion(() => Assert.That(_sBlocker.CanInteract(SPlayer, null), Is.True));
    }

    [Test, Description("A critical mob can't do anything, except say its last words once.")]
    public async Task UnconsciousBlocksEverything()
    {
        await SetMobState(MobState.Critical);

        await Server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(_sBlocker.CanInteract(SPlayer, null), Is.False);
                Assert.That(_sBlocker.CanConsciouslyPerformAction(SPlayer), Is.False);
                Assert.That(_sBlocker.CanSpeak(SPlayer), Is.False);
                Assert.That(_sBlocker.CanEmote(SPlayer), Is.False);
                Assert.That(_sBlocker.CanDrop(SPlayer), Is.False);
                Assert.That(_sBlocker.CanChangeDirection(SPlayer), Is.False);
                Assert.That(_sBlocker.CanMove(SPlayer), Is.False);
                Assert.That(_sConditions.IsBlocked(SPlayer, MobAction.StandUp), Is.True);
            });

            SEntMan.AddComponent<AllowNextCritSpeechComponent>(SPlayer);
            Assert.That(_sBlocker.CanSpeak(SPlayer), Is.True, "Last words were not allowed.");
        });

        await RunTicks(1);
        await Server.WaitAssertion(() => Assert.That(_sBlocker.CanSpeak(SPlayer), Is.False, "Last words were granted twice."));

        await SetMobState(MobState.Alive);
        await Server.WaitAssertion(() =>
        {
            Assert.That(_sBlocker.CanInteract(SPlayer, null), Is.True);
            Assert.That(_sBlocker.CanMove(SPlayer), Is.True);
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
                Assert.That(_sBlocker.CanInteract(SPlayer, null), Is.False);
                Assert.That(_sBlocker.CanMove(SPlayer), Is.False);
                Assert.That(_sBlocker.CanSpeak(SPlayer), Is.True);
                Assert.That(_sConditions.IsBlocked(SPlayer, MobAction.Point), Is.False);
                Assert.That(_sBlocker.CanInteract(SPlayer, null, ForgiveRestraint), Is.False);
            });
        });

        await Stun(false);
        await Server.WaitAssertion(() =>
        {
            Assert.That(_sBlocker.CanInteract(SPlayer, null), Is.True);
            Assert.That(_sBlocker.CanMove(SPlayer), Is.True);
        });
    }

    [Test, Description("The client derives the same conditions from the networked declared set.")]
    public async Task ClientPredictionMatchesServer()
    {
        await Cuff();
        await Stun(true);
        await RunTicks(5);

        var sComp = SComp<MobConditionsComponent>(SPlayer);
        var cComp = CComp<MobConditionsComponent>(CPlayer);

        await Client.WaitAssertion(() =>
        {
            Assert.That(cComp.Declared, Is.EqualTo(sComp.Declared));
            Assert.That(cComp.Conditions, Is.EqualTo(sComp.Conditions));
            Assert.That(cComp.Blocked, Is.EqualTo(sComp.Blocked));

            // Stun is still an independent cause, so forgiving restraint must not help on the client either.
            Assert.That(_cConditions.IsBlocked(CPlayer, MobAction.Interact, MobCondition.Restrained), Is.True);
        });

        await Stun(false);
        await RunTicks(5);

        await Client.WaitAssertion(() =>
        {
            Assert.That(_cConditions.IsBlocked(CPlayer, MobAction.Interact), Is.True);
            Assert.That(_cConditions.IsBlocked(CPlayer, MobAction.Interact, MobCondition.Restrained), Is.False);
        });
    }

    [Test, Description("A cuffed user can open paper to read it, but writing is rejected by the server.")]
    public async Task CuffedPaperReadOnly()
    {
        await SpawnTarget(PaperProto);
        await Cuff();

        await OpenPaper();
        Assert.That(await Write("cuffed"), Is.Empty, "A cuffed user wrote on paper.");

        await CloseBui(PaperComponent.PaperUiKey.Key);
        await Uncuff();

        await OpenPaper();
        Assert.That(await Write("free"), Is.EqualTo("free"));
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

            Assert.That(_sConditions.TryGetBlockedReason(SPlayer, MobAction.Interact, out var reason, MobCondition.Restrained));
            Assert.That(reason, Is.EqualTo(Loc.GetString("mob-condition-blocked-incapacitated")));
        });
    }

    [Test, Description("A stun leaves an open window open but rejects its input until the stun ends.")]
    public async Task StunKeepsWindowDisabled()
    {
        await SpawnTarget(PaperProto);
        await OpenPaper();

        await Stun(true);
        Assert.That(IsUiOpen(PaperComponent.PaperUiKey.Key), Is.True, "Stun closed the window.");
        Assert.That(await Write("stunned"), Is.Empty, "A stunned user wrote on paper.");

        await Stun(false);
        Assert.That(await Write("recovered"), Is.EqualTo("recovered"));
    }

    [Test, Description("Falling unconscious closes open windows.")]
    public async Task CritClosesWindow()
    {
        await SpawnTarget(PaperProto);
        await OpenPaper();

        await SetMobState(MobState.Critical);
        Assert.That(IsUiOpen(PaperComponent.PaperUiKey.Key), Is.False, "Going critical left the window open.");
    }
}
