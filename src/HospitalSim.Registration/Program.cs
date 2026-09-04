using System.Runtime.InteropServices;

using HospitalSim.Hl7;
using HospitalSim.Registration;
using HospitalSim.World;
var worldPath = Environment.GetEnvironmentVariable("HOSPITALSIM_WORLD_PATH") ?? "world.json";
var host = Environment.GetEnvironmentVariable("HOSPITALSIM_MLLP_HOST") ?? "localhost";
var port = int.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_MLLP_PORT") ?? "6661");
var intervalSeconds = double.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_INTERVAL_SECONDS") ?? "5");
var sendingApp = Environment.GetEnvironmentVariable("HOSPITALSIM_SENDING_APP") ?? "REGISTRATION";
var sendingFacility = Environment.GetEnvironmentVariable("HOSPITALSIM_SENDING_FACILITY") ?? "MRMC";
var listenPort = int.Parse(Environment.GetEnvironmentVariable("HOSPITALSIM_LISTEN_PORT") ?? "6660");
var censusPath = Environment.GetEnvironmentVariable("HOSPITALSIM_CENSUS_PATH") ?? "census.json";

var world = WorldStore.LoadOrGenerate(worldPath);
Console.WriteLine($"Loaded world: {world.Hospital.Name} in {world.Hospital.Town.Name}, {world.Hospital.Town.State}");
Console.WriteLine($"  {world.Doctors.Count} doctors, {world.InsuranceCompanies.Count} insurers, {world.Families.Count} families, {world.People.Count} people");
Console.WriteLine($"Sending MLLP to {host}:{port} every ~{intervalSeconds}s. Ctrl+C to stop.");
Console.WriteLine($"Listening for inbound HL7 (e.g. clinical-initiated transfers) on :{listenPort}.");

var census = CensusStore.LoadOrCreate(world.Hospital, censusPath);
Console.WriteLine($"Loaded census: {census.CurrentAdmissions.Count} patients currently admitted.");
using var mllp = new MllpClient(host, port);
var rng = new Random();
var controlIdSeq = 1;

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

while (!cts.IsCancellationRequested)
{
    try
    {
        await RunOneEventAsync();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Console.Error.WriteLine($"Event failed: {ex.Message}");
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds * (0.5 + rng.NextDouble())), cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

await listenerTask;

async Task RunOneEventAsync()
{
    var admittedCount = census.CurrentAdmissions.Count;
    var action = ChooseAction(admittedCount);

    switch (action)
    {
        case ActionKind.Admit:
            await AdmitRandomPatientAsync();
            break;
        case ActionKind.Discharge:
            await DischargeRandomPatientAsync();
            break;
    }
}

ActionKind ChooseAction(int admittedCount)
{
    if (admittedCount == 0) return ActionKind.Admit;
    if (admittedCount > 150) return ActionKind.Discharge;

    return rng.NextDouble() < 0.55 ? ActionKind.Admit : ActionKind.Discharge;
}

async Task AdmitRandomPatientAsync()
{
    var candidates = world.People.Where(p => !census.IsAdmitted(p.Id)).ToList();
    if (candidates.Count == 0) return;

    var patient = candidates[rng.Next(candidates.Count)];
    var doctor = world.Doctors[rng.Next(world.Doctors.Count)];
    var bed = census.FindFreeBed();
    if (bed is null)
    {
        Console.WriteLine("No free beds - skipping admit.");
        return;
    }

    var now = DateTime.UtcNow;
    var visitNumber = census.NextVisitNumber();
    var chiefComplaint = ClinicalCatalog.ChiefComplaints[rng.Next(ClinicalCatalog.ChiefComplaints.Length)];

    var message = AdtMessageBuilder.Build(
        AdtEventType.A01_Admit,
        ToAdtPatient(patient),
        new AdtVisit(visitNumber, "", bed.Value.unitId, bed.Value.room, bed.Value.bed, doctor.Id, doctor.LastName, doctor.FirstName, now),
        ToAdtInsurance(patient),
        // Broadcast, not point-to-point - MSH-5 stays empty (no single addressee) and MSH-6 is just
        // the hospital's own facility, since this never leaves it. A point-to-point message (a future
        // clinical order, say) would name a specific receiving application here (LAB, RAD, ...).
        sendingApp, sendingFacility, "", sendingFacility,
        NextControlId(), now, chiefComplaint);

    var ack = await mllp.SendAsync(message, cts.Token);
    census.Admit(new Admission(patient.Id, visitNumber, bed.Value.unitId, bed.Value.bedId, bed.Value.room, bed.Value.bed, doctor.Id, now));
    CensusStore.Save(census, censusPath);

    Console.WriteLine($"[ADMIT]   {patient.FirstName} {patient.LastName} -> {bed.Value.unitId} rm {bed.Value.room}{bed.Value.bed} (Dr. {doctor.LastName}) | reason: {chiefComplaint} | ack: {SummarizeAck(ack)}");
}

async Task DischargeRandomPatientAsync()
{
    var admissions = census.CurrentAdmissions.ToList();
    if (admissions.Count == 0) return;

    var admission = admissions[rng.Next(admissions.Count)];
    var patient = world.People.First(p => p.Id == admission.PatientId);
    var doctor = world.Doctors.FirstOrDefault(d => d.Id == admission.AttendingDoctorId) ?? world.Doctors[0];
    var now = DateTime.UtcNow;

    var message = AdtMessageBuilder.Build(
        AdtEventType.A03_Discharge,
        ToAdtPatient(patient),
        new AdtVisit(admission.VisitNumber, "", admission.NursingUnitId, admission.Room, admission.Bed, doctor.Id, doctor.LastName, doctor.FirstName, admission.AdmitDateTime),
        ToAdtInsurance(patient),
        sendingApp, sendingFacility, "", sendingFacility,
        NextControlId(), now);

    var ack = await mllp.SendAsync(message, cts.Token);
    census.Discharge(patient.Id);
    CensusStore.Save(census, censusPath);

    Console.WriteLine($"[DISCHARGE]{patient.FirstName} {patient.LastName} from {admission.NursingUnitId} | ack: {SummarizeAck(ack)}");
}

Task<string> HandleInboundAsync(string rawMessage) => Task.FromResult(HandleInbound(rawMessage));

string HandleInbound(string rawMessage)
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

    if (parsed.MessageType != "ADT^A02")
    {
        Console.WriteLine($"[INBOUND]   Ignoring unsupported message type '{parsed.MessageType}'");
        return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "Unsupported message type");
    }

    var patientId = parsed.Component("PID", 3, 1);
    var unitId = parsed.Component("PV1", 3, 1);
    var room = parsed.Component("PV1", 3, 2);
    var bed = parsed.Component("PV1", 3, 3);

    if (patientId is null || unitId is null || room is null || bed is null)
    {
        Console.WriteLine("[INBOUND]   Malformed A02 - missing PID-3 patient ID or PV1-3 location");
        return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "Missing PID-3 or PV1-3");
    }

    var admission = census.Get(patientId);
    if (admission is null)
    {
        Console.WriteLine($"[INBOUND]   A02 for patient {patientId} who isn't currently admitted");
        return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "Patient not currently admitted");
    }

    var bedId = $"{unitId}^{room}^{bed}";
    if (bedId != admission.BedId && !census.IsBedFree(bedId))
    {
        Console.WriteLine($"[INBOUND]   A02 target bed {bedId} is already occupied");
        return AckBuilder.Build(sendingApp, sendingFacility, inboundApp, inboundFacility, parsed.MessageControlId, now, accept: false, "Target bed occupied");
    }

    census.Transfer(patientId, admission with { NursingUnitId = unitId, BedId = bedId, Room = room, Bed = bed });
    CensusStore.Save(census, censusPath);
    var patient = world.People.First(p => p.Id == patientId);
    Console.WriteLine($"[INBOUND]   Clinical-initiated move: {patient.FirstName} {patient.LastName} -> {unitId} rm {room}{bed}");

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

enum ActionKind { Admit, Discharge }
