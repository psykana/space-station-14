using Content.Shared.ActionBlocker;
using NUnit.Framework;

namespace Content.Tests.Shared.ActionBlocker;

[Parallelizable]
[TestFixture]
[TestOf(typeof(MobConditionSystem))]
public sealed class MobConditionTablesTest
{
    private const MobAction AllActions = (MobAction) ((1u << 16) - 1);

    [Test]
    public void CascadeFixedPoint()
    {
        var unconscious = MobConditionSystem.Expand(MobCondition.Unconscious);
        Assert.That(unconscious.HasFlag(MobCondition.HandsBlocked | MobCondition.UiBlocked | MobCondition.PullBlocked),
            "Unconscious must reach the second-level conditions through HandsBlocked/Incapacitated.");
        Assert.That(MobConditionSystem.Expand(MobCondition.Restrained).HasFlag(MobCondition.UiBlocked));

        // Expand is idempotent.
        Assert.That(MobConditionSystem.Expand(unconscious), Is.EqualTo(unconscious));
    }

    [Test]
    public void UnconsciousBlocksEverything()
    {
        var blocked = MobConditionSystem.GetBlocked(MobConditionSystem.Expand(MobCondition.Unconscious));
        Assert.That(blocked, Is.EqualTo(AllActions));
    }

    [Test]
    public void StandUpOnlyFromUnconscious()
    {
        for (var bit = 1; bit <= (int) MobCondition.Incorporeal; bit <<= 1)
        {
            var condition = (MobCondition) bit;
            var blocked = MobConditionSystem.GetBlocked(MobConditionSystem.Expand(condition));
            Assert.That(blocked.HasFlag(MobAction.StandUp), Is.EqualTo(condition == MobCondition.Unconscious),
                $"{condition} must not block StandUp, or knockdown recovery breaks.");
        }
    }

    [Test]
    public void StunnedCanStillPoint()
    {
        var blocked = MobConditionSystem.GetBlocked(
            MobConditionSystem.Expand(MobCondition.Incapacitated | MobCondition.Immobilized));
        Assert.That(blocked.HasFlag(MobAction.Point), Is.False);
    }

    [Test]
    public void RestraintIsForgivable()
    {
        var cuffed = MobConditionSystem.Effective(MobCondition.Restrained, MobCondition.Restrained);
        Assert.That(MobConditionSystem.GetBlocked(cuffed) & MobAction.Interact, Is.EqualTo(MobAction.None));

        var cuffedAndOut = MobConditionSystem.Effective(MobCondition.Restrained | MobCondition.Unconscious,
            MobCondition.Restrained);
        Assert.That(MobConditionSystem.GetBlocked(cuffedAndOut).HasFlag(MobAction.Interact),
            "Forgiving restraint must not forgive an independent cause.");
    }

    [Test]
    public void IgnoringDerivedBit()
    {
        var cuffed = MobConditionSystem.Effective(MobCondition.Restrained, MobCondition.HandsBlocked);
        Assert.That(MobConditionSystem.GetBlocked(cuffed) & MobAction.Interact, Is.EqualTo(MobAction.None),
            "Forgiving a derived condition must work even though Restrained re-derives it.");

        var cuffedAndOut = MobConditionSystem.Effective(MobCondition.Restrained | MobCondition.Unconscious,
            MobCondition.HandsBlocked);
        Assert.That(MobConditionSystem.GetBlocked(cuffedAndOut).HasFlag(MobAction.Interact),
            "Incapacitated still blocks interaction.");
    }

    [Test]
    public void BlockingConditionsMultiCause()
    {
        var declared = MobCondition.Restrained | MobCondition.Unconscious;
        var blocking = MobConditionSystem.GetBlockingConditions(declared, MobAction.Interact);
        Assert.That(blocking, Is.EqualTo(declared));
        Assert.That(MobConditionSystem.PickReason(blocking), Is.EqualTo(MobCondition.Unconscious));

        Assert.That(MobConditionSystem.GetBlockingConditions(MobCondition.Restrained, MobAction.Move),
            Is.EqualTo(MobCondition.None));
    }
}
