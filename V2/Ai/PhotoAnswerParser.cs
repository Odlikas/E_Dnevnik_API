using System.Text.Json;
using System.Text.Json.Nodes;

namespace E_Dnevnik_API.V2.Ai
{
    // model vraća JSON iz PhotoPrompt; sve se provjeri i skrati prije upisa, ostalo se odbaci
    public static class PhotoAnswerParser
    {
        private const int MaxSteps = 6;
        private const int MaxFormulas = 4;
        private const int MaxSimilar = 3;
        private static readonly string[] Difficulties = { "easy", "medium", "hard" };

        // null = nečitljivo (ili model nije poštovao oblik)
        public static PhotoResult? Parse(string raw)
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
            if (root is null || Bool(root["readable"]) == false)
                return null;

            var expression = Text(root["expression"], 300);
            if (expression is null)
                return null;

            var steps = new JsonArray();
            if (root["steps"] is JsonArray rawSteps)
                foreach (var s in rawSteps.OfType<JsonObject>().Take(MaxSteps))
                    if (Step(s) is { } step)
                        steps.Add(step);
            if (steps.Count == 0)
                return null;

            var similar = new JsonArray();
            if (root["similar"] is JsonArray rawSimilar)
                foreach (var t in rawSimilar.OfType<JsonObject>())
                    if (Text(t["expression"], 200) is { } e && similar.Count < MaxSimilar)
                    {
                        var d = Text(t["difficulty"], 10)?.ToLowerInvariant();
                        var item = new JsonObject
                        {
                            ["expression"] = e,
                            ["difficulty"] = d is not null && Difficulties.Contains(d) ? d : Difficulties[similar.Count],
                        };
                        // rješenje i tri pogrešna za kviz "Sad ti probaj"; bez rješenja zadatak ostaje samo za čitanje
                        if (Text(t["answer"], 60) is { } answer)
                        {
                            item["answer"] = answer;
                            var wrong = new JsonArray();
                            if (t["choices"] is JsonArray c)
                                foreach (var x in c)
                                    if (Text(x, 60) is { } w && w != answer && wrong.Count < 3 && !wrong.Any(n => n!.GetValue<string>() == w))
                                        wrong.Add(w);
                            item["choices"] = wrong;
                            if (Text(t["why"], 300) is { } why)
                                item["why"] = why;
                        }
                        similar.Add(item);
                    }

            return new PhotoResult(
                expression,
                Text(root["topic"], 60) ?? "",
                Text(root["answer"], 300) ?? expression,
                steps,
                similar
            );
        }

        private static JsonObject? Step(JsonObject s)
        {
            var title = Text(s["title"], 120);
            if (title is null)
                return null;
            var step = new JsonObject { ["title"] = title };
            if (Text(s["text"], 600) is { } text)
                step["text"] = text;
            if (s["formulas"] is JsonArray f)
            {
                var formulas = new JsonArray();
                foreach (var x in f.Take(MaxFormulas))
                    if (Text(x, 200) is { } line)
                        formulas.Add(line);
                if (formulas.Count > 0)
                    step["formulas"] = formulas;
            }
            if (s["why"] is JsonObject w && Text(w["title"], 120) is { } wt && Text(w["text"], 600) is { } wx)
            {
                var why = new JsonObject { ["title"] = wt, ["text"] = wx, ["formula"] = Text(w["formula"], 200) ?? "" };
                if (Text(w["tip"], 300) is { } tip)
                    why["tip"] = tip;
                step["why"] = why;
            }
            return step;
        }

        private static bool? Bool(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

        private static string? Text(JsonNode? n, int max)
        {
            if (n is not JsonValue v || !v.TryGetValue<string>(out var s))
                return null;
            s = s.Trim();
            if (s.Length == 0)
                return null;
            return s.Length <= max ? s : s[..max].TrimEnd() + "…";
        }
    }
}
