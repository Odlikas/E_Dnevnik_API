using System.Text.Json.Nodes;

namespace E_Dnevnik_API.V2.Ai
{
    // tijelo POST /v2/ai/photo: izrezani dio slike koji je dijete označilo
    public class PhotoRequest
    {
        // base64 JPEG ili PNG
        public string? Image { get; set; }

        // math | croatian | english | nature (PhotoSubject u aplikaciji)
        public string? Subject { get; set; }
    }

    // 200 odgovor = redak public.photo_explanations; Steps u obliku study_notes.body.steps
    public class PhotoResponse
    {
        public Guid Id { get; set; }
        public string Subject { get; set; } = "";
        public string Expression { get; set; } = "";
        public string Topic { get; set; } = "";
        public string Answer { get; set; } = "";
        public JsonArray Steps { get; set; } = new();
        public JsonArray SimilarTasks { get; set; } = new();
    }

    public static class PhotoErrorCodes
    {
        public const string Unreadable = "unreadable";
        public const string PhotoHelpOff = "photo_help_off";
        public const string SubjectNotAllowed = "subject_not_allowed";
    }

    // roditeljeva "Pomoć sa slikanjem" (child_settings.photo_help_*); Subjects null = svi predmeti
    public record PhotoHelpRules(bool Enabled, string Level, int DailyCap, short[]? Subjects)
    {
        public static readonly PhotoHelpRules Default = new(true, "steps", 10, null);
    }

    // što je model pročitao i objasnio, već provjereno i skraćeno
    public record PhotoResult(string Expression, string Topic, string Answer, JsonArray Steps, JsonArray SimilarTasks);

    public static class PhotoSubjects
    {
        public static readonly string[] All = { "math", "croatian", "english", "nature" };

        // subjects.id: MAT 1, HRV 2, ENG 3, PID 4 / PRI 5 / BIO 6
        public static bool Allowed(string subject, short[]? allowed) =>
            allowed is null
            || subject switch
            {
                "math" => allowed.Contains((short)1),
                "croatian" => allowed.Contains((short)2),
                "english" => allowed.Contains((short)3),
                _ => allowed.Any(id => id is 4 or 5 or 6),
            };

        public static string Name(string subject) =>
            subject switch
            {
                "math" => "Matematika",
                "croatian" => "Hrvatski jezik",
                "english" => "Engleski jezik",
                _ => "Priroda i društvo",
            };
    }
}
