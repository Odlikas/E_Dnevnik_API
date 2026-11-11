using E_Dnevnik_API.V2.Sync;

namespace E_Dnevnik_API.V2.Identity
{
    // tijelo POST /v2/identity: kolačići s mobitela srednjoškolca i korisničko ime kojim se prijavio
    public class IdentityRequest
    {
        // AAI korisničko ime (ime.prezime@skole.hr); koristi se samo ako ga profil ne pokazuje
        public string? Username { get; set; }
        public List<SyncCookie>? Cookies { get; set; }
    }

    // 200 odgovor: HsStudentAccount u aplikaciji (+ childId i stanje synca)
    public class IdentityResponse
    {
        public Guid ChildId { get; set; }
        public bool Created { get; set; }
        public string FirstName { get; set; } = "";
        public string FullName { get; set; } = "";
        public string? School { get; set; }
        public string? ClassName { get; set; }
        public string? Program { get; set; }

        // roditelji kako ih e-Dnevnik piše ("Ana Anić"), najviše dva
        public List<string> ParentNames { get; set; } = new();
        public int SubjectCount { get; set; }

        // yyyy-MM-dd ili null kad ga profil ne pokazuje
        public string? BirthDate { get; set; }
        public bool NeedsParentConsent { get; set; }

        // false: račun je vezan, ali ocjene se nisu povukle (e-Dnevnik nije odgovorio)
        public bool Synced { get; set; }
    }

    public static class IdentityErrorCodes
    {
        // profil nije srednja škola (razred 5-8, OŠ) ili se razred ne da pročitati
        public const string NotHighSchool = "not_high_school";

        // poslano korisničko ime ne odgovara imenu na profilu
        public const string IdentityMismatch = "identity_mismatch";

        // ovaj Supabase korisnik je već drugo dijete (uparen mobitel) ili roditelj
        public const string AlreadyBound = "already_bound";
    }

    // greška iz public.ednevnik_bind_identity
    public class IdentityBindException : Exception
    {
        public IdentityBindException(string code)
            : base(code) => Code = code;

        public string Code { get; }
    }
}
