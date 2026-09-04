namespace HospitalSim.World;

public enum Sex { Male, Female }

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

public sealed class Doctor
{
    public required string Id { get; init; }
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

public sealed class Person
{
    public required string Id { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public required Sex Sex { get; init; }
    public required DateOnly DateOfBirth { get; init; }
    public required string Ssn { get; init; }
    public required Address Address { get; init; }
    public required string PhoneNumber { get; init; }
    public required string FamilyId { get; init; }
    public required string InsuranceCompanyId { get; init; }
    public required string PolicyNumber { get; init; }
}

public sealed class Family
{
    public required string Id { get; init; }
    public required string Surname { get; init; }
    public required Address Address { get; init; }
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
    public List<Family> Families { get; init; } = [];
    public List<Person> People { get; init; } = [];
}
