using System.Runtime.InteropServices;

using HospitalSim.Clinicals;
using HospitalSim.Hl7;
using HospitalSim.World;

var listenPort = int.Parse(Environment.GetEnvironmentVariable("CLINICALS_LISTEN_PORT") ?? "6680");
var statePath = Environment.GetEnvironmentVariable("CLINICALS_STATE_PATH") ?? "state.json";
var app = Environment.GetEnvironmentVariable("CLINICALS_APP") ?? "CLINICALS";
var facility = Environment.GetEnvironmentVariable("CLINICALS_FACILITY") ?? "MRMC";
var orderHost = Environment.GetEnvironmentVariable("CLINICALS_ORDER_MLLP_HOST") ?? "localhost";
var orderPort = int.Parse(Environment.GetEnvironmentVariable("CLINICALS_ORDER_MLLP_PORT") ?? "6662");
var orderIntervalSeconds = double.Parse(Environment.GetEnvironmentVariable("CLINICALS_ORDER_INTERVAL_SECONDS") ?? "20");

var state = VisitStateStore.LoadOrCreate(statePath);
Console.WriteLine($"Loaded visit state: {state.Count} people currently known to be in.");
Console.WriteLine($"Listening for ADT on :{listenPort}.");
Console.WriteLine($"Sending orders to {orderHost}:{orderPort} every ~{orderIntervalSeconds}s. Ctrl+C to stop.");

var rng = new Random();
using var orderMllp = new MllpClient(orderHost, orderPort);
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

async Task PlaceRandomOrderAsync()
{
    // Only ever order for someone Clinicals currently believes is admitted - never invent an order
    // for a visit it doesn't know about.
    var visit = state.RandomVisit(rng);
    if (visit is null) return;

    var test = ClinicalCatalog.OrderableTests[rng.Next(ClinicalCatalog.OrderableTests.Length)];
    var now = DateTime.UtcNow;
    var orderNumber = $"ORD{now:yyyyMMddHHmmss}{orderSeq++:0000}";

    var message = OrmMessageBuilder.Build(
        new OrmPatient(visit.PatientId, visit.PatientName, visit.VisitNumber),
        new OrmOrder(orderNumber, test.Code, test.Name, visit.OrderingProvider, now),
        test.Department,
        app, facility,
        NextControlId(), now);

    var ack = await orderMllp.SendAsync(message, cts.Token);
    Console.WriteLine($"[ORDER]     {test.Department} {test.Code} ({test.Name}) for {visit.PatientName} (visit {visit.VisitNumber}) | ack: {SummarizeAck(ack)}");
}

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
        case "ADT^A03":
            RecordDischarge(parsed);
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

    if (string.IsNullOrEmpty(visitNumber) || string.IsNullOrEmpty(patientId))
    {
        Console.WriteLine("[IGNORE]    A01 missing PV1-19 visit number or PID-3 patient ID");
        return;
    }

    state.RecordIn(new Visit(visitNumber, patientId, $"{firstName} {lastName}".Trim(), orderingProvider, DateTime.UtcNow));
    VisitStateStore.Save(state, statePath);
    Console.WriteLine($"[IN]        {firstName} {lastName} (visit {visitNumber})");
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

string NextControlId() => $"CL{DateTime.UtcNow:yyyyMMddHHmmss}{controlIdSeq++:0000}";

string SummarizeAck(string ack) => ack.Length > 60 ? ack[..60].Replace('\r', '|') + "..." : ack.Replace('\r', '|');
