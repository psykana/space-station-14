using System;
using System.Linq;
using Content.Shared.ActionBlocker;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.Tests.Shared.ActionBlocker;

[Parallelizable]
[TestFixture]
[TestOf(typeof(MobCapabilitiesSystem))]
public sealed class MobConditionTablesTest
{
    /// <summary>
    /// Every capability a condition can block. <see cref="MobCapability.Complex"/> is a creature property.
    /// </summary>
    private const MobCapability AllBlockable = (MobCapability) ((1u << 16) - 1);

    private static readonly MobCondition[] AllConditions =
        Enum.GetValues<MobCondition>().Where(c => c != MobCondition.None).ToArray();

    [Test]
    public void CascadeFixedPoint()
    {
        var unconscious = MobCapabilitiesSystem.Expand(MobCondition.Unconscious);
        Assert.That(unconscious.HasFlag(MobCondition.HandsBlocked | MobCondition.PullBlocked),
            "Unconscious must reach the second-level conditions through HandsBlocked/Incapacitated.");
        Assert.That(MobCapabilitiesSystem.Expand(MobCondition.Restrained).HasFlag(MobCondition.PullBlocked));

        // Expand is idempotent.
        Assert.That(MobCapabilitiesSystem.Expand(unconscious), Is.EqualTo(unconscious));
    }

    [Test]
    public void UnconsciousBlocksEverything()
    {
        var blocked = MobCapabilitiesSystem.GetBlocked(MobCapabilitiesSystem.Expand(MobCondition.Unconscious));

        // Everything but lying down: crit and death lie the mob down.
        Assert.That(blocked, Is.EqualTo(AllBlockable & ~MobCapability.Down));
    }

    [Test]
    public void StandUpOnlyFromUnconscious()
    {
        foreach (var condition in AllConditions)
        {
            var blocked = MobCapabilitiesSystem.GetBlocked(MobCapabilitiesSystem.Expand(condition));
            Assert.That(blocked.HasFlag(MobCapability.StandUp), Is.EqualTo(condition == MobCondition.Unconscious),
                $"{condition} must not block StandUp, or knockdown recovery breaks.");
        }
    }

    [Test]
    public void StunnedCanStillPoint()
    {
        var blocked = MobCapabilitiesSystem.GetBlocked(
            MobCapabilitiesSystem.Expand(MobCondition.Incapacitated | MobCondition.Immobilized));
        Assert.That(blocked.HasFlag(MobCapability.Point), Is.False);
    }

    [Test]
    public void RestraintIsForgivable()
    {
        var cuffed = MobCapabilitiesSystem.Effective(MobCondition.Restrained, MobCondition.Restrained);
        Assert.That(MobCapabilitiesSystem.GetBlocked(cuffed) & MobCapability.Interact, Is.EqualTo(MobCapability.None));

        var cuffedAndOut = MobCapabilitiesSystem.Effective(MobCondition.Restrained | MobCondition.Unconscious,
            MobCondition.Restrained);
        Assert.That(MobCapabilitiesSystem.GetBlocked(cuffedAndOut).HasFlag(MobCapability.Interact),
            "Forgiving restraint must not forgive an independent cause.");
    }

    [Test]
    public void IgnoringDerivedBit()
    {
        // A mask forgives declared conditions only: Restrained re-derives the HandsBlocked it names.
        var cuffed = MobCapabilitiesSystem.Effective(MobCondition.Restrained, MobCondition.HandsBlocked);
        Assert.That(MobCapabilitiesSystem.GetBlocked(cuffed).HasFlag(MobCapability.Interact),
            "Ignoring a derived condition must not forgive the condition that derives it.");
    }

    [Test]
    public void IgnoringStunKeepsCuffsAndCrit()
    {
        const MobCondition stun = MobCondition.Incapacitated | MobCondition.Immobilized;

        var stunned = MobCapabilitiesSystem.Effective(stun, MobCondition.Incapacitated);
        Assert.That(MobCapabilitiesSystem.GetBlocked(stunned).HasFlag(MobCapability.UseHeld), Is.False,
            "Ignoring the stun must forgive it when nothing else blocks hands.");

        var stunnedAndCuffed = MobCapabilitiesSystem.Effective(stun | MobCondition.Restrained, MobCondition.Incapacitated);
        Assert.That(MobCapabilitiesSystem.GetBlocked(stunnedAndCuffed).HasFlag(MobCapability.UseHeld),
            "Ignoring the stun must not forgive the cuffs.");

        var stunnedAndCrit = MobCapabilitiesSystem.Effective(stun | MobCondition.Unconscious, MobCondition.Incapacitated);
        Assert.That(MobCapabilitiesSystem.GetBlocked(stunnedAndCrit).HasFlag(MobCapability.UseHeld),
            "Ignoring the stun must not forgive being crit.");
    }

    [Test]
    public void UnconsciousIsForgivenOnlyByName()
    {
        var unconscious = MobCapabilitiesSystem.GetBlocked(MobCapabilitiesSystem.Expand(MobCondition.Unconscious));
        var bits = AllConditions.Where(c => c != MobCondition.Unconscious).ToArray();

        // Every mask built from the other members.
        for (var subset = 0; subset < 1 << bits.Length; subset++)
        {
            var mask = MobCondition.None;
            for (var i = 0; i < bits.Length; i++)
            {
                if ((subset & (1 << i)) != 0)
                    mask |= bits[i];
            }

            var blocked = MobCapabilitiesSystem.GetBlocked(MobCapabilitiesSystem.Effective(MobCondition.Unconscious, mask));
            Assert.That(blocked, Is.EqualTo(unconscious), $"Ignoring {mask} partly forgave unconsciousness.");
        }
    }

    [Test]
    public void BlockingConditionsMultiCause()
    {
        var declared = MobCondition.Restrained | MobCondition.Unconscious;
        var blocking = MobCapabilitiesSystem.GetBlockingConditions(declared, MobCapability.Interact);
        Assert.That(blocking, Is.EqualTo(declared));
        Assert.That(MobCapabilitiesSystem.PickReason(blocking), Is.EqualTo(MobCondition.Unconscious));

        Assert.That(MobCapabilitiesSystem.GetBlockingConditions(MobCondition.Restrained, MobCapability.Move),
            Is.EqualTo(MobCondition.None));
    }

    [Test]
    public void NoConditionBlocksComplex()
    {
        var everything = MobCondition.None;
        foreach (var condition in AllConditions)
            everything |= condition;

        Assert.That(MobCapabilitiesSystem.GetBlocked(MobCapabilitiesSystem.Expand(everything)).HasFlag(MobCapability.Complex),
            Is.False, "Complex is a creature property; no condition may block it.");
    }

    [Test]
    public void RuleEventsKeepTheFirstReason()
    {
        var ev = new MobActionTargetAttemptEvent(EntityUid.Invalid, MobCapability.Interact, null, default);
        ev.Cancel();
        ev.Cancel("first");
        ev.Cancel("second");

        Assert.That(ev.Cancelled);
        Assert.That(ev.Reason, Is.EqualTo("first"), "A later handler overwrote the reason.");
    }
}
