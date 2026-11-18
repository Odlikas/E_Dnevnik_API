using System.Text.Json;
using System.Text.Json.Nodes;

namespace E_Dnevnik_API.V2.Ai
{
    // model vraća JSON iz ExplainPrompt; sve se provjeri i skrati prije upisa
    public static class ExplainAnswerParser
    {
        private const int MaxSections = 4;
        private const int MaxLines = 4;

        // isti brojevi kao subject_id u aplikaciji (MAT 1, HRV 2, ENG 3, priroda 4)
        private static readonly Dictionary<string, short> SubjectIds = new()
        {
            ["math"] = 1,
            ["croatian"] = 2,
            ["english"] = 3,
            ["nature"] = 4,
        };

        // null = model je odbio ili nije poštovao oblik
        public static ExplainNote? Parse(string raw)
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start)
                return null;
            JsonObject? root;
            try
            {
                root = JsonNode.Parse(raw[start..(end + 1)]) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
            if (root is null || Bool(root["ok"]) == false)
                return null;
            var title = Text(root["title"], 60);
            if (title is null)
                return null;

            var sections = new JsonArray();
            if (root["sections"] is JsonArray raws)
                foreach (var s in raws.OfType<JsonObject>())
                {
                    if (sections.Count == MaxSections)
                        break;
                    var heading = Text(s["title"], 60);
                    var lines = new JsonArray();
                    if (s["lines"] is JsonArray l)
                        foreach (var line in l)
                            if (Text(line, 300) is { } t && lines.Count < MaxLines)
                                lines.Add(t);
                    if (heading is null || lines.Count == 0)
                        continue;
                    var section = new JsonObject { ["title"] = heading, ["lines"] = lines };
                    if (Bool(s["warning"]) == true)
                        section["warning"] = true;
                    sections.Add(section);
                }
            if (sections.Count == 0)
                return null;

            var subject = Text(root["subject"], 12)?.ToLowerInvariant();
            return new ExplainNote(
                title,
                subject is not null && SubjectIds.TryGetValue(subject, out var id) ? id : (short)1,
                Text(root["topic"], 60) ?? title,
                sections,
                Text(root["joso_says"], 200)
            );
        }

        private static string? Text(JsonNode? node, int max)
        {
            if (node is not JsonValue v || !v.TryGetValue<string>(out var s))
                return null;
            s = s.Trim();
            return s.Length == 0 ? null : s.Length > max ? s[..max] : s;
        }

        private static bool? Bool(JsonNode? node) =>
            node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    }
}
