namespace HospitalSim.Hl7;

public sealed record OrmPatient(string PatientId, string FirstName, string LastName, string VisitNumber);

// PlacerOrderNumber/FillerOrderNumber are independently optional - who's allowed to assign which is the
// whole point of the reflex handshake: Clinicals (the placer) hands out placer numbers, a department
// (the filler) hands out its own filler numbers, and each side only ever fills in its own.
//   - Clinicals places a normal order: PlacerOrderNumber set, FillerOrderNumber blank (department
//     assigns that itself on receipt), OrderControlCode "NW".
//   - A department fires a reflex order of its own: PlacerOrderNumber blank (it can't assign one),
//     FillerOrderNumber set (its own), OrderControlCode "NW".
//   - Clinicals acknowledges a reflex order and assigns it a placer number: both numbers set (echoing
//     the filler number back), OrderControlCode "XO" (change/update, not a new order).
public sealed record OrmOrder(
    string? PlacerOrderNumber,
    string? FillerOrderNumber,
    string TestCode,
    string TestName,
    string? OrderingProvider,
    DateTime OrderDateTime,
    string OrderControlCode = "NW");

/// <summary>
/// Builds a minimal ORM^O01 (MSH/PID/ORC/OBR) order message - a new order, a reflex order a
/// department originated on its own, or Clinicals' placer-number update replying to one. Unlike the
/// ADT broadcast, this is point-to-point - MSH-5 names the specific department (LAB/RAD/PATH), which
/// a downstream interface engine's translation step would use to route it.
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
        var placer = order.PlacerOrderNumber ?? "";
        var filler = order.FillerOrderNumber ?? "";

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
        // PID-3 is bare here - just the MR in PID-3.1, no assigning authority or identifier type.
        // Consistent with Clinicals' general dialect: minimal, only what a department actually needs
        // to route and fulfill the order.
        var pid = new string[18];
        pid[0] = "1";
        pid[2] = patient.PatientId;
        pid[4] = $"{patient.LastName}^{patient.FirstName}";
        pid[17] = patient.VisitNumber;
        for (var i = 0; i < pid.Length; i++) pid[i] ??= "";
        msg.AddSegment("PID", pid);

        // PV1-19 is the primary visit number location.
        var pv1 = new string[19];
        pv1[0] = "1";
        pv1[18] = patient.VisitNumber;
        for (var i = 0; i < pv1.Length; i++) pv1[i] ??= "";
        msg.AddSegment("PV1", pv1);

        // ORC-1 order control (NW = new order, XO = change/update an existing one), ORC-2 placer order
        // number, ORC-3 filler order number, ORC-9 transaction date/time, ORC-12 ordering provider.
        var orc = new string[12];
        orc[0] = order.OrderControlCode;
        orc[1] = placer;
        orc[2] = filler;
        orc[8] = ts;
        orc[11] = order.OrderingProvider ?? "";
        for (var i = 0; i < orc.Length; i++) orc[i] ??= "";
        msg.AddSegment("ORC", orc);

        // OBR-1 set id, OBR-2 placer order number, OBR-3 filler order number, OBR-4 universal service
        // id, OBR-7 observation date/time, OBR-16 ordering provider.
        var obr = new string[16];
        obr[0] = "1";
        obr[1] = placer;
        obr[2] = filler;
        obr[3] = $"{order.TestCode}^{order.TestName}^LOCAL";
        obr[6] = orderTs;
        obr[15] = order.OrderingProvider ?? "";
        for (var i = 0; i < obr.Length; i++) obr[i] ??= "";
        msg.AddSegment("OBR", obr);

        return msg.Build();
    }
}
