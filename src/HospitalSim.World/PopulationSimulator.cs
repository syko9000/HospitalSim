namespace HospitalSim.World;

/// <summary>
/// Builds the town's population by actually simulating a century of it - marriages (to another
/// resident or a newcomer from out of town), births scheduled at marriage time (some still in the
/// future relative to "today", i.e. not born yet), old-age mortality, and residents emigrating away -
/// rather than generating a single flat, already-adult snapshot. This produces real multi-generational
/// lineage (grandparent -> parent -> child, widowhood, remarriage, a household someone moved out of
/// when they married) instead of everyone being exactly one of two generations with no history.
/// </summary>
internal sealed class PopulationSimulator(Random rng, Town town, List<InsuranceCompany> insurers)
{
    private readonly List<Person> _people = [];
    private readonly Dictionary<string, Household> _households = [];
    private int _personSeq = 1;
    private int _householdSeq = 1;

    public (List<Household> households, List<Person> people) Simulate(int years, int founderHouseholdCount)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.AddYears(-years);

        SeedFounders(founderHouseholdCount, start);

        for (var y = 1; y <= years; y++)
        {
            var simDate = start.AddYears(y);
            RunMortality(simDate);
            RunMarriagesAndBirths(simDate);
            RunEmigration(simDate);
        }

        // A household nobody is left living in (everyone who lived there died or emigrated) isn't
        // worth keeping around as an empty shell.
        var finalHouseholds = _households.Values.Where(h => h.MemberIds.Count > 0).ToList();
        return (finalHouseholds, _people);
    }

    private void SeedFounders(int count, DateOnly asOf)
    {
        for (var i = 0; i < count; i++)
        {
            // Founders skew away from already-elderly so the century ahead has room to actually play
            // out for most of them, rather than half the starting population dying in year one.
            var head = NewAdult(RandomAdultDob(asOf, 19, 55));
            var household = NewHousehold(head.LastName, RandomAddress(town));
            PlaceInHousehold(head, household);

            if (rng.NextDouble() >= 0.55) continue; // stays single/unhoused-partner for now

            var spouse = NewAdult(ClampAdultDob(head.DateOfBirth.AddYears(rng.Next(-7, 8)).AddDays(rng.Next(-180, 181)), asOf));
            PlaceInHousehold(spouse, household);
            LinkSpouses(head, spouse);
            AssignGuarantor(head, spouse);

            // Founding couples may already have kids at simulation start, not just children scheduled
            // from here forward - otherwise the first ~20 simulated years would show an implausible
            // dip in the child population before any marriages have had time to produce one.
            SeedExistingChildren(head, spouse, household, asOf);
        }
    }

    private void SeedExistingChildren(Person a, Person b, Household household, DateOnly asOf)
    {
        var youngerParentDob = a.DateOfBirth > b.DateOfBirth ? a.DateOfBirth : b.DateOfBirth; // later DOB = younger
        List<string> parentIds = [a.Id, b.Id];

        for (var k = 0; k < WeightedKidCount(); k++)
        {
            var dob = asOf.AddYears(-rng.Next(0, 21)).AddDays(-rng.Next(0, 365));
            // Check against the actual final dob, day-jitter included - checking the nominal age
            // before applying that jitter let it silently push someone up to a year older than the
            // cap intended, valid at the moment picked but not once the days were subtracted.
            if (Age(youngerParentDob, dob) < 18) continue;
            NewChild(dob, parentIds, household, a.GuarantorId, a.InsuranceCompanyId, a.PolicyNumber);
        }
    }

    private void RunMortality(DateOnly simDate)
    {
        foreach (var p in _people.Where(p => p.Resident && p.DeathDate is null && p.DateOfBirth <= simDate).ToList())
        {
            var age = Age(p.DateOfBirth, simDate);
            if (age < 60 || rng.NextDouble() >= MortalityChance(age)) continue;

            p.DeathDate = simDate.AddDays(rng.Next(0, 365));
            RemoveFromHousehold(p);

            if (p.SpouseId is not { } spouseId) continue;
            var spouse = _people.FirstOrDefault(sp => sp.Id == spouseId);
            if (spouse is not { DeathDate: null }) continue;

            // SpouseId intentionally stays pointing at the deceased - widowed, not cleared, same as a
            // real next-of-kin record would still name them.
            spouse.MaritalStatus = MaritalStatus.Widowed;

            if (p.GuarantorId != p.Id) continue; // the deceased wasn't the household's policyholder - nothing to transfer
            spouse.GuarantorId = spouse.Id;
            foreach (var dependent in _people.Where(x => x.GuarantorId == p.Id && x.Id != p.Id))
            {
                // A single parent dying with no surviving spouse leaves their kids' GuarantorId pointing
                // at the deceased - a known, accepted simplification rather than modeling guardianship
                // transfer.
                dependent.GuarantorId = spouse.Id;
                dependent.InsuranceCompanyId = spouse.InsuranceCompanyId;
                dependent.PolicyNumber = spouse.PolicyNumber;
            }
        }
    }

    private void RunMarriagesAndBirths(DateOnly simDate)
    {
        var pool = _people
            .Where(p => p.Resident && p.DeathDate is null && p.DateOfBirth <= simDate
                && p.MaritalStatus != MaritalStatus.Married
                && Age(p.DateOfBirth, simDate) is >= 19 and <= 45)
            // Sorted by age so consecutive picks pair up close in age - a cheap stand-in for real
            // matching without needing an actual compatibility model.
            .OrderBy(p => p.DateOfBirth)
            .ToList();
        if (pool.Count == 0) return;

        // Scales with the current eligible pool rather than a flat headcount, so the town finds a
        // rough equilibrium over the century instead of compounding into unbounded growth.
        var target = Math.Max(1, pool.Count / 4);
        var used = new HashSet<string>();
        var idx = 0;
        var married = 0;

        while (married < target && idx < pool.Count)
        {
            var a = pool[idx++];
            if (!used.Add(a.Id)) continue;

            Person b;
            if (idx < pool.Count && !used.Contains(pool[idx].Id) && rng.NextDouble() < 0.7)
            {
                b = pool[idx++];
            }
            else
            {
                // Marries in from out of town - keeps the surname/gene pool from being capped by
                // whoever the founders happened to be.
                b = NewAdult(ClampAdultDob(a.DateOfBirth.AddYears(rng.Next(-5, 6)).AddDays(rng.Next(-180, 181)), simDate));
            }
            used.Add(b.Id);

            Marry(a, b, simDate);
            married++;
        }
    }

    private void Marry(Person a, Person b, DateOnly simDate)
    {
        RemoveFromHousehold(a);
        RemoveFromHousehold(b);

        var household = NewHousehold(a.LastName, RandomAddress(town));
        PlaceInHousehold(a, household);
        PlaceInHousehold(b, household);

        LinkSpouses(a, b);
        AssignGuarantor(a, b);
        ScheduleChildren(a, b, household, simDate);
    }

    private void ScheduleChildren(Person a, Person b, Household household, DateOnly marriageDate)
    {
        var count = WeightedKidCount();
        if (count == 0) return;

        var olderParentDob = a.DateOfBirth < b.DateOfBirth ? a.DateOfBirth : b.DateOfBirth;
        // 0-5 years to a first child, matching real spacing rather than an immediate birth.
        var nextBirth = marriageDate.AddYears(rng.Next(0, 6)).AddDays(rng.Next(0, 365));
        List<string> parentIds = [a.Id, b.Id];

        for (var k = 0; k < count; k++)
        {
            if (Age(olderParentDob, nextBirth) > 45) break; // stop scheduling once no longer plausible
            // The birth date can land after "today" if the marriage happens late in the simulated
            // century - that's intentional (a not-yet-born person), not a bug. Filtering those out of
            // who's arrival-eligible is the consuming code's job, not this generator's.
            NewChild(nextBirth, parentIds, household, a.GuarantorId, a.InsuranceCompanyId, a.PolicyNumber);
            nextBirth = nextBirth.AddYears(rng.Next(2, 5)).AddDays(rng.Next(0, 365));
        }
    }

    private void RunEmigration(DateOnly simDate)
    {
        // Only unmarried young adults leave solo in this model - covers "moved away for work/school/a
        // partner not otherwise modeled" without also needing to unwind an existing household.
        foreach (var p in _people.Where(p => p.Resident && p.DeathDate is null && p.DateOfBirth <= simDate
            && p.MaritalStatus != MaritalStatus.Married
            && Age(p.DateOfBirth, simDate) is >= 18 and <= 30).ToList())
        {
            if (rng.NextDouble() >= 0.03) continue;

            p.Resident = false;
            p.Address = RandomOutOfTownAddress();
            RemoveFromHousehold(p);
        }
    }

    // ~1% annual mortality at 60, climbing so survival past 90 is rare (matches the "almost certain by
    // 90" target: multiplying these bands out gives roughly a 7% chance of living from 60 to 90).
    private static double MortalityChance(int age) => age switch
    {
        < 65 => 0.008,
        < 70 => 0.015,
        < 75 => 0.03,
        < 80 => 0.06,
        < 85 => 0.12,
        < 90 => 0.25,
        < 95 => 0.45,
        _ => 0.65,
    };

    // Mean ~2.5 kids per marriage - well above simple replacement (~2.0), by design: founders (70
    // households) are meant to actually grow into a town over the century, not just hold steady, and
    // this also has to outrun deaths and emigration on top of replacing the two parents.
    private int WeightedKidCount()
    {
        var roll = rng.NextDouble();
        return roll < 0.10 ? 0 : roll < 0.25 ? 1 : roll < 0.50 ? 2 : roll < 0.75 ? 3 : roll < 0.90 ? 4 : 5;
    }

    private void LinkSpouses(Person a, Person b)
    {
        a.SpouseId = b.Id;
        b.SpouseId = a.Id;
        a.MaritalStatus = MaritalStatus.Married;
        b.MaritalStatus = MaritalStatus.Married;
    }

    // Exactly one of the pair keeps/gets self-guarantor status (the household's insurance
    // subscriber); the other becomes their dependent, sharing that same policy.
    private void AssignGuarantor(Person a, Person b)
    {
        var primary = rng.NextDouble() < 0.5 ? a : b;
        var dependent = primary == a ? b : a;
        primary.GuarantorId = primary.Id;
        dependent.GuarantorId = primary.Id;
        dependent.InsuranceCompanyId = primary.InsuranceCompanyId;
        dependent.PolicyNumber = primary.PolicyNumber;
    }

    private void RemoveFromHousehold(Person p)
    {
        if (!string.IsNullOrEmpty(p.HouseholdId) && _households.TryGetValue(p.HouseholdId, out var h))
        {
            h.MemberIds.Remove(p.Id);
        }
        // A household that ends up with zero members gets dropped entirely from the final output
        // (Simulate() filters empty ones) - leaving a stale HouseholdId here would then dangle. Marry()
        // always calls PlaceInHousehold right after this, which overwrites it with the new household.
        p.HouseholdId = "";
    }

    private void PlaceInHousehold(Person p, Household h)
    {
        p.HouseholdId = h.Id;
        p.Address = h.Address;
        h.MemberIds.Add(p.Id);
    }

    private Household NewHousehold(string surname, Address address)
    {
        var household = new Household { Id = $"H{_householdSeq++:00000}", Surname = surname, Address = address };
        _households[household.Id] = household;
        return household;
    }

    // A freshly created adult with no household yet - self-guaranteed and independently insured until
    // a caller (SeedFounders/Marry) places them and, if paired, hands dependent status to one of them.
    private Person NewAdult(DateOnly dob)
    {
        var sex = RandomSex();
        var id = $"P{_personSeq++:000000}";
        var insurer = Pick(insurers);
        var person = new Person
        {
            Id = id,
            FirstName = sex == Sex.Male ? Pick(Names.Male) : Pick(Names.Female),
            LastName = Pick(Names.Surnames),
            Sex = sex,
            DateOfBirth = dob,
            Ssn = RandomSsn(),
            Address = RandomAddress(town),
            PhoneNumber = RandomPhone(),
            HouseholdId = "",
            MaritalStatus = MaritalStatus.Single,
            GuarantorId = id,
            InsuranceCompanyId = insurer.Id,
            PolicyNumber = $"P{rng.Next(100000000, 999999999)}",
            Resident = true,
        };
        _people.Add(person);
        return person;
    }

    private Person NewChild(
        DateOnly dob, List<string> parentIds, Household household,
        string guarantorId, string insuranceCompanyId, string policyNumber)
    {
        var sex = RandomSex();
        var person = new Person
        {
            Id = $"P{_personSeq++:000000}",
            FirstName = sex == Sex.Male ? Pick(Names.Male) : Pick(Names.Female),
            LastName = household.Surname,
            Sex = sex,
            DateOfBirth = dob,
            Ssn = RandomSsn(),
            Address = household.Address,
            PhoneNumber = RandomPhone(),
            HouseholdId = household.Id,
            MaritalStatus = MaritalStatus.Single,
            ParentIds = parentIds,
            GuarantorId = guarantorId,
            InsuranceCompanyId = insuranceCompanyId,
            PolicyNumber = policyNumber,
            Resident = true,
        };
        _people.Add(person);
        household.MemberIds.Add(person.Id);
        return person;
    }

    private Sex RandomSex() => rng.NextDouble() < 0.5 ? Sex.Male : Sex.Female;

    private T Pick<T>(IReadOnlyList<T> items) => items[rng.Next(items.Count)];

    private Address RandomAddress(Town t) => new()
    {
        Line1 = $"{rng.Next(100, 9999)} {Pick(Names.StreetNames)} {Pick(Names.StreetSuffixes)}",
        City = t.Name,
        State = t.State,
        ZipCode = t.ZipCode,
    };

    private Address RandomOutOfTownAddress()
    {
        var (state, zipPrefix) = Pick(Names.OutOfTownStates);
        return new Address
        {
            Line1 = $"{rng.Next(100, 9999)} {Pick(Names.StreetNames)} {Pick(Names.StreetSuffixes)}",
            City = $"{Pick(Names.OutOfTownPrefixes)}{Pick(Names.OutOfTownSuffixes)}",
            State = state,
            ZipCode = $"{zipPrefix}{rng.Next(1000, 9999)}",
        };
    }

    private string RandomPhone() => $"555{rng.Next(200, 999)}{rng.Next(1000, 9999)}";

    private string RandomSsn() => $"{rng.Next(100, 999)}{rng.Next(10, 99)}{rng.Next(1000, 9999)}";

    private static int Age(DateOnly dob, DateOnly asOf) =>
        asOf.Year - dob.Year - (asOf < dob.AddYears(asOf.Year - dob.Year) ? 1 : 0);

    private DateOnly RandomAdultDob(DateOnly asOf, int minAge, int maxAge) =>
        asOf.AddYears(-rng.Next(minAge, maxAge + 1)).AddDays(-rng.Next(0, 365));

    private DateOnly ClampAdultDob(DateOnly dob, DateOnly asOf)
    {
        var minDob = asOf.AddYears(-90);
        var maxDob = asOf.AddYears(-19);
        if (dob >= minDob && dob <= maxDob) return dob;
        // Resample a fresh age in a narrow band past the edge rather than pinning everyone who clips
        // it to the exact same boundary day - that would make unrelated people share one birthdate.
        return dob > maxDob
            ? asOf.AddYears(-rng.Next(19, 23)).AddDays(-rng.Next(0, 365))
            : asOf.AddYears(-rng.Next(85, 91)).AddDays(-rng.Next(0, 365));
    }
}
