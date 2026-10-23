using System;
using System.Linq;
using Content.Shared.ActionBlocker;
using NUnit.Framework;

namespace Content.Tests.Shared.ActionBlocker;

[Parallelizable]
[TestFixture]
[TestOf(typeof(MobCapabilitiesSystem))]
public sealed class MobConditionTablesTest
{
    /// <summary>
    /// Every capability a condition can block. <see cref="MobCapability.Complex"/> is a creature property.
    /// </summary>
    private const MobCapability AllBlockable = (MobCapability) ((1u << 17) - 1);

    private static readonly MobCondition[] AllConditions =
        Enum.GetValues<MobCondition>().Where(c => c != MobCondition.None).ToArray();

    [Test]
    public void CascadeFixedPoint()
    {
        var unconscious = MobCapabilitiesSystem.Expand(MobCondition.Unconscious);
        Assert.That(unconscious.HasFlag(MobCondition.HandsBlocked | MobCondition.UiBlocked | MobCondition.PullBlocked),
            "Unconscious must reach the second-level conditions through HandsBlocked/Incapacitated.");
        Assert.That(MobCapabilitiesSystem.Expand(MobCondition.Restrained).HasFlag(MobCondition.UiBlocked));

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
        var cuffed = MobCapabilitiesSystem.Effective(MobCondition.Restrained, MobCondition.HandsBlocked);
        Assert.That(MobCapabilitiesSystem.GetBlocked(cuffed) & MobCapability.Interact, Is.EqualTo(MobCapability.None),
            "Forgiving a derived condition must work even though Restrained re-derives it.");

        var cuffedAndOut = MobCapabilitiesSystem.Effective(MobCondition.Restrained | MobCondition.Unconscious,
            MobCondition.HandsBlocked);
        Assert.That(MobCapabilitiesSystem.GetBlocked(cuffedAndOut).HasFlag(MobCapability.Interact),
            "Incapacitated still blocks interaction.");
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
}
