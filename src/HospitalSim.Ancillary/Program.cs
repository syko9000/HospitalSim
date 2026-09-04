using System.Runtime.InteropServices;

using HospitalSim.Ancillary;
using HospitalSim.Hl7;
using HospitalSim.World;

var department = (Environment.GetEnvironmentVariable("ANCILLARY_DEPARTMENT") ?? "LAB").ToUpperInvariant();
var app = Environment.GetEnvironmentVariable("ANCILLARY_APP") ?? department;
var facility = Environment.GetEnvironmentVariable("ANCILLARY_FACILITY") ?? "WRMC";
var clinicalsApp = Environment.GetEnvironmentVariable("ANCILLARY_CLINICALS_APP") ?? "CLINICALS";
var listenPort = int.Parse(Environment.GetEnvironmentVariable("ANCILLARY_LISTEN_PORT") ?? "6690");
var statePath = Environment.GetEnvironmentVariable("ANCILLARY_STATE_PATH") ?? "state.json";
var resultHost = Environment.GetEnvironmentVariable("ANCILLARY_RESULT_MLLP_HOST") ?? "localhost";
var resultPort = int.Parse(Environment.GetEnvironmentVariable("ANCILLARY_RESULT_MLLP_PORT") ?? "6680");
var checkIntervalSeconds = double.Parse(Environment.GetEnvironmentVariable("ANCILLARY_CHECK_INTERVAL_SECONDS") ?? "30");

var state = PendingResultStore.LoadOrCreate(statePath);
Console.WriteLine($"[{department}] Loaded pending results: {state.Count} order(s) in progress.");
Console.WriteLine($"[{department}] Listening for orders on :{listenPort}.");
Console.WriteLine($"[{department}] Sending results/reflex orders to {resultHost}:{resultPort} every ~{checkIntervalSeconds}s. Ctrl+C to stop.");

var rng = new Random();
using var resultMllp = new MllpClient(resultHost, resultPort);
var controlIdSeq = 1;
var fillerSeq = 1;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
// docker stop / a compose recreate sends SIGTERM, not SIGINT - same reasoning as every other service
// here: without this, a send that already reached the wire but hasn't been recorded yet as pending-
// removed could double up on restart.
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    cts.Cancel();
});

var listener = new MllpListener(listenPort);
var listenerTask = listener.RunAsync(HandleMessageAsync, cts.Token);

while (!cts.IsCancellationRequested)
{
    try
    {
        await ProcessDueResultsAsync();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Console.Error.WriteLine($"[{department}] Result check failed: {ex.Message}");
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds * (0.5 + rng.NextDouble())), cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

await listenerTask;

async Task ProcessDueResultsAsync()
{
    var now = DateTime.UtcNow;
    var due = state.Snapshot().Where(r => now >= r.ResultDueAt).ToList();
    foreach (var pending in due)
    {
        await SendResultAsync(pending);
        state.Remove(pending.FillerOrderNumber);
        PendingResultStore.Save(state, statePath);

        await MaybeReflexAsync(pending);
    }
}

async Task SendResultAsync(PendingResult pending)
{
    // Not modeling real reference ranges/numeric values - "looks realistic" matters more here than
    // clinically accurate content, same stance as the rest of this catalog.
    var abnormal = rng.NextDouble() < 0.15;
    var value = abnormal ? "Abnormal - see report" : "Within normal limits";

    var message = OruMessageBuilder.Build(
        new OruPatient(pending.PatientId, pending.PatientFirstName, pending.PatientLastName, pending.VisitNumber),
        new OruResult(pending.PlacerOrderNumber, pending.FillerOrderNumber, pending.TestCode, pending.TestName, value, abnormal ? "A" : "", pending.OrderingProvider, DateTime.UtcNow),
        app, facility, clinicalsApp,
        NextControlId(), DateTime.UtcNow);

    var ack = await resultMllp.SendAsync(message, cts.Token);
    Console.WriteLine($"[{department}-RESULT] {pending.TestCode} ({pending.TestName}) = {value} for {pending.PatientFirstName} {pending.PatientLastName} (visit {pending.VisitNumber}, placer {pending.PlacerOrderNumber}) | ack: {SummarizeAck(ack)}");
}

// The classic lab/path pattern: the performing department decides on its own to run a follow-up test,
// the ordering clinician never asked for it up front. This department is the one originating the
// order here, not Clinicals - so it can't assign a placer number, only its own filler number.
async Task MaybeReflexAsync(PendingResult pending)
{
    var rule = ClinicalCatalog.ReflexRules.FirstOrDefault(r => r.TriggerCode == pending.TestCode);
    if (rule is null || rng.NextDouble() >= rule.Chance) return;

    var reflexTest = ClinicalCatalog.OrderableTests.FirstOrDefault(t => t.Code == rule.ReflexCode);
    if (reflexTest is null) return; // catalog mismatch - shouldn't happen, nothing sane to do here

    var fillerOrderNumber = NextFillerOrderNumber();
    var now = DateTime.UtcNow;

    // Recorded before the send, not after - Clinicals can reply with the placer-number update fast
    // enough that it would otherwise arrive (on a separate inbound connection, handled concurrently)
    // before this coroutine got back around to storing the pending record, and get discarded as
    // untracked. Own turnaround starts immediately either way - this department can run the reflex
    // test right away, it doesn't need to wait on Clinicals' placer-number paperwork first. If the
    // update genuinely never arrives, the result just goes out later with a blank placer number.
    state.Upsert(new PendingResult(
        fillerOrderNumber, "",
        pending.PatientId, pending.PatientFirstName, pending.PatientLastName, pending.VisitNumber,
        reflexTest.Code, reflexTest.Name, pending.OrderingProvider,
        now, now + SampleTurnaround(reflexTest)));
    PendingResultStore.Save(state, statePath);

    var message = OrmMessageBuilder.Build(
        new OrmPatient(pending.PatientId, pending.PatientFirstName, pending.PatientLastName, pending.VisitNumber),
        new OrmOrder(null, fillerOrderNumber, reflexTest.Code, reflexTest.Name, pending.OrderingProvider, now),
        clinicalsApp, app, facility,
        NextControlId(), now);

    var ack = await resultMllp.SendAsync(message, cts.Token);
    Console.WriteLine($"[{department}-REFLEX] {pending.TestCode} -> {reflexTest.Code} ({reflexTest.Name}) for {pending.PatientFirstName} {pending.PatientLastName} (visit {pending.VisitNumber}), filler {fillerOrderNumber} | ack: {SummarizeAck(ack)}");
}

TimeSpan SampleTurnaround(OrderableTest test) =>
    TimeSpan.FromMinutes(test.MinTurnaroundMinutes + rng.NextDouble() * (test.MaxTurnaroundMinutes - test.MinTurnaroundMinutes));

Task<string> HandleMessageAsync(string rawMessage) => Task.FromResult(HandleMessage(rawMessage));

string HandleMessage(string rawMessage)
{
    var now = DateTime.UtcNow;
    Hl7ParsedMessage parsed;
    try
    {
        parsed = Hl7ParsedMessage.Parse(rawMessage);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{department}] Unparseable message, ACKing anyway: {ex.Message}");
        return AckBuilder.Build(app, facility, "?", "?", "", now, accept: true);
    }

    var sendingApp = parsed.Field("MSH", 3) ?? "?";
    var sendingFacility = parsed.Field("MSH", 4) ?? "?";

    // Lenient, same stance as Clinicals' own inbound ADT handling - this department only ever follows
    // what Clinicals sends it, so anything unrecognized is skipped, not NAK'd.
    switch (parsed.MessageType)
    {
        case "ORM^O01":
            RecordOrder(parsed);
            break;
        default:
            Console.WriteLine($"[{department}] [IGNORE] {parsed.MessageType} from {sendingApp}/{sendingFacility} - not handled");
            break;
    }

    return AckBuilder.Build(app, facility, sendingApp, sendingFacility, parsed.MessageControlId, now, accept: true);
}

void RecordOrder(Hl7ParsedMessage parsed)
{
    var orderControl = parsed.Field("ORC", 1);

    if (orderControl == "XO")
    {
        // Clinicals telling us the placer number for a reflex order we sent it earlier.
        var updateFillerNumber = parsed.Field("ORC", 3);
        var updatePlacerNumber = parsed.Field("ORC", 2);
        if (string.IsNullOrEmpty(updateFillerNumber) || string.IsNullOrEmpty(updatePlacerNumber))
        {
            Console.WriteLine($"[{department}] [IGNORE] Placer-number update missing ORC-2 or ORC-3");
            return;
        }

        var existing = state.Get(updateFillerNumber);
        if (existing is null)
        {
            Console.WriteLine($"[{department}] [IGNORE] Placer-number update for filler {updateFillerNumber} - not currently tracked (already resulted?)");
            return;
        }

        state.Upsert(existing with { PlacerOrderNumber = updatePlacerNumber });
        PendingResultStore.Save(state, statePath);
        Console.WriteLine($"[{department}-PLACER] filler {updateFillerNumber} now has placer {updatePlacerNumber}");
        return;
    }

    if (orderControl != "NW")
    {
        Console.WriteLine($"[{department}] [IGNORE] ORM with ORC-1 '{orderControl}' - not handled");
        return;
    }

    var patientId = parsed.Component("PID", 3, 1);
    var lastName = parsed.Component("PID", 5, 1) ?? "";
    var firstName = parsed.Component("PID", 5, 2) ?? "";
    var visitNumber = parsed.Field("PV1", 19) is { Length: > 0 } pv1Visit ? pv1Visit : parsed.Field("PID", 18) ?? "";
    var placerOrderNumberIn = parsed.Field("ORC", 2) ?? "";
    var testCode = parsed.Component("OBR", 4, 1);
    var testName = parsed.Component("OBR", 4, 2) ?? testCode ?? "";
    var orderingProvider = parsed.Field("ORC", 12);

    if (string.IsNullOrEmpty(patientId) || string.IsNullOrEmpty(testCode))
    {
        Console.WriteLine($"[{department}] [IGNORE] New order missing PID-3 patient ID or OBR-4 test code");
        return;
    }

    var test = ClinicalCatalog.OrderableTests.FirstOrDefault(t => t.Code == testCode);
    var turnaround = test is not null ? SampleTurnaround(test) : TimeSpan.FromMinutes(30 + rng.NextDouble() * 30);

    var fillerOrderNumber = NextFillerOrderNumber();
    var now = DateTime.UtcNow;
    state.Upsert(new PendingResult(
        fillerOrderNumber, placerOrderNumberIn,
        patientId, firstName, lastName, visitNumber,
        testCode, testName, orderingProvider,
        now, now + turnaround));
    PendingResultStore.Save(state, statePath);

    Console.WriteLine($"[{department}-ACCEPT] {testCode} ({testName}) for {firstName} {lastName} (visit {visitNumber}), placer {placerOrderNumberIn} -> filler {fillerOrderNumber}, due in ~{turnaround.TotalMinutes:0} min");
}

string NextFillerOrderNumber() => $"{department}{DateTime.UtcNow:yyyyMMddHHmmss}{fillerSeq++:0000}";

string NextControlId() => $"{department}{DateTime.UtcNow:yyyyMMddHHmmss}{controlIdSeq++:0000}";

string SummarizeAck(string ack) => ack.Length > 60 ? ack[..60].Replace('\r', '|') + "..." : ack.Replace('\r', '|');
