namespace HospitalSim.Hl7;

public sealed record OruPatient(string PatientId, string FirstName, string LastName, string VisitNumber);

// AbnormalFlag follows HL7 table 0078 ("" normal, "A" abnormal, ...) - kept as a plain string rather
// than an enum since this is the only place that needs it and the table has far more values than this
// simulator will ever produce.
public sealed record OruResult(
    string PlacerOrderNumber,
    string FillerOrderNumber,
    string TestCode,
    string TestName,
    string ResultValue,
    string AbnormalFlag,
    string? OrderingProvider,
    DateTime ObservationDateTime);

/// <summary>
/// Builds a minimal ORU^R01 (MSH/PID/PV1/ORC/OBR/OBX) result message - a department reporting a
/// finished result back to whoever placed the order. Point-to-point, same as the order that prompted
/// it: MSH-5 names Clinicals specifically, not a broadcast.
/// </summary>
public static class OruMessageBuilder
{
    public static string Build(
        OruPatient patient,
        OruResult result,
        string sendingApplication,
        string sendingFacility,
        string receivingApplication,
        string messageControlId,
        DateTime eventDateTime)
    {
        var ts = eventDateTime.ToString("yyyyMMddHHmmss");
        var obsTs = result.ObservationDateTime.ToString("yyyyMMddHHmmss");

        var msg = new Hl7Message();

        msg.AddSegment("MSH",
            "^~\\&",
            sendingApplication,
            sendingFacility,
            receivingApplication,
            sendingFacility,
            ts,
            "",
            "ORU^R01",
            messageControlId,
            "P",
            "2.5.1");

        var pid = new string[18];
        pid[0] = "1";
        pid[2] = patient.PatientId;
        pid[4] = $"{patient.LastName}^{patient.FirstName}";
        pid[17] = patient.VisitNumber;
        for (var i = 0; i < pid.Length; i++) pid[i] ??= "";
        msg.AddSegment("PID", pid);

        var pv1 = new string[19];
        pv1[0] = "1";
        pv1[18] = patient.VisitNumber;
        for (var i = 0; i < pv1.Length; i++) pv1[i] ??= "";
        msg.AddSegment("PV1", pv1);

        // ORC-1 SC (status change) - this is reporting on an order already placed, not placing a new
        // one. ORC-2/3 carry the same placer/filler pair the order itself used, so the result
        // correlates back without anything else to go on.
        var orc = new string[12];
        orc[0] = "SC";
        orc[1] = result.PlacerOrderNumber;
        orc[2] = result.FillerOrderNumber;
        orc[8] = ts;
        orc[11] = result.OrderingProvider ?? "";
        for (var i = 0; i < orc.Length; i++) orc[i] ??= "";
        msg.AddSegment("ORC", orc);

        // OBR-25 F = final result.
        var obr = new string[25];
        obr[0] = "1";
        obr[1] = result.PlacerOrderNumber;
        obr[2] = result.FillerOrderNumber;
        obr[3] = $"{result.TestCode}^{result.TestName}^LOCAL";
        obr[6] = obsTs;
        obr[15] = result.OrderingProvider ?? "";
        obr[24] = "F";
        for (var i = 0; i < obr.Length; i++) obr[i] ??= "";
        msg.AddSegment("OBR", obr);

        // OBX-2 ST (plain string result - this simulator isn't modeling real reference ranges/units),
        // OBX-3 test code^name^LOCAL, OBX-8 abnormal flags, OBX-11 F = final.
        msg.AddSegment("OBX",
            "1",
            "ST",
            $"{result.TestCode}^{result.TestName}^LOCAL",
            "",
            Hl7Message.EscapeField(result.ResultValue),
            "", "",
            result.AbnormalFlag,
            "", "",
            "F");

        return msg.Build();
    }
}
