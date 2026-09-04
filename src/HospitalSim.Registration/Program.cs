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
var sendingApp = Environment.GetEnvironmentVariable("HOSPITALSIM_SENDING_APP") ?? "REGISTRATION";
var sendingFacility = Environment.GetEnvironmentVariable("HOSPITALSIM_SENDING_FACILITY") ?? "MRMC";
var listenPort = int.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_LISTEN_PORT") ?? "6660");
var censusPath = Environment.GetEnvironmentVariable("HOSPITALSIM_CENSUS_PATH") ?? "census.json";

var world = WorldStore.LoadOrGenerate(worldPath);
Console.WriteLine($"Loaded world: {world.Hospital.Name} in {world.Hospital.Town.Name}, {world.Hospital.Town.State}");
Console.WriteLine($"  {world.Doctors.Count} doctors, {world.InsuranceCompanies.Count} insurers, {world.Families.Count} families, {world.People.Count} people");
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

void EnqueueArrival()
{
    var candidates = world.People
        .Where(p => !census.IsAdmitted(p.Id) && !pending.Any(a => a.Patient.Id == p.Id))
        .ToList();
    if (candidates.Count == 0) return;

    var patient = candidates[rng.Next(candidates.Count)];
    var waitMinutes = waitMinMinutes + rng.NextDouble() * (waitMaxMinutes - waitMinMinutes);
    pending.Add(new PendingArrival(patient, DateTime.UtcNow.AddMinutes(waitMinutes)));
    Console.WriteLine($"[ARRIVE]  {patient.FirstName} {patient.LastName} - waiting ~{waitMinutes:0} min to be seen");
}

async Task ProcessDueDispositionsAsync()
{
    var now = DateTime.UtcNow;
    var due = pending.Where(a => now >= a.DecideAt).ToList();
    foreach (var arrival in due)
    {
        pending.Remove(arrival);
        await DecideDispositionAsync(arrival.Patient);
    }
}

async Task DecideDispositionAsync(Person patient)
{
    if (census.IsAdmitted(patient.Id)) return; // already handled some other way - shouldn't happen, but never double up

    var doctor = world.Doctors[rng.Next(world.Doctors.Count)];
    var wantsInpatient = rng.NextDouble() < inpatientProbability;
    var bed = wantsInpatient ? census.FindFreeBed() : null;
    var actualClass = bed is null ? PatientClass.Outpatient : PatientClass.Inpatient;
    if (wantsInpatient && bed is null)
    {
        Console.WriteLine($"[DISPOSE] No free beds for {patient.FirstName} {patient.LastName} - registering outpatient instead.");
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
        NextControlId(), now, chiefComplaint);

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
        if (admission.Class != PatientClass.Outpatient)
        {
            Console.WriteLine($"[INBOUND]   A06 for {patient.FirstName} {patient.LastName} who isn't currently outpatient");
            return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "Patient is not currently outpatient");
        }

        // The bed itself is registration's call, not clinical's - unlike an A02's specific target,
        // any free bed will do here, the same as a fresh admit.
        var bed = census.FindFreeBed();
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
        if (admission.Class != PatientClass.Inpatient)
        {
            Console.WriteLine($"[INBOUND]   A07 for {patient.FirstName} {patient.LastName} who isn't currently inpatient");
            return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "Patient is not currently inpatient");
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

string NextControlId() => $"HS{DateTime.UtcNow:yyyyMMddHHmmss}{controlIdSeq++:0000}";

string SummarizeAck(string ack) => ack.Length > 60 ? ack[..60].Replace('\r', '|') + "..." : ack.Replace('\r', '|');

record PendingArrival(Person Patient, DateTime DecideAt);
