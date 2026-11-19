using System.Text.Json.Serialization;

namespace E_Dnevnik_API.V2.Ai
{
    // tijelo POST /v2/ai/ask
    public class AskRequest
    {
        public string? Question { get; set; }

        // zadatak iz kviza kad dijete otvori Josu s "Trebaš pomoć?"; inače null
        public AskTask? Task { get; set; }
    }

    // pitanje iz kviza: Answer i Given samo nakon što je dijete odgovorilo
    public class AskTask
    {
        public string? Prompt { get; set; }
        public List<string>? Choices { get; set; }
        public string? Given { get; set; }
        public string? Answer { get; set; }
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
        // samo ovi modeli, redom; sljedeći kad prethodni ne odgovori. Nikad "auto": freellmapi bi mogao
        // odabrati necenzurirane ili roleplay modele, a ovo je aplikacija za djecu.
        // env: Ai__ChatModels / Ai__VisionModels = popis odvojen zarezima
        public string ChatModels { get; set; } =
            "gemini-3.1-flash-lite,gemini-3-flash-preview,gemini-flash-lite,llama-3.3-70b,gemma-4-26b-it";
        public string VisionModels { get; set; } =
            "gemini-3-flash-preview,gemini-3.1-flash-lite,gemini-3.5-flash-lite,gemini-flash-lite,gemma-4-26b-it";

        // jedan pokušaj s jednim modelom
        public int AttemptSeconds { get; set; } = 25;

        public static IReadOnlyList<string> Split(string models) =>
            models.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(m => !m.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
                .ToList();

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

    public record AiReply(AskMessage Message, int InputTokens, int OutputTokens, string Model = "");
}
