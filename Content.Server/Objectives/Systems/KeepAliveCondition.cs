using Content.Server.Objectives.Components;
using Content.Server.GameTicking.Rules;
using Content.Shared.Mind;
using Content.Shared.Objectives.Components;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Random;
using System.Linq;

namespace Content.Server.Objectives.Systems;

/// <summary>
/// Handles keep alive condition logic and picking random traitors to keep alive.
/// </summary>
public sealed class KeepAliveConditionSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly TargetObjectiveSystem _target = default!;
    [Dependency] private readonly TraitorRuleSystem _traitorRule = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<KeepAliveConditionComponent, ObjectiveGetProgressEvent>(OnGetProgress);

        SubscribeLocalEvent<RandomTraitorAliveComponent, ObjectiveAssignedEvent>(OnAssigned);
    }

    private void OnGetProgress(EntityUid uid, KeepAliveConditionComponent comp, ref ObjectiveGetProgressEvent args)
    {
        if (!_target.GetTarget(uid, out var target))
            return;

        args.Progress = GetProgress(target.Value);
    }

    private void OnAssigned(EntityUid uid, RandomTraitorAliveComponent comp, ref ObjectiveAssignedEvent args)
    {
        // invalid prototype
        if (!TryComp<TargetObjectiveComponent>(uid, out var target))
        {
            args.Cancelled = true;
            return;
        }

        var traitors = _traitorRule.GetOtherTraitorMindsAliveAndConnected(args.Mind)
            .Select(pair => pair.Item1)
            .ToHashSet();
        var removeList = new List<EntityUid>();

        // Can't help the same person multiple times
        foreach (var objective in args.Mind.Objectives)
        {
            if (HasComp<RandomTraitorAliveComponent>(objective) || HasComp<RandomTraitorProgressComponent>(objective))
            {
                if (TryComp<TargetObjectiveComponent>(objective, out var help))
                {
                    if (help.Target != null)
                    {
                        removeList.Add(help.Target.Value);
                    }
                }
            }
        }

        foreach (var tot in removeList)
        {
            traitors.Remove(tot);
        }

        // You are the first/only traitor.
        if (traitors.Count == 0)
        {
            args.Cancelled = true;
            return;
        }

        _target.SetTarget(uid, _random.Pick(traitors), target);
    }

    private float GetProgress(EntityUid target)
    {
        if (!TryComp<MindComponent>(target, out var mind))
            return 0f;

        return _mind.IsCharacterDeadIc(mind) ? 0f : 1f;
    }
}
