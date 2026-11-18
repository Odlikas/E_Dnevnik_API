using System.Text.Json.Nodes;

namespace E_Dnevnik_API.V2.Ai
{
    // POST /v2/ai/explain {"notificationId"}: roditelj traži od Jose da djetetu objasni temu
    public class ExplainRequest
    {
        public Guid? NotificationId { get; set; }
    }

    public class ExplainResponse
    {
        public Guid NoteId { get; set; }
        public string Title { get; set; } = "";
        public int Steps { get; set; }
        public bool WasFree { get; set; }
    }

    public static class ExplainErrorCodes
    {
        public const string NotFound = "not_found";
        public const string PremiumRequired = "premium_required";
        public const string NothingToExplain = "nothing_to_explain";
    }

    // zahtjev za pomoć kako ga vidi roditelj iz te obitelji
    public record HelpContext(
        Guid NotificationId,
        Guid ChildId,
        Guid FamilyId,
        string ChildName,
        short? Grade,
        string? SchoolLevel,
        string Plan,
        string? Topic,
        string? LastQuestion
    )
    {
        public bool Premium => Plan == "premium";
        public string Source => $"help:{NotificationId}";
    }

    public record ExistingNote(Guid Id, string Title, int Steps);

    // bilješka koju je napisao model; Sections u obliku study_notes.body.sections
    public record ExplainNote(string Title, short SubjectId, string Topic, JsonArray Sections, string? JosoSays);
}
