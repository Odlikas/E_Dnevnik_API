using System.Text.Json;

namespace E_Dnevnik_API.V2.Ai
{
    // model vraća {"text": "...", "steps": ["..."]}; ako ne poštuje oblik, cijeli tekst je odgovor
    public static class AskAnswerParser
    {
        public const int MaxText = 3000;
        public const int MaxSteps = 8;
        public const int MaxStep = 500;

        public static AskMessage Parse(string raw)
        {
            var trimmed = raw.Trim();
            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmed[start..(end + 1)]);
                    var root = doc.RootElement;
                    if (
                        root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String
                    )
                    {
                        var steps = new List<string>();
                        if (root.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array)
                            foreach (var step in s.EnumerateArray())
                                if (step.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(step.GetString()))
                                    steps.Add(Cut(step.GetString()!.Trim(), MaxStep));
                        var body = text.GetString()!.Trim();
                        if (body.Length > 0 || steps.Count > 0)
                            return new AskMessage
                            {
                                Text = Cut(body.Length > 0 ? body : "Evo kako:", MaxText),
                                Steps = steps.Take(MaxSteps).ToList(),
                            };
                    }
                }
                catch (JsonException) { }
            }
            return new AskMessage { Text = Cut(StripFence(trimmed), MaxText) };
        }

        private static string StripFence(string s) =>
            s.StartsWith("```") ? s.Trim('`').Replace("json\n", "", StringComparison.OrdinalIgnoreCase).Trim() : s;

        private static string Cut(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }
}
