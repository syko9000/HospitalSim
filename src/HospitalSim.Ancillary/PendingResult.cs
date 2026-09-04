namespace HospitalSim.Ancillary;

public sealed record PendingResult(
    string FillerOrderNumber,
    string PlacerOrderNumber,
    string PatientId,
    string PatientFirstName,
    string PatientLastName,
    string VisitNumber,
    string TestCode,
    string TestName,
    string? OrderingProvider,
    DateTime OrderedAt,
    DateTime ResultDueAt);

/// <summary>
/// Orders this department has accepted and is working, keyed by its own filler order number - the one
/// number this department is guaranteed to have from the moment it accepts an order. PlacerOrderNumber
/// may still be blank for a reflex order this department originated itself, until Clinicals
/// acknowledges it and assigns one.
/// </summary>
public sealed class PendingResultState
{
    private readonly Dictionary<string, PendingResult> _byFillerOrderNumber;

    public PendingResultState() : this([]) { }

    private PendingResultState(Dictionary<string, PendingResult> byFillerOrderNumber)
    {
        _byFillerOrderNumber = byFillerOrderNumber;
    }

    public int Count => _byFillerOrderNumber.Count;

    public PendingResult? Get(string fillerOrderNumber) => _byFillerOrderNumber.GetValueOrDefault(fillerOrderNumber);

    public void Upsert(PendingResult result) => _byFillerOrderNumber[result.FillerOrderNumber] = result;

    public bool Remove(string fillerOrderNumber) => _byFillerOrderNumber.Remove(fillerOrderNumber);

    public List<PendingResult> Snapshot() => [.. _byFillerOrderNumber.Values];

    public static PendingResultState Restore(List<PendingResult> results)
    {
        var byFillerOrderNumber = new Dictionary<string, PendingResult>();
        foreach (var result in results)
        {
            byFillerOrderNumber[result.FillerOrderNumber] = result;
        }
        return new PendingResultState(byFillerOrderNumber);
    }
}
