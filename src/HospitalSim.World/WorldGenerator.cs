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
        int founderHouseholdCount = 90,
        int simulationYears = 100)
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

        // The population isn't generated as a flat, already-adult snapshot - it's simulated year by
        // year across a century (marriages, births, old-age mortality, people moving in or out of
        // town) so lineage, households, and age structure all come out of that history rather than
        // being assembled directly. See PopulationSimulator for the actual mechanics.
        var (households, people) = new PopulationSimulator(_rng, town, insurers).Simulate(simulationYears, founderHouseholdCount);

        return new HospitalWorld
        {
            Hospital = hospital,
            Doctors = doctors,
            InsuranceCompanies = insurers,
            Households = households,
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

    private string PickFirstName() => _rng.NextDouble() < 0.5 ? Pick(Names.Male) : Pick(Names.Female);

    private T Pick<T>(IReadOnlyList<T> items) => items[_rng.Next(items.Count)];

    private string RandomZip() => _rng.Next(10000, 99999).ToString();
}
