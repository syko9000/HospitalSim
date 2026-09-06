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

        var insurers = GenerateInsuranceCompanies(insuranceCompanyCount);

        // The population isn't generated as a flat, already-adult snapshot - it's simulated year by
        // year across a century (marriages, births, old-age mortality, people moving in or out of
        // town) so lineage, households, and age structure all come out of that history rather than
        // being assembled directly. See PopulationSimulator for the actual mechanics.
        var (households, people) = new PopulationSimulator(_rng, town, insurers).Simulate(simulationYears, founderHouseholdCount);

        // Doctors are drawn from that same population rather than invented separately - a doctor is a
        // resident with their own household, spouse, kids, and mortality, same as anyone else, so
        // nothing stops one of them (or a family member) from later showing up as a patient too.
        var doctors = GenerateDoctors(doctorCount, people);

        return new HospitalWorld
        {
            Hospital = hospital,
            Doctors = doctors,
            InsuranceCompanies = insurers,
            Households = households,
            People = people,
        };
    }

    private List<Doctor> GenerateDoctors(int count, List<Person> people)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // Old enough to plausibly be through medical school and residency.
        var candidates = people
            .Where(p => p.Resident && p.DeathDate is null && p.DateOfBirth <= today.AddYears(-28))
            .OrderBy(_ => _rng.Next())
            .Take(count)
            .ToList();

        var doctors = new List<Doctor>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var person = candidates[i];
            doctors.Add(new Doctor
            {
                Id = $"D{i + 1:0000}",
                PersonId = person.Id,
                FirstName = person.FirstName,
                LastName = person.LastName,
                Specialty = Names.Specialties[i % Names.Specialties.Length],
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

    private T Pick<T>(IReadOnlyList<T> items) => items[_rng.Next(items.Count)];

    private string RandomZip() => _rng.Next(10000, 99999).ToString();
}
