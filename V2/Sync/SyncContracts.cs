using System.Text.Json.Serialization;

namespace E_Dnevnik_API.V2.Sync
{
    // tijelo POST /v2/sync - kolačići su za ocjene.skole.hr, path /
    public class SyncRequest
    {
        public Guid? ChildId { get; set; }
        public List<SyncCookie>? Cookies { get; set; }
    }

    public class SyncCookie
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
    }

    // 200 odgovor (EdnevnikSummary u aplikaciji)
    public class SyncResponse
    {
        public string? School { get; set; }
        public string? ClassName { get; set; }
        public string? HomeroomTeacher { get; set; }
        public int SubjectCount { get; set; }
        public string? WeakestSubject { get; set; }
        public double? WeakestAverage { get; set; }
        public string? NextTestSubject { get; set; }

        // yyyy-MM-dd ili null
        public string? NextTestOn { get; set; }
        public int NewGrades { get; set; }
        public List<string> UnmappedSubjects { get; set; } = new();
    }

    public class SyncError
    {
        [JsonPropertyName("error")]
        public string Error { get; set; } = "";
    }

    public static class SyncErrorCodes
    {
        public const string Unauthorized = "unauthorized";
        public const string Forbidden = "forbidden";
        public const string BadRequest = "bad_request";
        public const string SessionInvalid = "ednevnik_session_invalid";
        public const string Unavailable = "ednevnik_unavailable";
        public const string NotConfigured = "not_configured";
        public const string AuthUnavailable = "auth_unavailable";
        public const string Internal = "internal";
    }

    // e-Dnevnik nas je vratio na /login - kolačić je mrtav
    public class EdnevnikSessionInvalidException : Exception
    {
        public EdnevnikSessionInvalidException()
            : base("e-Dnevnik session is not valid.") { }
    }

    // e-Dnevnik 5xx, timeout ili parsiranje nije uspjelo
    public class EdnevnikUnavailableException : Exception
    {
        public EdnevnikUnavailableException(string message, Exception? inner = null)
            : base(message, inner) { }
    }
}
