using System.Runtime.InteropServices;

using HospitalSim.Hl7;
using HospitalSim.Registration;
using HospitalSim.World;
var worldPath = Environment.GetEnvironmentVariable("HOSPITALSIM_WORLD_PATH") ?? "world.json";
var host = Environment.GetEnvironmentVariable("HOSPITALSIM_MLLP_HOST") ?? "localhost";
var port = int.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_MLLP_PORT") ?? "6661");
var arrivalIntervalSeconds = double.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_INTERVAL_SECONDS") ?? "300");
var waitMinMinutes = double.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_WAIT_MIN_MINUTES") ?? "5");
var waitMaxMinutes = double.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_WAIT_MAX_MINUTES") ?? "60");
var inpatientProbability = double.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_INPATIENT_PROBABILITY") ?? "0.3");
var dispositionCheckSeconds = double.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_DISPOSITION_CHECK_SECONDS") ?? "20");
// Relative likelihood of each *rolled* arrival channel - normalized against each other, not absolute
// percentages. ED dominates (most unscheduled admissions really do come through the door), front
// desk (scheduled procedures/surgery) is a meaningful chunk, the children's ward its own smaller,
// restricted stream. L&D isn't rolled at all - see EnqueueDueBirths.
var edWeight = double.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_CHANNEL_WEIGHT_ED") ?? "60");
var childrensWardWeight = double.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_CHANNEL_WEIGHT_CHILDRENS_WARD") ?? "10");
var frontDeskWeight = double.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_CHANNEL_WEIGHT_FRONT_DESK") ?? "25");
var sendingApp = Environment.GetEnvironmentVariable("HOSPITALSIM_SENDING_APP") ?? "REGISTRATION";
var sendingFacility = Environment.GetEnvironmentVariable("HOSPITALSIM_SENDING_FACILITY") ?? "WRMC";
var listenPort = int.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_LISTEN_PORT") ?? "6660");
var censusPath = Environment.GetEnvironmentVariable("HOSPITALSIM_CENSUS_PATH") ?? "census.json";

var world = WorldStore.LoadOrGenerate(worldPath);
Console.WriteLine($"Loaded world: {world.Hospital.Name} in {world.Hospital.Town.Name}, {world.Hospital.Town.State}");
var today = DateOnly.FromDateTime(DateTime.UtcNow);
var livingResidents = world.People.Count(p => p.Resident && p.DeathDate is null && p.DateOfBirth <= today);
Console.WriteLine($"  {world.Doctors.Count} doctors, {world.InsuranceCompanies.Count} insurers, {world.Households.Count} households, {livingResidents} living residents ({world.People.Count} in the full roster, incl. deceased/emigrated/not-yet-born)");
Console.WriteLine($"Sending MLLP to {host}:{port}. New arrivals every ~{arrivalIntervalSeconds}s baseline (day/night and weekday shaped), each waiting {waitMinMinutes}-{waitMaxMinutes} min to be seen before disposition. Ctrl+C to stop.");
Console.WriteLine($"Listening for inbound HL7 (e.g. clinical-initiated transfers/discharges/class changes) on :{listenPort}.");

var census = CensusStore.LoadOrCreate(world.Hospital, censusPath);
Console.WriteLine($"Loaded census: {census.CurrentAdmissions.Count} patients currently admitted.");
using var mllp = new MllpClient(host, port);
var rng = new Random();
var controlIdSeq = 1;

// Who's arrived and is waiting to be seen, not yet admitted or registered - deliberately not
// persisted (unlike census/world): a restart mid-wait just drops that one pending arrival, which
// is a fine simplification for a demo tool and avoids a second on-disk state file for something this
// short-lived (minutes to an hour or so).
var pending = new List<PendingArrival>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
// docker stop / a compose recreate sends SIGTERM, not SIGINT - without this, the process is killed
// wherever it happens to be (mid-send, or between a successful send and recording it in the census),
// which can leave the receiver holding an admit the census never learns about and re-admits later.
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    cts.Cancel();
});

var listener = new MllpListener(listenPort);
var listenerTask = listener.RunAsync(HandleInboundAsync, cts.Token);
var arrivalLoopTask = ArrivalLoopAsync();

while (!cts.IsCancellationRequested)
{
    try
    {
        await ProcessDueDispositionsAsync();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Console.Error.WriteLine($"Disposition failed: {ex.Message}");
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(dispositionCheckSeconds), cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

await listenerTask;
await arrivalLoopTask;

async Task ArrivalLoopAsync()
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            EnqueueArrival();
            EnqueueDueBirths();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Arrival failed: {ex.Message}");
        }

        // Busier hours/days get a shorter wait between arrivals, not a higher chance per fixed tick -
        // keeps the jitter shape (0.5x-1.5x) consistent regardless of what the multiplier's doing.
        var effectiveInterval = arrivalIntervalSeconds / ArrivalRateMultiplier(DateTime.Now);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(effectiveInterval * (0.5 + rng.NextDouble())), cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

// Rough day/night shape - quiet overnight, busiest evening (after-work/after-school walk-ins), plus a
// modest Friday/Saturday bump. Not modeling anything more precise than "looks like a hospital," same
// spirit as the rest of this catalog.
double ArrivalRateMultiplier(DateTime now)
{
    double[] hourly =
    [
        0.4, 0.3, 0.3, 0.3, 0.35, 0.5, // 00-05
        0.7, 0.9, 1.1, 1.2, 1.2, 1.2, // 06-11
        1.1, 1.1, 1.1, 1.2, 1.3, 1.4, // 12-17
        1.6, 1.6, 1.4, 1.1, 0.8, 0.6, // 18-23
    ];
    var weekendBump = now.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday ? 1.15 : 1.0;
    return hourly[now.Hour] * weekendBump;
}

// LaborAndDelivery deliberately isn't handled here - it's never picked through this generic
// roll-a-channel-then-find-a-candidate path, see EnqueueDueBirths.
bool ChannelEligible(ArrivalChannel channel, Person patient) => channel switch
{
    ArrivalChannel.ChildrensWard => Age(patient) < 18,
    _ => true, // ED and the front desk take anyone
};

string[] ChannelTargetUnits(ArrivalChannel channel) => channel switch
{
    ArrivalChannel.ED => ["ED"],
    ArrivalChannel.ChildrensWard => ["PEDS"],
    ArrivalChannel.FrontDesk => ["MS3", "MS4"],
    ArrivalChannel.LaborAndDelivery => ["L&D"],
    _ => [],
};

// A direct children's-ward or L&D arrival already implies inpatient care is needed - nobody gets
// walked in there just to be sent home. ED and the front desk both still roll the normal coin flip
// (most ED visits go home; front-desk same-day procedures vs. an inpatient surgical stay are both real).
bool ChannelAlwaysInpatient(ArrivalChannel channel) => channel is ArrivalChannel.ChildrensWard or ArrivalChannel.LaborAndDelivery;

// Only the channels someone shows up to unscheduled - L&D arrivals are driven by actual due dates
// (EnqueueDueBirths), never by this weighted roll.
ArrivalChannel PickChannel()
{
    var weights = new (ArrivalChannel channel, double weight)[]
    {
        (ArrivalChannel.ED, edWeight),
        (ArrivalChannel.ChildrensWard, childrensWardWeight),
        (ArrivalChannel.FrontDesk, frontDeskWeight),
    };
    var roll = rng.NextDouble() * weights.Sum(w => w.weight);
    var cumulative = 0.0;
    foreach (var (channel, weight) in weights)
    {
        cumulative += weight;
        if (roll < cumulative) return channel;
    }
    return ArrivalChannel.ED;
}

void EnqueueArrival()
{
    var channel = PickChannel();
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    // world.People is the full roster - alive residents, the deceased, non-residents who emigrated,
    // and children already scheduled but not yet born - not just who can actually walk through the
    // door today.
    var candidates = world.People
        .Where(p => p.Resident && p.DeathDate is null && p.DateOfBirth <= today)
        .Where(p => !census.IsAdmitted(p.Id) && !pending.Any(a => a.Patient.Id == p.Id) && ChannelEligible(channel, p))
        .ToList();
    if (candidates.Count == 0) return; // nobody eligible for this channel right now - skip, the next roll tries again

    ScheduleArrival(candidates[rng.Next(candidates.Count)], channel);
}

// L&D isn't a random roll against an age/sex filter - the population simulator already knows exactly
// who's due today (a not-yet-born child's DateOfBirth), so this looks up today's births directly and
// sends their mother in, instead of picking an arbitrary childbearing-age woman with no connection to
// an actual pregnancy.
void EnqueueDueBirths()
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    foreach (var child in world.People.Where(p => p.DateOfBirth == today))
    {
        // Prefer a living resident female parent (the one who was actually pregnant); fall back to
        // whichever parent is still around otherwise - same-sex parents are a case this simulator's
        // birth mechanic already doesn't model precisely, this just keeps it from throwing rather than
        // pretending to solve it.
        var mother = child.ParentIds
            .Select(id => world.People.FirstOrDefault(p => p.Id == id))
            .Where(p => p is { Resident: true, DeathDate: null })
            .OrderByDescending(p => p!.Sex == Sex.Female)
            .FirstOrDefault();
        if (mother is null) continue; // both parents dead or emigrated by the due date - nobody to send in

        if (census.IsAdmitted(mother.Id) || pending.Any(a => a.Patient.Id == mother.Id)) continue; // already on her way in for this birth

        ScheduleArrival(mother, ArrivalChannel.LaborAndDelivery);
    }
}

void ScheduleArrival(Person patient, ArrivalChannel channel)
{
    // A fuller hospital means a longer wait to be seen - but "fuller" has to mean the capacity that
    // would actually see this patient: PEDS occupancy for a children's-ward arrival, ED occupancy for
    // an ED arrival, and so on - not whole-hospital occupancy, which doesn't reflect who's actually
    // competing for the same beds. Still some jitter so it's not purely deterministic by the numbers.
    var occupancyFraction = OccupancyFractionForChannel(channel);
    var baseWaitMinutes = waitMinMinutes + occupancyFraction * (waitMaxMinutes - waitMinMinutes);
    var waitMinutes = Math.Clamp(baseWaitMinutes * (0.7 + rng.NextDouble() * 0.6), waitMinMinutes, waitMaxMinutes);
    pending.Add(new PendingArrival(patient, channel, DateTime.UtcNow.AddMinutes(waitMinutes)));
    Console.WriteLine($"[ARRIVE]  {patient.FirstName} {patient.LastName} via {channel} - waiting ~{waitMinutes:0} min to be seen");
}

async Task ProcessDueDispositionsAsync()
{
    var now = DateTime.UtcNow;
    var due = pending.Where(a => now >= a.DecideAt).ToList();
    foreach (var arrival in due)
    {
        try
        {
            await DecideDispositionAsync(arrival.Patient, arrival.Channel);
            pending.Remove(arrival);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only remove from pending once the disposition actually went through - leaving it queued
            // on failure means the next check retries the same arrival instead of losing them outright
            // (this happened live: Synapse briefly unreachable mid-redeploy silently dropped an
            // arrival that was removed from pending before the broadcast that then failed). One
            // arrival failing shouldn't block the rest of this batch either, hence catching per-item
            // rather than around the whole loop.
            Console.Error.WriteLine($"Disposition failed for {arrival.Patient.FirstName} {arrival.Patient.LastName}: {ex.Message}");
        }
    }
}

async Task DecideDispositionAsync(Person patient, ArrivalChannel channel)
{
    if (census.IsAdmitted(patient.Id)) return; // already handled some other way - shouldn't happen, but never double up

    var doctor = world.Doctors[rng.Next(world.Doctors.Count)];
    var targetUnits = ChannelTargetUnits(channel);
    var wantsInpatient = ChannelAlwaysInpatient(channel) || rng.NextDouble() < inpatientProbability;
    var bed = wantsInpatient ? census.FindFreeBed(unitFilter: unit => targetUnits.Contains(unit.Id)) : null;
    var actualClass = bed is null ? PatientClass.Outpatient : PatientClass.Inpatient;
    if (wantsInpatient && bed is null)
    {
        Console.WriteLine($"[DISPOSE] No free beds for {patient.FirstName} {patient.LastName} via {channel} - registering outpatient instead.");
    }

    var visitNumber = census.NextVisitNumber();
    var admission = new Admission(
        patient.Id, visitNumber, actualClass,
        bed?.unitId ?? "", bed?.bedId ?? "", bed?.room ?? "", bed?.bed ?? "",
        doctor.Id, DateTime.UtcNow);
    var chiefComplaint = ClinicalCatalog.ChiefComplaints[rng.Next(ClinicalCatalog.ChiefComplaints.Length)];
    var eventType = actualClass == PatientClass.Inpatient ? AdtEventType.A01_Admit : AdtEventType.A04_Register;

    var ack = await BroadcastAsync(eventType, admission, chiefComplaint);
    census.Admit(admission);
    CensusStore.Save(census, censusPath);

    var label = actualClass == PatientClass.Inpatient ? "ADMIT" : "REGISTER";
    var location = actualClass == PatientClass.Inpatient ? $"{bed!.Value.unitId} rm {bed.Value.room}{bed.Value.bed}" : "outpatient, no bed";
    Console.WriteLine($"[{label}]   {patient.FirstName} {patient.LastName} -> {location} (Dr. {doctor.LastName}) | reason: {chiefComplaint} | ack: {SummarizeAck(ack)}");
}

// Builds a fresh outbound message from census/world state - the source of truth - rather than
// relaying raw bytes. Used both for registration's own self-initiated events and for bouncing an
// inbound clinical-initiated transfer/discharge/class-change back out to the broadcast feed in
// registration's own format.
async Task<string> BroadcastAsync(AdtEventType eventType, Admission admission, string? chiefComplaint = null)
{
    var patient = world.People.First(p => p.Id == admission.PatientId);
    var doctor = world.Doctors.FirstOrDefault(d => d.Id == admission.AttendingDoctorId) ?? world.Doctors[0];
    var now = DateTime.UtcNow;

    var message = AdtMessageBuilder.Build(
        eventType,
        ToAdtPatient(patient),
        new AdtVisit(admission.VisitNumber, admission.Class, "", admission.NursingUnitId, admission.Room, admission.Bed, $"{doctor.Id}^{doctor.LastName}^{doctor.FirstName}", admission.AdmitDateTime),
        ToAdtInsurance(patient),
        // Broadcast, not point-to-point - MSH-5 stays empty (no single addressee) and MSH-6 is just
        // the hospital's own facility, since this never leaves it. A point-to-point message (a future
        // clinical order, say) would name a specific receiving application here (LAB, RAD, ...).
        sendingApp, sendingFacility, "", sendingFacility,
        NextControlId(), now, chiefComplaint,
        ToAdtNextOfKin(patient), ToAdtGuarantor(patient));

    return await mllp.SendAsync(message, cts.Token);
}

async Task<string> HandleInboundAsync(string rawMessage)
{
    var now = DateTime.UtcNow;
    Hl7ParsedMessage parsed;
    try
    {
        parsed = Hl7ParsedMessage.Parse(rawMessage);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[INBOUND]   Unparseable message: {ex.Message}");
        return AckBuilder.Build(sendingApp, sendingFacility, "", "", "", now, accept: false, "Unparseable message");
    }

    // The ACK is addressed back to whoever actually sent this message, not to a fixed config value.
    var inboundApp = parsed.Field("MSH", 3) ?? "";
    var inboundFacility = parsed.Field("MSH", 4) ?? "";

    if (parsed.MessageType is not ("ADT^A02" or "ADT^A03" or "ADT^A06" or "ADT^A07"))
    {
        Console.WriteLine($"[INBOUND]   Ignoring unsupported message type '{parsed.MessageType}'");
        return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "Unsupported message type");
    }

    // PID-3.1 is only trusted as the MR when it's actually qualified as one - PID-3.4 (assigning
    // authority) matching this facility and PID-3.5 (identifier type code) reading "MR". A bare,
    // unqualified PID-3.1 (this hospital's other systems sometimes send exactly that) isn't good
    // enough for a field this important.
    var assigningAuthority = parsed.Component("PID", 3, 4);
    var identifierTypeCode = parsed.Component("PID", 3, 5);
    if (assigningAuthority != sendingFacility || identifierTypeCode != "MR")
    {
        Console.WriteLine($"[INBOUND]   Rejecting {parsed.MessageType} - PID-3 assigning authority/type code not '{sendingFacility}'/'MR' (got '{assigningAuthority}'/'{identifierTypeCode}')");
        return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "PID-3 assigning authority or identifier type not recognized");
    }

    var patientId = parsed.Component("PID", 3, 1);
    if (string.IsNullOrEmpty(patientId))
    {
        Console.WriteLine($"[INBOUND]   Malformed {parsed.MessageType} - missing PID-3.1 patient ID");
        return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "Missing PID-3.1");
    }

    var admission = census.Get(patientId);
    if (admission is null)
    {
        // A discharge that already happened - most likely a duplicate stuck in a downstream retry/
        // backlog - is a no-op, not an error: the patient's already in the state this message is
        // asking for. Silently ACKing it (and not re-broadcasting - that already went out once, for
        // the original) lets a stuck sender's backlog drain on its own instead of piling up NAKs that
        // someone then has to clear out by hand. A visit number this census has never heard of at all
        // still gets rejected below, same as before.
        var visitNumber = parsed.Field("PV1", 19);
        if (parsed.MessageType == "ADT^A03" && !string.IsNullOrEmpty(visitNumber) && census.WasDischarged(visitNumber))
        {
            Console.WriteLine($"[INBOUND]   A03 for visit {visitNumber} - already discharged, ignoring duplicate");
            return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: true);
        }

        Console.WriteLine($"[INBOUND]   {parsed.MessageType} for patient {patientId} who isn't currently admitted/registered");
        return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "Patient not currently admitted");
    }

    var patient = world.People.First(p => p.Id == patientId);

    if (parsed.MessageType == "ADT^A02")
    {
        if (admission.Class != PatientClass.Inpatient)
        {
            Console.WriteLine($"[INBOUND]   A02 for {patient.FirstName} {patient.LastName} who's currently outpatient - no bed to move");
            return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "Not applicable to an outpatient visit");
        }

        var unitId = parsed.Component("PV1", 3, 1);
        var room = parsed.Component("PV1", 3, 2);
        var bed = parsed.Component("PV1", 3, 3);

        if (unitId is null || room is null || bed is null)
        {
            Console.WriteLine("[INBOUND]   Malformed A02 - missing PV1-3 location");
            return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "Missing PV1-3");
        }

        var bedId = $"{unitId}^{room}^{bed}";
        if (bedId != admission.BedId && !census.IsBedFree(bedId))
        {
            Console.WriteLine($"[INBOUND]   A02 target bed {bedId} is already occupied");
            // Clinicals guessed wrong, which means its picture of this patient's real location has
            // already drifted from the census (otherwise it wouldn't have proposed an occupied bed as
            // if it were free). A NAK alone doesn't fix that - Clinicals has no way to learn the truth
            // from a rejection text. Re-assert the patient's actual current location as a fresh A02, so
            // the drift self-corrects instead of accumulating silently until something like this happens.
            await BroadcastAsync(AdtEventType.A02_Transfer, admission);
            return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "Target bed occupied");
        }

        var updated = admission with { NursingUnitId = unitId, BedId = bedId, Room = room, Bed = bed };
        census.Transfer(patientId, updated);
        CensusStore.Save(census, censusPath);
        Console.WriteLine($"[INBOUND]   Clinical-initiated move: {patient.FirstName} {patient.LastName} -> {unitId} rm {room}{bed}");

        // Bounced back out to the broadcast feed recreated in registration's own format, not a copy
        // of the bytes that came in - same treatment an admit gets.
        await BroadcastAsync(AdtEventType.A02_Transfer, updated);
    }
    else if (parsed.MessageType == "ADT^A03")
    {
        census.Discharge(patientId);
        CensusStore.Save(census, censusPath);
        var from = admission.Class == PatientClass.Inpatient ? admission.NursingUnitId : "outpatient";
        Console.WriteLine($"[INBOUND]   Clinical-initiated discharge: {patient.FirstName} {patient.LastName} from {from}");

        await BroadcastAsync(AdtEventType.A03_Discharge, admission);
    }
    else if (parsed.MessageType == "ADT^A06") // outpatient being changed to inpatient
    {
        if (admission.Class == PatientClass.Inpatient)
        {
            // Already in the state being asked for - not an error, and rejecting it as one only
            // teaches Clinicals nothing. Most likely Clinicals' own picture is what's stale (it missed
            // whatever earlier broadcast actually made this true), so re-assert the current state the
            // same way a rejected A02 already does, instead of just NAK'ing and leaving it to retry
            // forever against a request that's already satisfied.
            Console.WriteLine($"[INBOUND]   A06 for {patient.FirstName} {patient.LastName} who's already inpatient - no-op, re-asserting current state");
            await BroadcastAsync(AdtEventType.A06_ChangeToInpatient, admission);
            return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: true);
        }

        // The bed itself is registration's call, not clinical's - unlike an A02's specific target,
        // any free bed will do here, the same as a fresh admit.
        var bed = census.FindFreeBed(unitFilter: unit => EligibleUnit(patient, unit));
        if (bed is null)
        {
            Console.WriteLine($"[INBOUND]   A06 for {patient.FirstName} {patient.LastName} - no free beds");
            return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "No capacity");
        }

        var updated = admission with { Class = PatientClass.Inpatient, NursingUnitId = bed.Value.unitId, BedId = bed.Value.bedId, Room = bed.Value.room, Bed = bed.Value.bed };
        census.Transfer(patientId, updated);
        CensusStore.Save(census, censusPath);
        Console.WriteLine($"[INBOUND]   Clinical-initiated class change: {patient.FirstName} {patient.LastName} outpatient -> inpatient, {bed.Value.unitId} rm {bed.Value.room}{bed.Value.bed}");

        await BroadcastAsync(AdtEventType.A06_ChangeToInpatient, updated);
    }
    else // ADT^A07 - inpatient being changed to outpatient, freeing their bed
    {
        if (admission.Class == PatientClass.Outpatient)
        {
            // Mirror of the A06 case above - already outpatient is the request already satisfied, not
            // an error. Re-assert current state so Clinicals' (evidently stale) picture self-corrects.
            Console.WriteLine($"[INBOUND]   A07 for {patient.FirstName} {patient.LastName} who's already outpatient - no-op, re-asserting current state");
            await BroadcastAsync(AdtEventType.A07_ChangeToOutpatient, admission);
            return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: true);
        }

        var freedBed = admission.NursingUnitId;
        var updated = admission with { Class = PatientClass.Outpatient, NursingUnitId = "", BedId = "", Room = "", Bed = "" };
        census.Transfer(patientId, updated);
        CensusStore.Save(census, censusPath);
        Console.WriteLine($"[INBOUND]   Clinical-initiated class change: {patient.FirstName} {patient.LastName} inpatient -> outpatient, bed freed ({freedBed})");

        await BroadcastAsync(AdtEventType.A07_ChangeToOutpatient, updated);
    }

    return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: true);
}

// PEDS/PICU/L&D aren't general-purpose the way ED/ICU/MS3/MS4 are - a unit a patient isn't eligible
// for is excluded from FindFreeBed entirely, not just skipped after being offered. ICU and PICU split
// the same way PEDS and general wards do - critical care for a kid belongs in a pediatric ICU, not the
// adult one, so ICU itself is now adults-only rather than age-agnostic.
bool EligibleUnit(Person patient, NursingUnit unit) => unit.Id switch
{
    "PEDS" or "PICU" => Age(patient) < 18,
    "ICU" => Age(patient) >= 18,
    "L&D" => patient.Sex == Sex.Female,
    _ => true,
};

int Age(Person patient)
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var age = today.Year - patient.DateOfBirth.Year;
    if (patient.DateOfBirth > today.AddYears(-age)) age--;
    return age;
}

// The congestion signal behind a wait time has to be the capacity that would actually see this
// arrival - only ED beds for an ED arrival, only PEDS for the children's ward, MS3+MS4 combined for
// the front desk, only L&D for L&D. Never whole-hospital occupancy - an empty ICU doesn't get an ED
// patient seen any faster, and a full ICU doesn't slow a scheduled front-desk admission down either.
double OccupancyFractionForChannel(ArrivalChannel channel)
{
    var units = ChannelTargetUnits(channel);
    var capacity = world.Hospital.NursingUnits.Where(u => units.Contains(u.Id)).Sum(u => u.RoomCount * u.BedsPerRoom);
    var occupied = census.CurrentAdmissions.Count(a => units.Contains(a.NursingUnitId));
    return capacity == 0 ? 0 : Math.Clamp((double)occupied / capacity, 0, 1);
}

AdtPatient ToAdtPatient(Person patient) => new(
    patient.Id, patient.FirstName, patient.LastName,
    patient.Sex == Sex.Male ? 'M' : 'F', patient.DateOfBirth, patient.Ssn,
    patient.Address.Line1, patient.Address.City, patient.Address.State, patient.Address.ZipCode,
    patient.PhoneNumber);

AdtInsurance ToAdtInsurance(Person patient)
{
    var insurer = world.InsuranceCompanies.First(i => i.Id == patient.InsuranceCompanyId);
    return new AdtInsurance(insurer.Id, insurer.Name, patient.PolicyNumber);
}

// Spouse first, then a parent, then (mainly for a widowed/single elderly resident whose own parents
// predate the simulation) a living resident child found by reverse lookup - the same reverse-lookup
// idea already used to find doctors' PersonId, applied to family instead. Null when none of those
// exist (an independent adult with no tracked spouse, parent, or child), same as any other
// never-learned-it field elsewhere in this codebase.
AdtNextOfKin? ToAdtNextOfKin(Person patient)
{
    Person? nextOfKin = null;
    string relationshipCode = "", relationshipDescription = "";

    if (patient.SpouseId is { } spouseId)
    {
        var spouse = world.People.FirstOrDefault(p => p.Id == spouseId && p.DeathDate is null);
        if (spouse is not null) (nextOfKin, relationshipCode, relationshipDescription) = (spouse, "SPO", "Spouse");
    }
    if (nextOfKin is null && patient.ParentIds.Count > 0)
    {
        var parent = world.People.FirstOrDefault(p => patient.ParentIds.Contains(p.Id) && p.DeathDate is null);
        if (parent is not null) (nextOfKin, relationshipCode, relationshipDescription) = (parent, "PAR", "Parent");
    }
    if (nextOfKin is null)
    {
        var child = world.People.FirstOrDefault(p => p.Resident && p.DeathDate is null && p.ParentIds.Contains(patient.Id));
        if (child is not null) (nextOfKin, relationshipCode, relationshipDescription) = (child, "CHD", "Child");
    }
    if (nextOfKin is null) return null;

    return new AdtNextOfKin(
        nextOfKin.FirstName, nextOfKin.LastName, relationshipCode, relationshipDescription,
        nextOfKin.Address.Line1, nextOfKin.Address.City, nextOfKin.Address.State, nextOfKin.Address.ZipCode,
        nextOfKin.PhoneNumber, nextOfKin.Sex == Sex.Male ? 'M' : 'F', nextOfKin.DateOfBirth);
}

// Always resolvable - every resident has a guarantor, even if it's themselves (GuarantorId == Id).
AdtGuarantor ToAdtGuarantor(Person patient)
{
    var guarantor = world.People.First(p => p.Id == patient.GuarantorId);
    var (relationshipCode, relationshipDescription) = guarantor.Id == patient.Id ? ("SEL", "Self")
        : guarantor.Id == patient.SpouseId ? ("SPO", "Spouse")
        : ("PAR", "Parent");

    return new AdtGuarantor(
        guarantor.Id, guarantor.FirstName, guarantor.LastName, relationshipCode, relationshipDescription,
        guarantor.Address.Line1, guarantor.Address.City, guarantor.Address.State, guarantor.Address.ZipCode,
        guarantor.PhoneNumber, guarantor.Sex == Sex.Male ? 'M' : 'F', guarantor.DateOfBirth, guarantor.Ssn);
}

string NextControlId() => $"HS{DateTime.UtcNow:yyyyMMddHHmmss}{controlIdSeq++:0000}";

string SummarizeAck(string ack) => ack.Length > 60 ? ack[..60].Replace('\r', '|') + "..." : ack.Replace('\r', '|');

record PendingArrival(Person Patient, ArrivalChannel Channel, DateTime DecideAt);

// How someone gets in the door matters - it determines who's even eligible to arrive this way, which
// ward they actually head toward, and what "busy" means for their wait. Not just flavor: ED and the
// front desk feed general beds, but the children's ward and L&D are the *only* way in for their
// respective units - nobody gets routed there some other way (no ED-to-L&D, no front-desk-to-PEDS).
enum ArrivalChannel { ED, ChildrensWard, FrontDesk, LaborAndDelivery }
