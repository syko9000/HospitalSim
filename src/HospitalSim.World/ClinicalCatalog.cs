namespace HospitalSim.World;

/// <summary>Something Clinicals can order - carries which department (LAB/RAD/PATH) fulfills it, so a
/// consumer can pick a test and already know both what to ask for and where to route it. Turnaround is
/// a realistic range for that specific test, not a single global guess - a culture or a surgical
/// pathology read genuinely takes a day or more, a STAT lactate comes back in minutes.</summary>
public sealed record OrderableTest(
    string Code,
    string Name,
    string Department,
    int MinTurnaroundMinutes = 30,
    int MaxTurnaroundMinutes = 60);

/// <summary>A result for TriggerCode has a Chance of the performing department firing a follow-up
/// order for ReflexCode on its own - the classic lab/path "reflex" pattern (e.g. an abnormal TSH
/// reflexing to a Free T4) rather than the ordering clinician having to think of it up front.</summary>
public sealed record ReflexRule(string TriggerCode, string ReflexCode, double Chance);

/// <summary>
/// Reference data for clinical content (as opposed to Names, which is world-generation-time-only word
/// lists) - shared by anything that needs to describe why a patient is there, not just who they are.
/// </summary>
public static class ClinicalCatalog
{
    public static readonly string[] ChiefComplaints =
    [
        "Chest pain", "Abdominal pain", "Shortness of breath", "Fever", "Headache",
        "Back pain", "Dizziness", "Nausea and vomiting", "Laceration", "Fall",
        "Motor vehicle accident", "Syncope", "Altered mental status", "Chest tightness", "Cough",
        "Allergic reaction", "Fracture, suspected", "Weakness", "Palpitations", "Flank pain"
    ];

    // Codes are a plain local scheme, not a real coding system (unlike the chief complaint's LOINC
    // 8661-1) - deliberately not claiming specific LOINC/CPT numbers here without being sure they're
    // right for a demo tool where "looks realistic" matters more than "is exactly correct."
    public static readonly OrderableTest[] OrderableTests =
    [
        new("CBC", "Complete Blood Count", "LAB", 20, 45),
        new("BMP", "Basic Metabolic Panel", "LAB", 20, 45),
        new("CMP", "Comprehensive Metabolic Panel", "LAB", 30, 60),
        new("TROP", "Troponin", "LAB", 20, 40),
        new("LIPID", "Lipid Panel", "LAB", 45, 90),
        new("UA", "Urinalysis", "LAB", 20, 40),
        new("BCX2", "Blood Culture x2", "LAB", 1440, 4320), // 1-3 days - needs incubation, not a quick result
        new("PTINR", "PT/INR", "LAB", 15, 30),
        new("TSH", "Thyroid Stimulating Hormone", "LAB", 60, 120),
        new("A1C", "Hemoglobin A1c", "LAB", 45, 90),
        new("LACT", "Lactate", "LAB", 15, 30),
        new("ABG", "Arterial Blood Gas", "LAB", 10, 20),
        new("TS", "Type and Screen", "LAB", 30, 60),
        new("FT4", "Free T4", "LAB", 60, 120), // TSH's reflex

        new("CXR", "Chest X-ray, 2 View", "RAD", 30, 60),
        new("CTHEAD", "CT Head without Contrast", "RAD", 60, 120),
        new("CTAP", "CT Abdomen/Pelvis with Contrast", "RAD", 90, 150),
        new("MRIBR", "MRI Brain without Contrast", "RAD", 120, 240),
        new("USAB", "Ultrasound Abdomen", "RAD", 60, 120),
        new("KUB", "KUB (Kidneys, Ureters, Bladder)", "RAD", 45, 90),
        new("CTCHEST", "CT Chest with Contrast", "RAD", 90, 150),
        new("XREXT", "X-ray, Extremity", "RAD", 30, 60),

        new("SURGPATH", "Surgical Pathology - Tissue Biopsy", "PATH", 1440, 4320), // 1-3 days
        new("FROZEN", "Frozen Section", "PATH", 20, 40), // done intra-op, deliberately fast
        new("PAP", "Cytology - Pap Smear", "PATH", 1440, 2880),
        new("BMBX", "Bone Marrow Biopsy", "PATH", 1440, 4320),
        new("SKINBX", "Skin Biopsy", "PATH", 1440, 4320),
        new("IHC", "Immunohistochemistry Stain", "PATH", 1440, 2880), // SURGPATH's reflex
    ];

    // The small subset of OrderableTests that make sense as a daily/routine check on an inpatient -
    // the rest are acute/diagnostic, appropriate for a workup, not a standing order.
    private static readonly string[] RoutineTestCodes = ["CBC", "BMP", "A1C", "UA", "LACT"];

    public static readonly OrderableTest[] RoutineTests =
        [.. OrderableTests.Where(t => RoutineTestCodes.Contains(t.Code))];

    // Two realistic, well-known reflex patterns - not a general rules engine, just enough to show the
    // pattern: the performing department orders a follow-up test on its own, the ordering clinician
    // never asked for it up front.
    public static readonly ReflexRule[] ReflexRules =
    [
        new("TSH", "FT4", 0.3),
        new("SURGPATH", "IHC", 0.2),
    ];

    // Same shape as OrderableTest (code/name/department) - a same-day outpatient procedure is ordered
    // and fulfilled the same way a lab/rad/path test is, just against its own department. There's no
    // single "procedures department" in a real hospital, though - each of these is actually performed
    // by a different specialty service, so each gets its own, not a shared bucket. Turnaround doesn't
    // apply - nothing is ever ordered out for a procedure (see HospitalSim.Clinicals), so the default
    // is meaningless filler, never read.
    public static readonly OrderableTest[] Procedures =
    [
        new("COLO", "Colonoscopy", "GI"),
        new("EGD", "Upper Endoscopy (EGD)", "GI"),
        new("CATH", "Cardiac Catheterization", "CATH"),
        new("KNEEARTHRO", "Arthroscopy - Knee", "ORTHO"),
        new("CYSTO", "Cystoscopy", "URO"),
        new("SKINEXC", "Skin Lesion Excision", "SURG"),
    ];
}
