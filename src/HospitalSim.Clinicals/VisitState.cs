using HospitalSim.Hl7;

namespace HospitalSim.Clinicals;

// CurrentUnit is "" for an Outpatient (or an Inpatient Clinicals hasn't heard a location for yet) -
// deliberately still a follower here, same as the class doc below says about the rest of this state.
// DateOfBirth is the one demographic Clinicals actually keeps (parsed from PID-7, which registration's
// broadcast already carries) - everything else stays unknown by design, but step-down routing (ER/ICU
// to PEDS vs. MS, see Program.cs) is genuinely age-dependent, so this one has a real behavioral use,
// not just completeness. Nullable since a message missing PID-7 shouldn't be a hard failure.
// LastDischargeAttemptAt tracks the last time a discharge was actually sent for this visit, separate
// from PlannedDischargeAt (when it became due) - without it, DischargeDueVisitsAsync would resend an
// unconfirmed discharge on every single lifecycle tick forever, since nothing else ever moves a due
// visit's plan forward. Null means never attempted.
public sealed record Visit(
    string VisitNumber,
    string PatientId,
    string PatientFirstName,
    string PatientLastName,
    string? OrderingProvider,
    PatientClass Class,
    string CurrentUnit,
    DateOnly? DateOfBirth,
    DateTime AdmitDateTime,
    DateTime PlannedDischargeAt,
    DateTime? LastDischargeAttemptAt = null);

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

    public Visit? Get(string visitNumber) => _byVisitNumber.GetValueOrDefault(visitNumber);

    public void RecordIn(Visit visit) => _byVisitNumber[visit.VisitNumber] = visit;

    /// <returns><see langword="true"/> if the visit was known and removed; <see langword="false"/> if it was already unknown.</returns>
    public bool RecordOut(string visitNumber) => _byVisitNumber.Remove(visitNumber);

    public List<Visit> Snapshot() => [.. _byVisitNumber.Values];

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
