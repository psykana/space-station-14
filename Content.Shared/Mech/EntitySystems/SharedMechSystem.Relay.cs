using Content.Shared.ActionBlocker;
using Content.Shared.Interaction.Events;
using Content.Shared.Mech.Components;

namespace Content.Shared.Mech.EntitySystems;

public abstract partial class SharedMechSystem
{
    [SubscribeLocalEvent]
    private void OnTargetAttempt(Entity<MechComponent> ent, ref MobActionTargetAttemptEvent args)
    {
        if (args.Action == MobAction.Attack)
            RelayRefToPilot(ent, ref args);
    }

    // Still relayed for downstream subscribers of MechPilotRelayedEvent<GettingAttackedAttemptEvent>, until the
    // obsolete event is removed.
#pragma warning disable CS0618
    [SubscribeLocalEvent]
    private void OnGettingAttackedAttempt(Entity<MechComponent> ent, ref GettingAttackedAttemptEvent args)
    {
        RelayRefToPilot(ent, ref args);
    }
#pragma warning restore CS0618

    private void RelayRefToPilot<T>(Entity<MechComponent> uid, ref T args) where T :struct
    {
        if (!Vehicle.TryGetOperator(uid.Owner, out var operatorEnt))
            return;

        var ev = new MechPilotRelayedEvent<T>(args);

        RaiseLocalEvent(operatorEnt.Value, ref ev);

        args = ev.Args;
    }
}

[ByRefEvent]
public record struct MechPilotRelayedEvent<TEvent>(TEvent Args)
{
    public TEvent Args = Args;
}
