namespace HospitalSim.Hl7;

public enum AdtEventType
{
    A01_Admit,
    A02_Transfer,
    A03_Discharge,
    A04_Register,
    A06_ChangeToInpatient,
    A07_ChangeToOutpatient,
}

/// <summary>PV1-2. Only the two values this simulator ever actually produces - not the full HL7 table.</summary>
public enum PatientClass
{
    Inpatient,
    Outpatient,
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
    PatientClass Class,
    string PriorPointOfCare,
    string PointOfCare,
    string Room,
    string Bed,
    // Pre-composed PV1-7 component string (e.g. "DOC001^Smith^Jane"), not separate id/last/first
    // fields - a sender that only ever captured this field as opaque text off the wire (Clinicals,
    // echoing what it heard from registration) has no other way to supply it.
    string AttendingDoctor,
    DateTime AdmitDateTime);

public sealed record AdtInsurance(string CompanyId, string CompanyName, string PolicyNumber);

// RelationshipCode/RelationshipDescription follow HL7 user table 0063 (e.g. "SPO"/"Spouse",
// "PAR"/"Parent", "CHD"/"Child") - describes this person's relationship *to the patient*.
public sealed record AdtNextOfKin(
    string FirstName, string LastName, string RelationshipCode, string RelationshipDescription,
    string AddressLine1, string City, string State, string ZipCode, string PhoneNumber,
    char Sex, DateOnly DateOfBirth);

public sealed record AdtGuarantor(
    string GuarantorId, string FirstName, string LastName, string RelationshipCode, string RelationshipDescription,
    string AddressLine1, string City, string State, string ZipCode, string PhoneNumber,
    char Sex, DateOnly DateOfBirth, string Ssn);

/// <summary>
/// Builds minimal ADT^A01 (inpatient admit), ADT^A02 (transfer), ADT^A03 (discharge), ADT^A04
/// (outpatient register), ADT^A06 (outpatient -> inpatient class change), and ADT^A07 (inpatient ->
/// outpatient class change) messages with MSH/EVN/PID/NK1/PV1/OBX/GT1/IN1 segments.
/// </summary>
public static class AdtMessageBuilder
{
    public static string Build(
        AdtEventType eventType,
        AdtPatient patient,
        AdtVisit visit,
        AdtInsurance? insurance,
        string sendingApplication,
        string sendingFacility,
        string receivingApplication,
        string receivingFacility,
        string messageControlId,
        DateTime eventDateTime,
        string? chiefComplaint = null,
        AdtNextOfKin? nextOfKin = null,
        AdtGuarantor? guarantor = null)
    {
        var (triggerEvent, messageTypeCode) = eventType switch
        {
            AdtEventType.A01_Admit => ("A01", "ADT^A01"),
            AdtEventType.A02_Transfer => ("A02", "ADT^A02"),
            AdtEventType.A03_Discharge => ("A03", "ADT^A03"),
            AdtEventType.A04_Register => ("A04", "ADT^A04"),
            AdtEventType.A06_ChangeToInpatient => ("A06", "ADT^A06"),
            AdtEventType.A07_ChangeToOutpatient => ("A07", "ADT^A07"),
            _ => throw new ArgumentOutOfRangeException(nameof(eventType)),
        };

        var ts = eventDateTime.ToString("yyyyMMddHHmmss");
        // Blank rather than a misleading epoch date when the sender never captured a DOB (Clinicals,
        // whose PID is otherwise just id + name).
        var dob = patient.DateOfBirth == default ? "" : patient.DateOfBirth.ToString("yyyyMMdd");
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

        // NK1 falls between PID and PV1 in the ADT^A01 message structure - not after PV1/OBX like
        // GT1/IN1 below. Omitted entirely when the patient has no known next of kin (an independent
        // adult with no tracked spouse, parent, or child) rather than sending an empty segment.
        if (nextOfKin is not null)
        {
            var nk1 = new string[16];
            nk1[0] = "1";
            nk1[1] = $"{Hl7Message.EscapeField(nextOfKin.LastName)}^{Hl7Message.EscapeField(nextOfKin.FirstName)}";
            nk1[2] = $"{nextOfKin.RelationshipCode}^{nextOfKin.RelationshipDescription}";
            nk1[3] = $"{Hl7Message.EscapeField(nextOfKin.AddressLine1)}^^{Hl7Message.EscapeField(nextOfKin.City)}^{nextOfKin.State}^{nextOfKin.ZipCode}";
            nk1[4] = nextOfKin.PhoneNumber;
            nk1[14] = nextOfKin.Sex.ToString();
            nk1[15] = nextOfKin.DateOfBirth.ToString("yyyyMMdd");
            for (var i = 0; i < nk1.Length; i++) nk1[i] ??= "";
            msg.AddSegment("NK1", nk1);
        }

        // PV1 field positions below are 1-indexed to match the HL7 spec (PV1-2 patient class,
        // PV1-3 assigned location, PV1-7 attending doctor, PV1-19 visit number, PV1-44/45 admit/
        // discharge date-time) - build via an explicit array so the gaps stay obviously intentional.
        var pv1 = new string[45];
        pv1[0] = "1";
        pv1[1] = visit.Class == PatientClass.Inpatient ? "I" : "O";
        // PointOfCare is a nursing unit name/id someone configured, not system-generated like Room/Bed
        // - "L&D" is a real example already in this catalog, and a literal & is HL7's subcomponent
        // separator (part of this project's own encoding chars, "^~\&"). Escape it rather than assume
        // no unit name will ever collide with an encoding character.
        pv1[2] = $"{Hl7Message.EscapeField(visit.PointOfCare)}^{visit.Room}^{visit.Bed}^{Hl7Message.EscapeField(sendingFacility)}";
        pv1[6] = visit.AttendingDoctor;
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

        // GT1 falls after OBX and before the IN1/insurance group in the ADT^A01 message structure.
        // Guarantor is nearly always known (every resident has one, even if it's themselves) but stays
        // optional here the same way insurance does, since Clinicals never tracked it either.
        if (guarantor is not null)
        {
            var gt1 = new string[12];
            gt1[0] = "1";
            gt1[1] = guarantor.GuarantorId;
            gt1[2] = $"{Hl7Message.EscapeField(guarantor.LastName)}^{Hl7Message.EscapeField(guarantor.FirstName)}";
            gt1[4] = $"{Hl7Message.EscapeField(guarantor.AddressLine1)}^^{Hl7Message.EscapeField(guarantor.City)}^{guarantor.State}^{guarantor.ZipCode}";
            gt1[5] = guarantor.PhoneNumber;
            gt1[7] = guarantor.DateOfBirth.ToString("yyyyMMdd");
            gt1[8] = guarantor.Sex.ToString();
            gt1[10] = $"{guarantor.RelationshipCode}^{guarantor.RelationshipDescription}";
            gt1[11] = guarantor.Ssn;
            for (var i = 0; i < gt1.Length; i++) gt1[i] ??= "";
            msg.AddSegment("GT1", gt1);
        }

        // IN1-2 plan ID, IN1-3 company ID, IN1-4 company name, IN1-16 name of insured, IN1-36 policy number.
        // Omitted entirely when the sender doesn't carry insurance at all (Clinicals never learned it).
        if (insurance is not null)
        {
            var in1 = new string[36];
            in1[0] = "1";
            in1[1] = $"{insurance.CompanyId}^{insurance.CompanyName}";
            in1[2] = insurance.CompanyId;
            in1[3] = insurance.CompanyName;
            // The insured is whoever actually holds the policy - the guarantor, not necessarily the
            // patient themselves (a dependent child's insurance is in a parent's name). Falls back to
            // the patient's own name only when no guarantor was supplied at all.
            in1[15] = guarantor is not null
                ? $"{Hl7Message.EscapeField(guarantor.LastName)}^{Hl7Message.EscapeField(guarantor.FirstName)}"
                : $"{patient.LastName}^{patient.FirstName}";
            in1[35] = insurance.PolicyNumber;
            for (var i = 0; i < in1.Length; i++) in1[i] ??= "";
            msg.AddSegment("IN1", in1);
        }

        return msg.Build();
    }
}
