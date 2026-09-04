using HospitalSim.Hl7;

var listenPort = int.Parse(Environment.GetEnvironmentVariable("SINK_LISTEN_PORT") ?? "6670");
var sinkApp = Environment.GetEnvironmentVariable("SINK_APP") ?? "SINK";
var sinkFacility = Environment.GetEnvironmentVariable("SINK_FACILITY") ?? "SINK";

Console.WriteLine($"MLLP sink listening on :{listenPort} - every message gets an AA ack, nothing is stored or routed.");
Console.WriteLine("Fan multiple outbound interfaces at this one port; each connection is handled independently. Ctrl+C to stop.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var received = 0;

var listener = new MllpListener(listenPort);
await listener.RunAsync(HandleMessage, cts.Token);

Task<string> HandleMessage(string rawMessage)
{
    var count = Interlocked.Increment(ref received);
    var now = DateTime.UtcNow;

    string sendingApp, sendingFacility, controlId, messageType;
    try
    {
        var parsed = Hl7ParsedMessage.Parse(rawMessage);
        sendingApp = parsed.Field("MSH", 3) ?? "?";
        sendingFacility = parsed.Field("MSH", 4) ?? "?";
        controlId = parsed.MessageControlId;
        messageType = string.IsNullOrEmpty(parsed.MessageType) ? "?" : parsed.MessageType;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{count:00000}] Unparseable message, ACKing anyway: {ex.Message}");
        return Task.FromResult(AckBuilder.Build(sinkApp, sinkFacility, "?", "?", "", now, accept: true));
    }

    Console.WriteLine($"[{count:00000}] ACK {messageType} from {sendingApp}/{sendingFacility} (control id {controlId})");

    return Task.FromResult(AckBuilder.Build(sinkApp, sinkFacility, sendingApp, sendingFacility, controlId, now, accept: true));
}
