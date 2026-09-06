namespace HospitalSim.World;

internal static class Names
{
    public static readonly string[] Male =
    [
        "James", "Robert", "John", "Michael", "David", "William", "Richard", "Joseph", "Thomas", "Charles",
        "Daniel", "Matthew", "Anthony", "Mark", "Steven", "Andrew", "Paul", "Joshua", "Kenneth", "Kevin",
        "Brian", "George", "Edward", "Ronald", "Timothy", "Jason", "Jeffrey", "Ryan", "Jacob", "Gary",
        "Eric", "Stephen", "Jonathan", "Larry", "Justin", "Scott", "Brandon", "Raymond", "Gregory", "Frank"
    ];

    public static readonly string[] Female =
    [
        "Mary", "Patricia", "Jennifer", "Linda", "Elizabeth", "Barbara", "Susan", "Jessica", "Sarah", "Karen",
        "Nancy", "Lisa", "Margaret", "Betty", "Sandra", "Ashley", "Dorothy", "Kimberly", "Emily", "Donna",
        "Michelle", "Carol", "Amanda", "Melissa", "Deborah", "Stephanie", "Rebecca", "Laura", "Sharon", "Cynthia",
        "Amy", "Kathleen", "Angela", "Shirley", "Brenda", "Emma", "Anna", "Pamela", "Nicole", "Samantha"
    ];

    public static readonly string[] Surnames =
    [
        "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez",
        "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson", "Thomas", "Taylor", "Moore", "Jackson", "Martin",
        "Lee", "Perez", "Thompson", "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson",
        "Walker", "Young", "Allen", "King", "Wright", "Scott", "Torres", "Nguyen", "Hill", "Flores"
    ];

    public static readonly string[] Specialties =
    [
        "Family Medicine", "Internal Medicine", "Cardiology", "Emergency Medicine", "General Surgery",
        "Orthopedic Surgery", "Pediatrics", "Obstetrics and Gynecology", "Neurology", "Psychiatry",
        "Radiology", "Anesthesiology", "Pathology", "Oncology", "Pulmonology",
        "Gastroenterology", "Nephrology", "Endocrinology", "Urology", "Dermatology"
    ];

    public static readonly string[] StreetNames =
    [
        "Maple", "Oak", "Elm", "Cedar", "Birch", "Willow", "Chestnut", "Pine", "Walnut", "Spruce",
        "Sycamore", "Magnolia", "Hickory", "Poplar", "Sunset", "Ridge", "Meadow", "River", "Lake", "Church"
    ];

    public static readonly string[] StreetSuffixes = ["St", "Ave", "Dr", "Ln", "Rd", "Ct", "Way", "Blvd"];

    public static readonly string[] InsuranceRoots =
    [
        "Heartland", "Cornerstone", "Pinnacle", "Meridian", "Everwell", "Trustmark", "Bridgepoint", "Summit"
    ];

    public static readonly string[] InsuranceSuffixes =
    [
        "Health Partners", "Mutual", "Insurance Group", "Health Plan", "Assurance"
    ];

    // Combined with OutOfTownSuffixes below (14 x 12 = 168 combinations) for emigrated residents'
    // addresses - a synthetic town name pool built the same procedural way as street names, rather
    // than a hand-authored list of exactly 100, since neither risks colliding with a real place.
    public static readonly string[] OutOfTownPrefixes =
    [
        "Fair", "River", "Amber", "Stone", "Elm", "North", "South", "New", "Green", "Clear",
        "Silver", "Cross", "Deer", "Ash"
    ];

    public static readonly string[] OutOfTownSuffixes =
    [
        "view", "field", "ton", "ville", "burg", "dale", "wood", "port", "haven", "ridge", "town", "boro"
    ];

    // 10 states within a plausible drive/relocation radius of Ohio, each with a real zip-code leading
    // digit so a generated zip at least starts right, without needing a full per-state zip range table.
    public static readonly (string State, string ZipPrefix)[] OutOfTownStates =
    [
        ("OH", "4"), ("PA", "1"), ("IN", "4"), ("KY", "4"), ("WV", "2"),
        ("MI", "4"), ("IL", "6"), ("TN", "3"), ("NC", "2"), ("VA", "2")
    ];
}
