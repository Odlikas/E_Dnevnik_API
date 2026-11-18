using System.Text.Json.Serialization;

namespace E_Dnevnik_API.V2.Ai
{
    // tijelo POST /v2/ai/ask
    public class AskRequest
    {
        public string? Question { get; set; }
    }

    // jedna Josina poruka; razlomci kao {1/2}
    public class AskMessage
    {
        public string Text { get; set; } = "";
        public List<string> Steps { get; set; } = new();
    }

    // 200 odgovor; QuestionsLeft null = Odlikaš+ (bez dnevnog limita)
    public class AskResponse
    {
        public List<AskMessage> Messages { get; set; } = new();

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int? QuestionsLeft { get; set; }
    }

    public static class AiErrorCodes
    {
        public const string LimitReached = "limit_reached";
        public const string NotAChild = "not_a_child";
        public const string AiUnavailable = "ai_unavailable";
    }

    // postavke iz sekcije "Ai" (env Ai__BaseUrl, Ai__ApiKey, Ai__Model, Ai__Provider)
    public class AiOptions
    {
        // freellmapi na istom serveru, port samo na 127.0.0.1
        public string BaseUrl { get; set; } = "http://127.0.0.1:3001/v1";
        public string? ApiKey { get; set; }
        public string Model { get; set; } = "auto";

        // upisuje se u ai_calls.provider
        public string Provider { get; set; } = "freellmapi";

        // besplatni paket: pitanja po danu (dan počinje u 7:00 po zagrebačkom vremenu)
        public int FreeQuestionsPerDay { get; set; } = 5;

        // Odlikaš+ nema limita u aplikaciji; ovo je samo kočnica protiv zloupotrebe
        public int PremiumQuestionsPerDay { get; set; } = 50;
    }

    // dijete koje pita; Plan je families.plan ('free' | 'premium')
    public record Asker(Guid ChildId, Guid FamilyId, short? Grade, string? SchoolLevel, string Plan)
    {
        public bool Premium => Plan == "premium";
    }

    public record ChatTurn(bool FromChild, string Text);

    public record AiReply(AskMessage Message, int InputTokens, int OutputTokens);
}
