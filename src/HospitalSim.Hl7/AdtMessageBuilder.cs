namespace HospitalSim.Hl7;

public enum AdtEventType
{
    A01_Admit,
    A02_Transfer,
    A03_Discharge,
}

public sealed record AdtPatient(
    string PatientId,
    string FirstName,
    string LastName,
    char Sex,
    DateOnly DateOfBirth,
    string Ssn,
    string AddressLine1,
    string City,
    string State,
    string ZipCode,
    string PhoneNumber);

public sealed record AdtVisit(
    string VisitNumber,
    string PriorPointOfCare,
    string PointOfCare,
    string Room,
    string Bed,
    string AttendingDoctorId,
    string AttendingDoctorLastName,
    string AttendingDoctorFirstName,
    DateTime AdmitDateTime);

public sealed record AdtInsurance(string CompanyId, string CompanyName, string PolicyNumber);

/// <summary>
/// Builds minimal ADT^A01 (admit), ADT^A02 (transfer), and ADT^A03 (discharge) messages with
/// MSH/EVN/PID/PV1/OBX/IN1 segments.
/// </summary>
public static class AdtMessageBuilder
{
    public static string Build(
        AdtEventType eventType,
        AdtPatient patient,
        AdtVisit visit,
        AdtInsurance insurance,
        string sendingApplication,
        string sendingFacility,
        string receivingApplication,
        string receivingFacility,
        string messageControlId,
        DateTime eventDateTime,
        string? chiefComplaint = null)
    {
        var (triggerEvent, messageTypeCode) = eventType switch
        {
            AdtEventType.A01_Admit => ("A01", "ADT^A01"),
            AdtEventType.A02_Transfer => ("A02", "ADT^A02"),
            AdtEventType.A03_Discharge => ("A03", "ADT^A03"),
            _ => throw new ArgumentOutOfRangeException(nameof(eventType)),
        };

        var ts = eventDateTime.ToString("yyyyMMddHHmmss");
        var dob = patient.DateOfBirth.ToString("yyyyMMdd");
        var admitTs = visit.AdmitDateTime.ToString("yyyyMMddHHmmss");

        var msg = new Hl7Message();

        msg.AddSegment("MSH",
            "^~\\&",
            sendingApplication,
            sendingFacility,
            receivingApplication,
            receivingFacility,
            ts,
            "",
            messageTypeCode,
            messageControlId,
            "P",
            "2.5.1");

        msg.AddSegment("EVN",
            triggerEvent,
            ts);

        msg.AddSegment("PID",
            "1",
            "",
            $"{patient.PatientId}^^^{sendingFacility}^MR",
            "",
            $"{patient.LastName}^{patient.FirstName}",
            "",
            dob,
            patient.Sex.ToString(),
            "",
            "",
            $"{patient.AddressLine1}^^{patient.City}^{patient.State}^{patient.ZipCode}",
            "",
            patient.PhoneNumber,
            "",
            "",
            "",
            "",
            "",
            patient.Ssn);

        // PV1 field positions below are 1-indexed to match the HL7 spec (PV1-2 patient class,
        // PV1-3 assigned location, PV1-7 attending doctor, PV1-19 visit number, PV1-44/45 admit/
        // discharge date-time) - build via an explicit array so the gaps stay obviously intentional.
        var pv1 = new string[45];
        pv1[0] = "1";
        pv1[1] = "I";
        pv1[2] = $"{visit.PointOfCare}^{visit.Room}^{visit.Bed}^{sendingFacility}";
        pv1[6] = $"{visit.AttendingDoctorId}^{visit.AttendingDoctorLastName}^{visit.AttendingDoctorFirstName}";
        pv1[18] = visit.VisitNumber;
        if (eventType == AdtEventType.A03_Discharge)
        {
            pv1[43] = admitTs;
            pv1[44] = ts;
        }
        for (var i = 0; i < pv1.Length; i++) pv1[i] ??= "";
        msg.AddSegment("PV1", pv1);

        if (!string.IsNullOrEmpty(chiefComplaint))
        {
            // OBX-3 8661-1^Chief Complaint^LN (LOINC), OBX-5 the value, OBX-11 F = final result.
            // Must precede IN1 - that's where OBX falls in the ADT^A0x segment order.
            msg.AddSegment("OBX",
                "1",
                "TX",
                "8661-1^Chief Complaint^LN",
                "",
                Hl7Message.EscapeField(chiefComplaint),
                "", "", "", "", "",
                "F");
        }

        // IN1-2 plan ID, IN1-3 company ID, IN1-4 company name, IN1-16 name of insured, IN1-36 policy number.
        var in1 = new string[36];
        in1[0] = "1";
        in1[1] = $"{insurance.CompanyId}^{insurance.CompanyName}";
        in1[2] = insurance.CompanyId;
        in1[3] = insurance.CompanyName;
        in1[15] = $"{patient.LastName}^{patient.FirstName}";
        in1[35] = insurance.PolicyNumber;
        for (var i = 0; i < in1.Length; i++) in1[i] ??= "";
        msg.AddSegment("IN1", in1);

        return msg.Build();
    }
}
