namespace PersonalDashboard.V2.Platform;

/// <summary>Marks journal entries created while applying an operation from the legacy peer.</summary>
public sealed class PeerChangeOrigin
{
    public bool IsPeerImport { get; private set; }

    public IDisposable EnterPeerImport()
    {
        var previous = IsPeerImport;
        IsPeerImport = true;
        return new Restore(this, previous);
    }

    private sealed class Restore(PeerChangeOrigin owner, bool previous) : IDisposable
    {
        public void Dispose() => owner.IsPeerImport = previous;
    }
}
