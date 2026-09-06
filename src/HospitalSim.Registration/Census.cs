using HospitalSim.Hl7;
using HospitalSim.World;

namespace HospitalSim.Registration;

// NursingUnitId/BedId/Room/Bed are "" for an Outpatient admission - registered, but never assigned a
// bed. Class defaults to Inpatient (enum value 0) so a census.json saved before outpatients existed
// still deserializes as what it always was.
public sealed record Admission(
    string PatientId,
    string VisitNumber,
    PatientClass Class,
    string NursingUnitId,
    string BedId,
    string Room,
    string Bed,
    string AttendingDoctorId,
    DateTime AdmitDateTime);

/// <summary>
/// Tracks who's currently admitted and which beds are occupied, so the simulator never double-books a
/// bed or transfers/discharges a patient who was never admitted.
/// </summary>
public sealed record CensusSnapshot(List<Admission> Admissions, int NextVisitSeq);

public sealed class Census(Hospital hospital)
{
    private readonly Dictionary<string, Admission> _byPatient = [];
    private readonly HashSet<string> _occupiedBeds = [];
    private int _visitSeq = 1;

    public static Census Restore(Hospital hospital, CensusSnapshot snapshot)
    {
        var census = new Census(hospital) { _visitSeq = snapshot.NextVisitSeq };
        foreach (var admission in snapshot.Admissions)
        {
            census._byPatient[admission.PatientId] = admission;
            if (!string.IsNullOrEmpty(admission.BedId)) census._occupiedBeds.Add(admission.BedId);
        }
        return census;
    }

    public CensusSnapshot CreateSnapshot() => new(CurrentAdmissions.ToList(), _visitSeq);

    public IReadOnlyCollection<Admission> CurrentAdmissions => _byPatient.Values;

    public bool IsAdmitted(string patientId) => _byPatient.ContainsKey(patientId);

    public Admission? Get(string patientId) => _byPatient.GetValueOrDefault(patientId);

    public string NextVisitNumber() => $"V{DateTime.UtcNow:yyyyMMdd}{_visitSeq++:0000}";

    // unitFilter excludes units the patient isn't eligible for (PEDS below some age, L&D by sex, ...) -
    // applied before picking, not after, so an ineligible unit's beds are never even considered, not
    // just skipped once offered.
    public (string unitId, string bedId, string room, string bed)? FindFreeBed(string? preferUnitId = null, Func<NursingUnit, bool>? unitFilter = null)
    {
        var units = preferUnitId is null
            ? hospital.NursingUnits
            : hospital.NursingUnits.Where(u => u.Id == preferUnitId)
                .Concat(hospital.NursingUnits.Where(u => u.Id != preferUnitId));

        if (unitFilter is not null) units = units.Where(unitFilter);

        foreach (var unit in units)
        {
            foreach (var bedId in unit.BedIds())
            {
                if (_occupiedBeds.Contains(bedId)) continue;
                var parts = bedId.Split('^');
                return (unit.Id, bedId, parts[1], parts[2]);
            }
        }
        return null;
    }

    public bool IsBedFree(string bedId) => !_occupiedBeds.Contains(bedId);

    public void Admit(Admission admission)
    {
        _byPatient[admission.PatientId] = admission;
        if (!string.IsNullOrEmpty(admission.BedId)) _occupiedBeds.Add(admission.BedId);
    }

    // Also how an outpatient's class change to inpatient is applied (the "transfer" is from no bed at
    // all to their first one) - the prior/new BedId being "" is handled the same as any other bed.
    public void Transfer(string patientId, Admission newAdmission)
    {
        var prior = _byPatient[patientId];
        if (!string.IsNullOrEmpty(prior.BedId)) _occupiedBeds.Remove(prior.BedId);
        _byPatient[patientId] = newAdmission;
        if (!string.IsNullOrEmpty(newAdmission.BedId)) _occupiedBeds.Add(newAdmission.BedId);
    }

    public void Discharge(string patientId)
    {
        if (_byPatient.Remove(patientId, out var admission) && !string.IsNullOrEmpty(admission.BedId))
        {
            _occupiedBeds.Remove(admission.BedId);
        }
    }
}
