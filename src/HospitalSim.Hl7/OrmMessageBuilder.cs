namespace HospitalSim.Hl7;

public sealed record OrmPatient(string PatientId, string PatientName, string VisitNumber);

public sealed record OrmOrder(
    string OrderNumber,
    string TestCode,
    string TestName,
    string? OrderingProvider,
    DateTime OrderDateTime);

/// <summary>
/// Builds a minimal ORM^O01 (MSH/PID/ORC/OBR) new-order message. Unlike the ADT broadcast, this is
/// point-to-point - MSH-5 names the specific department fulfilling the order (LAB/RAD/PATH), which
/// ClinicalsXL uses to route it.
/// </summary>
public static class OrmMessageBuilder
{
    public static string Build(
        OrmPatient patient,
        OrmOrder order,
        string department,
        string sendingApplication,
        string sendingFacility,
        string messageControlId,
        DateTime eventDateTime)
    {
        var ts = eventDateTime.ToString("yyyyMMddHHmmss");
        var orderTs = order.OrderDateTime.ToString("yyyyMMddHHmmss");

        var msg = new Hl7Message();

        msg.AddSegment("MSH",
            "^~\\&",
            sendingApplication,
            sendingFacility,
            department,
            sendingFacility,
            ts,
            "",
            "ORM^O01",
            messageControlId,
            "P",
            "2.5.1");

        // PID-18 carries the visit number too, as an alternative to PV1-19 - not every downstream
        // system looks in the same place, and this one's cheap to populate both ways.
        var pid = new string[18];
        pid[0] = "1";
        pid[2] = $"{patient.PatientId}^^^{sendingFacility}^MR";
        pid[4] = patient.PatientName;
        pid[17] = patient.VisitNumber;
        for (var i = 0; i < pid.Length; i++) pid[i] ??= "";
        msg.AddSegment("PID", pid);

        // PV1-19 is the primary visit number location.
        var pv1 = new string[19];
        pv1[0] = "1";
        pv1[18] = patient.VisitNumber;
        for (var i = 0; i < pv1.Length; i++) pv1[i] ??= "";
        msg.AddSegment("PV1", pv1);

        // ORC-1 order control (NW = new order), ORC-2 placer order number, ORC-9 transaction date/time,
        // ORC-12 ordering provider.
        var orc = new string[12];
        orc[0] = "NW";
        orc[1] = order.OrderNumber;
        orc[8] = ts;
        orc[11] = order.OrderingProvider ?? "";
        for (var i = 0; i < orc.Length; i++) orc[i] ??= "";
        msg.AddSegment("ORC", orc);

        // OBR-1 set id, OBR-2 placer order number, OBR-4 universal service id, OBR-7 observation
        // date/time, OBR-16 ordering provider.
        var obr = new string[16];
        obr[0] = "1";
        obr[1] = order.OrderNumber;
        obr[3] = $"{order.TestCode}^{order.TestName}^LOCAL";
        obr[6] = orderTs;
        obr[15] = order.OrderingProvider ?? "";
        for (var i = 0; i < obr.Length; i++) obr[i] ??= "";
        msg.AddSegment("OBR", obr);

        return msg.Build();
    }
}
