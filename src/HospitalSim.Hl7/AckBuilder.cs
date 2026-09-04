namespace HospitalSim.Hl7;

public static class AckBuilder
{
    public static string Build(
        string sendingApplication,
        string sendingFacility,
        string receivingApplication,
        string receivingFacility,
        string ackingControlId,
        DateTime timestamp,
        bool accept,
        string? errorText = null)
    {
        var msg = new Hl7Message();
        msg.AddSegment("MSH",
            "^~\\&",
            sendingApplication,
            sendingFacility,
            receivingApplication,
            receivingFacility,
            timestamp.ToString("yyyyMMddHHmmss"),
            "",
            "ACK",
            $"ACK{ackingControlId}",
            "P",
            "2.5.1");
        msg.AddSegment("MSA",
            accept ? "AA" : "AE",
            ackingControlId,
            Hl7Message.EscapeField(errorText));
        return msg.Build();
    }
}
