using System.Runtime.InteropServices;

using HospitalSim.Clinicals;
using HospitalSim.Hl7;
using HospitalSim.World;

var listenPort = int.Parse(Environment.GetEnvironmentVariable("CLINICALS_LISTEN_PORT") ?? "6680");
var statePath = Environment.GetEnvironmentVariable("CLINICALS_STATE_PATH") ?? "state.json";
var app = Environment.GetEnvironmentVariable("CLINICALS_APP") ?? "CLINICALS";
var facility = Environment.GetEnvironmentVariable("CLINICALS_FACILITY") ?? "WRMC";
var orderIntervalSeconds = double.Parse(Environment.GetEnvironmentVariable("CLINICALS_ORDER_INTERVAL_SECONDS") ?? "20");
// Each department is a genuinely separate destination now that Lab/Rad/Path are real services, not
// one generic sink - Clinicals has to know where each one is, the same way an interface engine's
// MSH-5-based routing would, since nothing here plays that role locally.
var labHost = Environment.GetEnvironmentVariable("CLINICALS_LAB_MLLP_HOST") ?? "localhost";
var labPort = int.Parse(Environment.GetEnvironmentVariable("CLINICALS_LAB_MLLP_PORT") ?? "6662");
var radHost = Environment.GetEnvironmentVariable("CLINICALS_RAD_MLLP_HOST") ?? "localhost";
var radPort = int.Parse(Environment.GetEnvironmentVariable("CLINICALS_RAD_MLLP_PORT") ?? "6663");
var pathHost = Environment.GetEnvironmentVariable("CLINICALS_PATH_MLLP_HOST") ?? "localhost";
var pathPort = int.Parse(Environment.GetEnvironmentVariable("CLINICALS_PATH_MLLP_PORT") ?? "6664");
var adtHost = Environment.GetEnvironmentVariable("CLINICALS_ADT_MLLP_HOST") ?? "localhost";
var adtPort = int.Parse(Environment.GetEnvironmentVariable("CLINICALS_ADT_MLLP_PORT") ?? "6660");
var adtIntervalSeconds = double.Parse(Environment.GetEnvironmentVariable("CLINICALS_ADT_INTERVAL_SECONDS") ?? "30");
// A due discharge that hasn't been echoed back yet is retried, not abandoned - but not on every single
// lifecycle tick either. Without a cooldown, a slow or stuck downstream (an interface engine backlog,
// say) turns into an unbounded flood: the same still-unconfirmed visit resent every ~30s forever, with
// more visits joining that pile as their own discharge comes due, never shrinking.
var dischargeResendCooldownMinutes = double.Parse(Environment.GetEnvironmentVariable("CLINICALS_DISCHARGE_RESEND_COOLDOWN_MINUTES") ?? "5");
var outpatientLosMinHours = double.Parse(Environment.GetEnvironmentVariable("CLINICALS_OUTPATIENT_LOS_MIN_HOURS") ?? "1");
var outpatientLosMaxHours = double.Parse(Environment.GetEnvironmentVariable("CLINICALS_OUTPATIENT_LOS_MAX_HOURS") ?? "6");
var inpatientLosMinHours = double.Parse(Environment.GetEnvironmentVariable("CLINICALS_INPATIENT_LOS_MIN_HOURS") ?? "24");
var inpatientLosMaxHours = double.Parse(Environment.GetEnvironmentVariable("CLINICALS_INPATIENT_LOS_MAX_HOURS") ?? "72");
var transferChancePerCheck = double.Parse(Environment.GetEnvironmentVariable("CLINICALS_TRANSFER_CHANCE_PER_CHECK") ?? "0.08");
var edTransferWeight = int.Parse(Environment.GetEnvironmentVariable("CLINICALS_ED_TRANSFER_WEIGHT") ?? "3");
var escalationChancePerCheck = double.Parse(Environment.GetEnvironmentVariable("CLINICALS_A06_CHANCE_PER_CHECK") ?? "0.03");
var demotionChancePerCheck = double.Parse(Environment.GetEnvironmentVariable("CLINICALS_A07_CHANCE_PER_CHECK") ?? "0.05");
var outpatientOrderWeight = int.Parse(Environment.GetEnvironmentVariable("CLINICALS_OUTPATIENT_ORDER_WEIGHT") ?? "3");
var procedureChance = double.Parse(Environment.GetEnvironmentVariable("CLINICALS_PROCEDURE_CHANCE") ?? "0.25");

var state = VisitStateStore.LoadOrCreate(statePath);
Console.WriteLine($"Loaded visit state: {state.Count} people currently known to be in.");
Console.WriteLine($"Listening for ADT/results/reflex orders on :{listenPort}.");
Console.WriteLine($"Sending orders every ~{orderIntervalSeconds}s - LAB {labHost}:{labPort}, RAD {radHost}:{radPort}, PATH {pathHost}:{pathPort}.");
Console.WriteLine($"Checking visit lifecycles (discharge/transfer/class-change) against {adtHost}:{adtPort} every ~{adtIntervalSeconds}s. Ctrl+C to stop.");

var rng = new Random();
using var labMllp = new MllpClient(labHost, labPort);
using var radMllp = new MllpClient(radHost, radPort);
using var pathMllp = new MllpClient(pathHost, pathPort);
var orderClients = new Dictionary<string, MllpClient>(StringComparer.OrdinalIgnoreCase)
{
    ["LAB"] = labMllp,
    ["RAD"] = radMllp,
    ["PATH"] = pathMllp,
};
using var adtMllp = new MllpClient(adtHost, adtPort);
var orderSeq = 1;
var controlIdSeq = 1;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    cts.Cancel();
});

var listener = new MllpListener(listenPort);
var listenerTask = listener.RunAsync(HandleMessageAsync, cts.Token);
var lifecycleLoopTask = LifecycleLoopAsync();

while (!cts.IsCancellationRequested)
{
    try
    {
        await PlaceRandomOrderAsync();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Console.Error.WriteLine($"Order failed: {ex.Message}");
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(orderIntervalSeconds * (0.5 + rng.NextDouble())), cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

await listenerTask;
await lifecycleLoopTask;

async Task LifecycleLoopAsync()
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            await DischargeDueVisitsAsync();
            await MaybeTransferAsync();
            await MaybeEscalateAsync();
            await MaybeDemoteAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Lifecycle check failed: {ex.Message}");
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(adtIntervalSeconds * (0.5 + rng.NextDouble())), cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

// A visit's length of stay is sampled once, at admit/register time (see RecordAdmit/RecordRegister/
// RecordClassChange) - discharge fires when that clock runs out, not on a per-tick coin flip. That's
// the whole point: a patient shouldn't be able to get discharged four seconds after being admitted.
async Task DischargeDueVisitsAsync()
{
    var now = DateTime.UtcNow;
    var cooldown = TimeSpan.FromMinutes(dischargeResendCooldownMinutes);
    var due = state.Snapshot()
        .Where(v => now >= v.PlannedDischargeAt && (v.LastDischargeAttemptAt is not { } last || now - last >= cooldown))
        .ToList();
    foreach (var visit in due)
    {
        var message = AdtMessageBuilder.Build(
            AdtEventType.A03_Discharge,
            ToAdtPatient(visit),
            // Clinicals never tracked the current bed precisely enough to echo it back - the PV1-3
            // fields it never learned just go out blank rather than guessed.
            new AdtVisit(visit.VisitNumber, visit.Class, "", "", "", "", visit.OrderingProvider ?? "", visit.AdmitDateTime),
            insurance: null,
            app, facility, "REGISTRATION", facility,
            NextControlId(), now);

        // Recorded before the send completes, not after - a visit that's still in flight (or whose ack
        // never comes because the connection itself is stuck) must not be retried next tick either.
        state.RecordIn(visit with { LastDischargeAttemptAt = now });
        VisitStateStore.Save(state, statePath);

        var ack = await adtMllp.SendAsync(message, cts.Token);
        var tag = WasAccepted(ack) ? "DISCHARGE" : "DISCHARGE-REJECTED";
        Console.WriteLine($"[{tag}] {visit.PatientFirstName} {visit.PatientLastName} (visit {visit.VisitNumber}) | ack: {SummarizeAck(ack)}");
    }
}

// Only ED and ICU boarders have a defined step-down target - once someone's actually on a ward
// (MS3/MS4/PEDS/L&D) there's nowhere further this simulator sends them, so they're not candidates
// here at all, not just deprioritized. From the ED: critical care if it's serious - PICU for a kid,
// ICU for an adult, never the adult unit for a child - otherwise the age-appropriate ward (PEDS/MS).
// From ICU or PICU: always the matching ward directly, never back to the ED. Someone currently
// boarding in the ED is more likely to actually need the move than someone already in critical care,
// so the pick is weighted, not uniform.
async Task MaybeTransferAsync()
{
    var candidates = state.Snapshot().Where(v => v.Class == PatientClass.Inpatient && v.CurrentUnit is "ED" or "ICU" or "PICU").ToList();
    if (candidates.Count == 0 || rng.NextDouble() >= transferChancePerCheck) return;

    var visit = PickWeighted(candidates, v => v.CurrentUnit == "ED" ? edTransferWeight : 1);
    var unit = StepDownTarget(visit);
    var room = rng.Next(1, 21).ToString("000");
    var bed = ((char)('A' + rng.Next(2))).ToString();

    var message = AdtMessageBuilder.Build(
        AdtEventType.A02_Transfer,
        ToAdtPatient(visit),
        new AdtVisit(visit.VisitNumber, visit.Class, "", unit, room, bed, visit.OrderingProvider ?? "", visit.AdmitDateTime),
        insurance: null,
        app, facility, "REGISTRATION", facility,
        NextControlId(), DateTime.UtcNow);

    var ack = await adtMllp.SendAsync(message, cts.Token);
    var tag = WasAccepted(ack) ? "TRANSFER" : "TRANSFER-REJECTED";
    Console.WriteLine($"[{tag}]  {visit.PatientFirstName} {visit.PatientLastName} (visit {visit.VisitNumber}, currently {visit.CurrentUnit}) -> proposing {unit} | ack: {SummarizeAck(ack)}");
}

// A small chance any given outpatient visit turns out to need admission after all, rather than always
// either discharging clean or having been decided inpatient from the start.
async Task MaybeEscalateAsync()
{
    var outpatients = state.Snapshot().Where(v => v.Class == PatientClass.Outpatient).ToList();
    if (outpatients.Count == 0 || rng.NextDouble() >= escalationChancePerCheck) return;

    var visit = outpatients[rng.Next(outpatients.Count)];
    var message = AdtMessageBuilder.Build(
        AdtEventType.A06_ChangeToInpatient,
        ToAdtPatient(visit),
        // No proposed bed here, unlike a transfer - registration decides placement for a fresh
        // admission-in-place the same way it does for any other admit.
        new AdtVisit(visit.VisitNumber, PatientClass.Inpatient, "", "", "", "", visit.OrderingProvider ?? "", visit.AdmitDateTime),
        insurance: null,
        app, facility, "REGISTRATION", facility,
        NextControlId(), DateTime.UtcNow);

    var ack = await adtMllp.SendAsync(message, cts.Token);
    var tag = WasAccepted(ack) ? "ESCALATE" : "ESCALATE-REJECTED";
    Console.WriteLine($"[{tag}]  {visit.PatientFirstName} {visit.PatientLastName} (visit {visit.VisitNumber}) outpatient -> inpatient | ack: {SummarizeAck(ack)}");
}

// The mirror of MaybeEscalateAsync: an inpatient who no longer needs the bed gets stepped back down
// to outpatient rather than only ever leaving via a full discharge. Frees the bed on registration's
// side same as a discharge would, but the visit stays open.
async Task MaybeDemoteAsync()
{
    var inpatients = state.Snapshot().Where(v => v.Class == PatientClass.Inpatient).ToList();
    if (inpatients.Count == 0 || rng.NextDouble() >= demotionChancePerCheck) return;

    var visit = inpatients[rng.Next(inpatients.Count)];
    var message = AdtMessageBuilder.Build(
        AdtEventType.A07_ChangeToOutpatient,
        ToAdtPatient(visit),
        new AdtVisit(visit.VisitNumber, PatientClass.Outpatient, "", "", "", "", visit.OrderingProvider ?? "", visit.AdmitDateTime),
        insurance: null,
        app, facility, "REGISTRATION", facility,
        NextControlId(), DateTime.UtcNow);

    var ack = await adtMllp.SendAsync(message, cts.Token);
    var tag = WasAccepted(ack) ? "DEMOTE" : "DEMOTE-REJECTED";
    Console.WriteLine($"[{tag}]    {visit.PatientFirstName} {visit.PatientLastName} (visit {visit.VisitNumber}) inpatient -> outpatient | ack: {SummarizeAck(ack)}");
}

// MSA-1 - "AA" is the only accept code this simulator's own AckBuilder ever sends, so anything else
// (or an unparseable ack) is treated as rejected. Every lifecycle action checks this now instead of
// logging whatever came back and assuming it worked - a NAK'd transfer/discharge/class-change didn't
// actually happen, and silently treating it as if it did is exactly how Clinicals' view of the world
// drifts from registration's actual census.
bool WasAccepted(string ack)
{
    try
    {
        return Hl7ParsedMessage.Parse(ack).Field("MSA", 1) == "AA";
    }
    catch
    {
        return false;
    }
}

T PickWeighted<T>(List<T> items, Func<T, int> weight)
{
    var weighted = new List<T>();
    foreach (var item in items)
    {
        for (var i = 0; i < Math.Max(1, weight(item)); i++) weighted.Add(item);
    }
    return weighted[rng.Next(weighted.Count)];
}

// ED can send someone straight to critical care or on to their age-appropriate ward; critical care
// only ever graduates to the matching ward. A visit whose DOB Clinicals never learned (PID-7 missing)
// is treated as an adult - no better information to go on.
string StepDownTarget(Visit visit)
{
    var isChild = visit.DateOfBirth is { } dob && Age(dob) < 18;
    var criticalCare = isChild ? "PICU" : "ICU";
    var ward = isChild ? "PEDS" : rng.NextDouble() < 0.5 ? "MS3" : "MS4";
    return visit.CurrentUnit == "ED" && rng.NextDouble() < 0.5 ? criticalCare : ward;
}

int Age(DateOnly dob)
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var age = today.Year - dob.Year;
    if (dob > today.AddYears(-age)) age--;
    return age;
}

TimeSpan SampleLengthOfStay(PatientClass patientClass) => patientClass == PatientClass.Inpatient
    ? TimeSpan.FromHours(inpatientLosMinHours + rng.NextDouble() * (inpatientLosMaxHours - inpatientLosMinHours))
    : TimeSpan.FromHours(outpatientLosMinHours + rng.NextDouble() * (outpatientLosMaxHours - outpatientLosMinHours));

// Clinicals only ever captured id + name off the wire - everything else PID would normally carry
// (DOB, sex, address, SSN...) it never learned, so those go out blank/unknown rather than invented.
AdtPatient ToAdtPatient(Visit visit) => new(
    visit.PatientId, visit.PatientFirstName, visit.PatientLastName,
    'U', default, "", "", "", "", "", "");

async Task PlaceRandomOrderAsync()
{
    // Only ever order for someone Clinicals currently believes is in - never invent an order for a
    // visit it doesn't know about. Outpatient visits get weighted heavier than inpatient: a short,
    // test-heavy workup vs. sparse routine labs on the floor.
    var visits = state.Snapshot();
    if (visits.Count == 0) return;
    var visit = PickWeighted(visits, v => v.Class == PatientClass.Outpatient ? outpatientOrderWeight : 1);

    // A procedure (colonoscopy, cath, ...) is performed by Clinicals itself, not ordered out to a
    // separate department the way a lab/rad/path test is - nothing leaves Clinicals for this. Only
    // the result would, once that's modeled (see the README's ORU note - not built yet), so there's
    // no HL7 message to send here at all, just the fact that it happened.
    if (visit.Class == PatientClass.Outpatient && rng.NextDouble() < procedureChance)
    {
        var procedure = ClinicalCatalog.Procedures[rng.Next(ClinicalCatalog.Procedures.Length)];
        Console.WriteLine($"[PROCEDURE] {procedure.Department} {procedure.Code} ({procedure.Name}) for {visit.PatientFirstName} {visit.PatientLastName} (visit {visit.VisitNumber}) - performed in-house, no outbound order");
        return;
    }

    var test = visit.Class == PatientClass.Inpatient
        ? ClinicalCatalog.RoutineTests[rng.Next(ClinicalCatalog.RoutineTests.Length)]
        : ClinicalCatalog.OrderableTests[rng.Next(ClinicalCatalog.OrderableTests.Length)];

    if (!orderClients.TryGetValue(test.Department, out var client))
    {
        Console.WriteLine($"[IGNORE]    No route configured for department '{test.Department}' - order not sent");
        return;
    }

    var now = DateTime.UtcNow;
    var placerOrderNumber = NextOrderNumber();

    var message = OrmMessageBuilder.Build(
        new OrmPatient(visit.PatientId, visit.PatientFirstName, visit.PatientLastName, visit.VisitNumber),
        new OrmOrder(placerOrderNumber, null, test.Code, test.Name, visit.OrderingProvider, now),
        test.Department,
        app, facility,
        NextControlId(), now);

    var ack = await client.SendAsync(message, cts.Token);
    Console.WriteLine($"[ORDER]     {test.Department} {test.Code} ({test.Name}) for {visit.PatientFirstName} {visit.PatientLastName} (visit {visit.VisitNumber}), placer {placerOrderNumber} | ack: {SummarizeAck(ack)}");
}

async Task<string> HandleMessageAsync(string rawMessage)
{
    var now = DateTime.UtcNow;
    Hl7ParsedMessage parsed;
    try
    {
        parsed = Hl7ParsedMessage.Parse(rawMessage);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unparseable message, ACKing anyway: {ex.Message}");
        return AckBuilder.Build(app, facility, "?", "?", "", now, accept: true);
    }

    var sendingApp = parsed.Field("MSH", 3) ?? "?";
    var sendingFacility = parsed.Field("MSH", 4) ?? "?";

    // Not a hard validator like registration's own census - Clinicals only ever follows registration's
    // ADT feed, so anything it can't act on yet (an unrecognized event type, a message missing the
    // visit number) is just skipped, not NAK'd. Holding up the queue over something Clinicals doesn't
    // understand isn't its call to make.
    switch (parsed.MessageType)
    {
        case "ADT^A01":
            RecordAdmit(parsed);
            break;
        case "ADT^A04":
            RecordRegister(parsed);
            break;
        case "ADT^A02":
            RecordTransfer(parsed);
            break;
        case "ADT^A03":
            RecordDischarge(parsed);
            break;
        case "ADT^A06":
            RecordClassChangeToInpatient(parsed);
            break;
        case "ADT^A07":
            RecordClassChangeToOutpatient(parsed);
            break;
        case "ORU^R01":
            RecordResult(parsed);
            break;
        case "ORM^O01":
            await RecordReflexOrderAsync(parsed, sendingApp);
            break;
        default:
            Console.WriteLine($"[IGNORE]    {parsed.MessageType} from {sendingApp}/{sendingFacility} - not handled yet");
            break;
    }

    return AckBuilder.Build(app, facility, sendingApp, sendingFacility, parsed.MessageControlId, now, accept: true);
}

void RecordAdmit(Hl7ParsedMessage parsed)
{
    var visitNumber = parsed.Field("PV1", 19);
    var patientId = parsed.Component("PID", 3, 1);
    var lastName = parsed.Component("PID", 5, 1);
    var firstName = parsed.Component("PID", 5, 2);
    var orderingProvider = parsed.Field("PV1", 7);
    var unit = parsed.Component("PV1", 3, 1) ?? "";
    var dob = ParseDob(parsed.Field("PID", 7));

    if (string.IsNullOrEmpty(visitNumber) || string.IsNullOrEmpty(patientId))
    {
        Console.WriteLine("[IGNORE]    A01 missing PV1-19 visit number or PID-3 patient ID");
        return;
    }

    var now = DateTime.UtcNow;
    state.RecordIn(new Visit(visitNumber, patientId, firstName ?? "", lastName ?? "", orderingProvider, PatientClass.Inpatient, unit, dob, now, now + SampleLengthOfStay(PatientClass.Inpatient)));
    VisitStateStore.Save(state, statePath);
    Console.WriteLine($"[IN]        {firstName} {lastName} (visit {visitNumber}), {unit}");
}

void RecordRegister(Hl7ParsedMessage parsed)
{
    var visitNumber = parsed.Field("PV1", 19);
    var patientId = parsed.Component("PID", 3, 1);
    var lastName = parsed.Component("PID", 5, 1);
    var firstName = parsed.Component("PID", 5, 2);
    var orderingProvider = parsed.Field("PV1", 7);
    var dob = ParseDob(parsed.Field("PID", 7));

    if (string.IsNullOrEmpty(visitNumber) || string.IsNullOrEmpty(patientId))
    {
        Console.WriteLine("[IGNORE]    A04 missing PV1-19 visit number or PID-3 patient ID");
        return;
    }

    var now = DateTime.UtcNow;
    state.RecordIn(new Visit(visitNumber, patientId, firstName ?? "", lastName ?? "", orderingProvider, PatientClass.Outpatient, "", dob, now, now + SampleLengthOfStay(PatientClass.Outpatient)));
    VisitStateStore.Save(state, statePath);
    Console.WriteLine($"[REGISTER]  {firstName} {lastName} (visit {visitNumber})");
}

DateOnly? ParseDob(string? field) =>
    !string.IsNullOrEmpty(field) && DateOnly.TryParseExact(field, "yyyyMMdd", out var dob) ? dob : null;

void RecordTransfer(Hl7ParsedMessage parsed)
{
    var visitNumber = parsed.Field("PV1", 19);
    var unit = parsed.Component("PV1", 3, 1);
    if (string.IsNullOrEmpty(visitNumber) || unit is null) return;

    var visit = state.Get(visitNumber);
    if (visit is null) return; // not tracked - same "not our call to flag" stance as everything else here

    state.RecordIn(visit with { CurrentUnit = unit });
    VisitStateStore.Save(state, statePath);
    Console.WriteLine($"[MOVE]      visit {visitNumber} -> {unit}");
}

void RecordClassChangeToInpatient(Hl7ParsedMessage parsed)
{
    var visitNumber = parsed.Field("PV1", 19);
    if (string.IsNullOrEmpty(visitNumber)) return;

    var visit = state.Get(visitNumber);
    if (visit is null) return;

    var unit = parsed.Component("PV1", 3, 1) ?? "";
    var now = DateTime.UtcNow;
    state.RecordIn(visit with { Class = PatientClass.Inpatient, CurrentUnit = unit, PlannedDischargeAt = now + SampleLengthOfStay(PatientClass.Inpatient) });
    VisitStateStore.Save(state, statePath);
    Console.WriteLine($"[PROMOTE]   visit {visitNumber} outpatient -> inpatient, {unit}");
}

void RecordClassChangeToOutpatient(Hl7ParsedMessage parsed)
{
    var visitNumber = parsed.Field("PV1", 19);
    if (string.IsNullOrEmpty(visitNumber)) return;

    var visit = state.Get(visitNumber);
    if (visit is null) return;

    // Fresh outpatient timeline from this point - same idea as the fresh inpatient one a promotion
    // gets, just the other direction. The freed bed itself isn't Clinicals' state to track.
    var now = DateTime.UtcNow;
    state.RecordIn(visit with { Class = PatientClass.Outpatient, CurrentUnit = "", PlannedDischargeAt = now + SampleLengthOfStay(PatientClass.Outpatient) });
    VisitStateStore.Save(state, statePath);
    Console.WriteLine($"[DEMOTE]    visit {visitNumber} inpatient -> outpatient");
}

void RecordDischarge(Hl7ParsedMessage parsed)
{
    var visitNumber = parsed.Field("PV1", 19);
    if (string.IsNullOrEmpty(visitNumber))
    {
        Console.WriteLine("[IGNORE]    A03 missing PV1-19 visit number");
        return;
    }

    if (!state.RecordOut(visitNumber))
    {
        Console.WriteLine($"[IGNORE]    A03 for visit {visitNumber} - not currently tracked, nothing to remove");
        return;
    }

    VisitStateStore.Save(state, statePath);
    Console.WriteLine($"[OUT]       visit {visitNumber}");
}

void RecordResult(Hl7ParsedMessage parsed)
{
    var visitNumber = parsed.Field("PV1", 19);
    var testCode = parsed.Component("OBR", 4, 1);
    var testName = parsed.Component("OBR", 4, 2);
    var value = parsed.Field("OBX", 5);
    var flag = parsed.Field("OBX", 8);
    var flagSuffix = string.IsNullOrEmpty(flag) ? "" : $" [{flag}]";
    Console.WriteLine($"[RESULT]    {testCode} ({testName}) = {value}{flagSuffix} for visit {visitNumber}");
}

// A department originated this order on its own (a reflex, e.g. an abnormal TSH reflexing to a Free
// T4) - it can supply its own filler number but not a placer number, since it isn't the placer.
// Clinicals is, so it assigns one here and replies with an update (ORC-1 XO) carrying both numbers,
// rather than just ACKing and leaving the department's own order un-numbered on Clinicals' side.
async Task RecordReflexOrderAsync(Hl7ParsedMessage parsed, string sendingApp)
{
    var orderControl = parsed.Field("ORC", 1);
    if (orderControl != "NW")
    {
        Console.WriteLine($"[IGNORE]    ORM with ORC-1 '{orderControl}' from {sendingApp} - not handled");
        return;
    }

    var patientId = parsed.Component("PID", 3, 1);
    var lastName = parsed.Component("PID", 5, 1) ?? "";
    var firstName = parsed.Component("PID", 5, 2) ?? "";
    var visitNumber = parsed.Field("PV1", 19) is { Length: > 0 } pv1Visit ? pv1Visit : parsed.Field("PID", 18) ?? "";
    var fillerOrderNumber = parsed.Field("ORC", 3);
    var testCode = parsed.Component("OBR", 4, 1);
    var testName = parsed.Component("OBR", 4, 2) ?? testCode ?? "";
    var orderingProvider = parsed.Field("ORC", 12);

    if (string.IsNullOrEmpty(patientId) || string.IsNullOrEmpty(fillerOrderNumber) || string.IsNullOrEmpty(testCode))
    {
        Console.WriteLine($"[IGNORE]    Reflex order from {sendingApp} missing PID-3, ORC-3, or OBR-4");
        return;
    }

    if (!orderClients.TryGetValue(sendingApp, out var client))
    {
        Console.WriteLine($"[IGNORE]    Reflex order from unrecognized department '{sendingApp}' - no route to reply on");
        return;
    }

    var placerOrderNumber = NextOrderNumber();
    var now = DateTime.UtcNow;

    var message = OrmMessageBuilder.Build(
        new OrmPatient(patientId, firstName, lastName, visitNumber),
        new OrmOrder(placerOrderNumber, fillerOrderNumber, testCode, testName, orderingProvider, now, "XO"),
        sendingApp, app, facility,
        NextControlId(), now);

    var ack = await client.SendAsync(message, cts.Token);
    Console.WriteLine($"[REFLEX]    Assigned placer {placerOrderNumber} for {testCode} ({testName}) filler {fillerOrderNumber} from {sendingApp} | ack: {SummarizeAck(ack)}");
}

string NextOrderNumber() => $"ORD{DateTime.UtcNow:yyyyMMddHHmmss}{orderSeq++:0000}";

string NextControlId() => $"CL{DateTime.UtcNow:yyyyMMddHHmmss}{controlIdSeq++:0000}";

string SummarizeAck(string ack) => ack.Length > 60 ? ack[..60].Replace('\r', '|') + "..." : ack.Replace('\r', '|');
