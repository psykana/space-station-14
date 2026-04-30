using Content.Shared.Blob.Components;

namespace Content.Shared.Blob;

public abstract partial class SharedBlobSystem
{
    public void InitializeResource()
    {
        SubscribeLocalEvent<BlobResourceComponent, BlobPulsedSetEvent>(OnResourcePulseSet);
    }

    private void OnResourcePulseSet(Entity<BlobResourceComponent> ent, ref BlobPulsedSetEvent args)
    {
        if (args.Pulsed)
            ent.Comp.NextResourceGen = _timing.CurTime + ent.Comp.Delay;
    }

    private void UpdateResource()
    {
        var query = EntityQueryEnumerator<BlobResourceComponent, BlobStructureComponent, BlobCreatedComponent>();
        while (query.MoveNext(out var uid, out var resource, out var blob, out var created))
        {
            if (_timing.CurTime < resource.NextResourceGen)
                continue;

            if (!blob.Pulsed)
                continue;

            TryAddResource(created.Creator, resource.Resource);
            resource.NextResourceGen += resource.Delay;
            resource.Delay += resource.DelayAccumulation;
            Dirty(uid, resource);
        }
    }
}
