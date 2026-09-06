namespace HospitalSim.World;

public sealed class WorldGenerator(int seed)
{
    private readonly Random _rng = new(seed);

    public HospitalWorld Generate(
        string townName = "Wrenfield",
        string state = "OH",
        string hospitalName = "Wrenfield Regional Medical Center",
        int doctorCount = 18,
        int insuranceCompanyCount = 5,
        int familyCount = 250)
    {
        var town = new Town { Name = townName, State = state, ZipCode = RandomZip() };

        var hospital = new Hospital
        {
            Id = "WRMC",
            Name = hospitalName,
            Town = town,
            NursingUnits =
            [
                new NursingUnit { Id = "ED", Name = "Emergency Department", RoomCount = 12, BedsPerRoom = 1 },
                new NursingUnit { Id = "ICU", Name = "Intensive Care Unit", RoomCount = 10, BedsPerRoom = 1 },
                new NursingUnit { Id = "MS3", Name = "Medical/Surgical 3rd Floor", RoomCount = 20, BedsPerRoom = 2 },
                new NursingUnit { Id = "MS4", Name = "Medical/Surgical 4th Floor", RoomCount = 20, BedsPerRoom = 2 },
                new NursingUnit { Id = "L&D", Name = "Labor & Delivery", RoomCount = 8, BedsPerRoom = 1 },
                new NursingUnit { Id = "PEDS", Name = "The Kimberly Martin Wing", RoomCount = 10, BedsPerRoom = 1 },
                new NursingUnit { Id = "PICU", Name = "Pediatric ICU - Kimberly Martin Wing", RoomCount = 4, BedsPerRoom = 1 },
            ],
        };

        var doctors = GenerateDoctors(doctorCount);
        var insurers = GenerateInsuranceCompanies(insuranceCompanyCount);
        var (families, people) = GenerateFamilies(familyCount, town, insurers);

        return new HospitalWorld
        {
            Hospital = hospital,
            Doctors = doctors,
            InsuranceCompanies = insurers,
            Families = families,
            People = people,
        };
    }

    private List<Doctor> GenerateDoctors(int count)
    {
        var doctors = new List<Doctor>(count);
        var usedSpecialties = new List<string>(Names.Specialties);
        for (var i = 0; i < count; i++)
        {
            var specialty = usedSpecialties[i % usedSpecialties.Count];
            doctors.Add(new Doctor
            {
                Id = $"D{i + 1:0000}",
                FirstName = PickFirstName(),
                LastName = Pick(Names.Surnames),
                Specialty = specialty,
            });
        }
        return doctors;
    }

    private List<InsuranceCompany> GenerateInsuranceCompanies(int count)
    {
        var roots = Names.InsuranceRoots.OrderBy(_ => _rng.Next()).Take(count).ToList();
        return roots.Select((root, i) => new InsuranceCompany
        {
            Id = $"INS{i + 1:00}",
            Name = $"{root} {Pick(Names.InsuranceSuffixes)}",
        }).ToList();
    }

    private (List<Family> families, List<Person> people) GenerateFamilies(
        int count, Town town, List<InsuranceCompany> insurers)
    {
        var families = new List<Family>(count);
        var people = new List<Person>();
        var personSeq = 1;

        for (var i = 0; i < count; i++)
        {
            var familyId = $"FAM{i + 1:00000}";
            var surname = Pick(Names.Surnames);
            var address = RandomAddress(town);
            var insurer = Pick(insurers);
            var policyNumber = $"P{_rng.Next(100000000, 999999999)}";

            var family = new Family { Id = familyId, Surname = surname, Address = address };

            var memberCount = _rng.Next(1, 6); // 1-5 members
            var adultsAdded = 0;
            for (var m = 0; m < memberCount; m++)
            {
                var isAdult = adultsAdded < 2 && (m < 2 || _rng.NextDouble() < 0.3);
                if (isAdult) adultsAdded++;

                var sex = _rng.NextDouble() < 0.5 ? Sex.Male : Sex.Female;
                var dob = isAdult ? RandomAdultDob() : RandomChildDob();

                var person = new Person
                {
                    Id = $"P{personSeq++:000000}",
                    FirstName = sex == Sex.Male ? Pick(Names.Male) : Pick(Names.Female),
                    LastName = surname,
                    Sex = sex,
                    DateOfBirth = dob,
                    Ssn = RandomSsn(),
                    Address = address,
                    PhoneNumber = RandomPhone(),
                    FamilyId = familyId,
                    InsuranceCompanyId = insurer.Id,
                    PolicyNumber = policyNumber,
                };

                people.Add(person);
                family.MemberIds.Add(person.Id);
            }

            families.Add(family);
        }

        return (families, people);
    }

    private string PickFirstName() => _rng.NextDouble() < 0.5 ? Pick(Names.Male) : Pick(Names.Female);

    private T Pick<T>(IReadOnlyList<T> items) => items[_rng.Next(items.Count)];

    private Address RandomAddress(Town town) => new()
    {
        Line1 = $"{_rng.Next(100, 9999)} {Pick(Names.StreetNames)} {Pick(Names.StreetSuffixes)}",
        City = town.Name,
        State = town.State,
        ZipCode = town.ZipCode,
    };

    private string RandomZip() => _rng.Next(10000, 99999).ToString();

    private string RandomPhone() => $"555{_rng.Next(200, 999)}{_rng.Next(1000, 9999)}";

    private string RandomSsn() => $"{_rng.Next(100, 999)}{_rng.Next(10, 99)}{_rng.Next(1000, 9999)}";

    private DateOnly RandomAdultDob()
    {
        var age = _rng.Next(19, 90);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return today.AddYears(-age).AddDays(-_rng.Next(0, 365));
    }

    private DateOnly RandomChildDob()
    {
        var age = _rng.Next(0, 18);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return today.AddYears(-age).AddDays(-_rng.Next(0, 365));
    }
}
