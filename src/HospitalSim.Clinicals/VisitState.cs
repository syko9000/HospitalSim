namespace HospitalSim.Clinicals;

public sealed record Visit(
    string VisitNumber,
    string PatientId,
    string PatientName,
    string? OrderingProvider,
    DateTime AdmitDateTime);

/// <summary>
/// Tracks who Clinicals currently believes is in the hospital, keyed by visit number rather than
/// patient - Clinicals only ever learns about people through registration's ADT feed, so its state is
/// deliberately a follower, not a source of truth. An A03 for a visit it never saw the A01 for (e.g.
/// it started up after that patient was already admitted) is expected, not an error - it just has
/// nothing to remove, and later admits/discharges bring it back in sync with registration on their own.
/// </summary>
public sealed class VisitState
{
    private readonly Dictionary<string, Visit> _byVisitNumber;

    public VisitState() : this([]) { }

    private VisitState(Dictionary<string, Visit> byVisitNumber)
    {
        _byVisitNumber = byVisitNumber;
    }

    public int Count => _byVisitNumber.Count;

    public bool IsKnown(string visitNumber) => _byVisitNumber.ContainsKey(visitNumber);

    public void RecordIn(Visit visit) => _byVisitNumber[visit.VisitNumber] = visit;

    /// <returns><see langword="true"/> if the visit was known and removed; <see langword="false"/> if it was already unknown.</returns>
    public bool RecordOut(string visitNumber) => _byVisitNumber.Remove(visitNumber);

    public List<Visit> Snapshot() => [.. _byVisitNumber.Values];

    /// <summary>A random currently-known visit to order against, or <see langword="null"/> if nobody's in.</summary>
    public Visit? RandomVisit(Random rng)
    {
        if (_byVisitNumber.Count == 0) return null;
        var index = rng.Next(_byVisitNumber.Count);
        return _byVisitNumber.Values.Skip(index).First();
    }

    public static VisitState Restore(List<Visit> visits)
    {
        var byVisitNumber = new Dictionary<string, Visit>();
        foreach (var visit in visits)
        {
            byVisitNumber[visit.VisitNumber] = visit;
        }
        return new VisitState(byVisitNumber);
    }
}
