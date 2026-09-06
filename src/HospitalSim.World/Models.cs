namespace HospitalSim.World;

public enum Sex { Male, Female }

public enum MaritalStatus { Single, Married, Divorced, Widowed }

public sealed class Town
{
    public required string Name { get; init; }
    public required string State { get; init; }
    public required string ZipCode { get; init; }
}

public sealed class InsuranceCompany
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

// A doctor is a resident of the town too, not a disconnected identity - PersonId points at their
// actual Person record (same households/family/lineage as everyone else, and the same Resident/
// DeathDate tracking, so nothing stops a doctor - or their spouse or kid - from later showing up as a
// patient). FirstName/LastName are copied from that Person at creation for convenient direct access on
// HL7 fields that need them, not independently generated.
public sealed class Doctor
{
    public required string Id { get; init; }
    public required string PersonId { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public required string Specialty { get; init; }
    public string FullName => $"{FirstName} {LastName}, MD";
}

public sealed class Address
{
    public required string Line1 { get; init; }
    public required string City { get; init; }
    public required string State { get; init; }
    public required string ZipCode { get; init; }
}

// Mutable, not a snapshot record - WorldGenerator's century simulation ages this same Person through
// marriage, widowhood, remarriage, moving households, and death, rather than building it once and
// leaving it fixed. Id/FirstName/LastName/Sex/DateOfBirth/Ssn/ParentIds are the only things that never
// change once a person is born.
public sealed class Person
{
    public required string Id { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public required Sex Sex { get; init; }
    public required DateOnly DateOfBirth { get; init; }
    public required string Ssn { get; init; }
    public required Address Address { get; set; }
    public required string PhoneNumber { get; set; }
    public required string HouseholdId { get; set; }
    public required MaritalStatus MaritalStatus { get; set; }
    // Null for anyone unmarried; the other spouse's Id otherwise - stays pointing at a deceased spouse
    // (widowed, not cleared) until/unless a remarriage overwrites it.
    public string? SpouseId { get; set; }
    // Empty for an adult (head/spouse); 1-2 parent Ids for a dependent child. Fixed at birth - which
    // household someone currently lives in can change, but who their parents were never does.
    public List<string> ParentIds { get; init; } = [];
    // Exactly one person per household is their own guarantor (Id == GuarantorId) - the insurance
    // subscriber/financially-responsible party. Everyone else in the household (spouse, kids) points at
    // that same person, which is also who InsuranceCompanyId/PolicyNumber below actually belong to.
    public required string GuarantorId { get; set; }
    public required string InsuranceCompanyId { get; set; }
    public required string PolicyNumber { get; set; }
    // True while living in Wrenfield; false once emigrated (married out, moved for work, etc.) - a
    // non-resident keeps their full Person record (Address updated to wherever they moved) rather than
    // being deleted, so anyone still in town whose ParentIds/SpouseId/GuarantorId points at them never
    // dangles. Never an arrival/admission candidate once false.
    public required bool Resident { get; set; }
    // Null while alive. A dead person still isn't deleted, same reasoning as Resident=false above.
    public DateOnly? DeathDate { get; set; }
}

public sealed class Household
{
    public required string Id { get; init; }
    // Cosmetic label only (the household that formed it) - individual members can carry different
    // LastName values (a spouse who didn't take the household's name, kids who did), this doesn't
    // constrain them.
    public required string Surname { get; init; }
    public required Address Address { get; set; }
    public List<string> MemberIds { get; init; } = [];
}

public sealed class NursingUnit
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int RoomCount { get; init; }
    public required int BedsPerRoom { get; init; }

    public IEnumerable<string> BedIds()
    {
        for (var room = 1; room <= RoomCount; room++)
        {
            for (var bed = 1; bed <= BedsPerRoom; bed++)
            {
                yield return $"{Id}^{room:000}^{(char)('A' + bed - 1)}";
            }
        }
    }
}

public sealed class Hospital
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public required Town Town { get; init; }
    public List<NursingUnit> NursingUnits { get; init; } = [];
}

public sealed class HospitalWorld
{
    public required Hospital Hospital { get; init; }
    public List<Doctor> Doctors { get; init; } = [];
    public List<InsuranceCompany> InsuranceCompanies { get; init; } = [];
    public List<Household> Households { get; init; } = [];
    // The full roster - alive residents, the deceased, and non-residents who emigrated - not just
    // who's currently arrival-eligible. Consumers that pick an arrival/admission candidate need their
    // own filter (Resident && DeathDate is null && already born); this list exists so lineage
    // references (ParentIds/SpouseId/GuarantorId) never point at someone who was deleted outright.
    public List<Person> People { get; init; } = [];
}
