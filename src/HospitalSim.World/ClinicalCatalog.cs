namespace HospitalSim.World;

/// <summary>Something Clinicals can order - carries which department (LAB/RAD/PATH) fulfills it, so a
/// consumer can pick a test and already know both what to ask for and where to route it.</summary>
public sealed record OrderableTest(string Code, string Name, string Department);

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
        new("CBC", "Complete Blood Count", "LAB"),
        new("BMP", "Basic Metabolic Panel", "LAB"),
        new("CMP", "Comprehensive Metabolic Panel", "LAB"),
        new("TROP", "Troponin", "LAB"),
        new("LIPID", "Lipid Panel", "LAB"),
        new("UA", "Urinalysis", "LAB"),
        new("BCX2", "Blood Culture x2", "LAB"),
        new("PTINR", "PT/INR", "LAB"),
        new("TSH", "Thyroid Stimulating Hormone", "LAB"),
        new("A1C", "Hemoglobin A1c", "LAB"),
        new("LACT", "Lactate", "LAB"),
        new("ABG", "Arterial Blood Gas", "LAB"),
        new("TS", "Type and Screen", "LAB"),

        new("CXR", "Chest X-ray, 2 View", "RAD"),
        new("CTHEAD", "CT Head without Contrast", "RAD"),
        new("CTAP", "CT Abdomen/Pelvis with Contrast", "RAD"),
        new("MRIBR", "MRI Brain without Contrast", "RAD"),
        new("USAB", "Ultrasound Abdomen", "RAD"),
        new("KUB", "KUB (Kidneys, Ureters, Bladder)", "RAD"),
        new("CTCHEST", "CT Chest with Contrast", "RAD"),
        new("XREXT", "X-ray, Extremity", "RAD"),

        new("SURGPATH", "Surgical Pathology - Tissue Biopsy", "PATH"),
        new("FROZEN", "Frozen Section", "PATH"),
        new("PAP", "Cytology - Pap Smear", "PATH"),
        new("BMBX", "Bone Marrow Biopsy", "PATH"),
        new("SKINBX", "Skin Biopsy", "PATH"),
    ];
}
