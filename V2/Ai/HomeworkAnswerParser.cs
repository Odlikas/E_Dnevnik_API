using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace E_Dnevnik_API.V2.Ai
{
    // model vraća JSON iz HomeworkPrompt; sve se provjeri i skrati prije upisa, ostalo se odbaci
    public static class HomeworkAnswerParser
    {
        public const int MaxLines = 15;
        private const int MaxHints = 3;

        // null = nečitljivo (ili model nije poštovao oblik)
        public static HomeworkResult? Parse(string raw)
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
            if (root is null || Bool(root["readable"]) == false || root["lines"] is not JsonArray rawLines)
                return null;

            var lines = new List<HomeworkLine>();
            var used = new HashSet<int>();
            foreach (var l in rawLines.OfType<JsonObject>())
            {
                if (lines.Count == MaxLines)
                    break;
                var text = WithoutNumber(Text(l["text"], 200));
                if (text is null)
                    continue;
                // broj kao na stranici; bez njega ili ponovljen = sljedeći slobodan
                var number = Int(l["number"]) is { } n && n is > 0 and < 1000 && !used.Contains(n) ? n : Next(used);
                used.Add(number);
                var answer = Text(l["answer"], 80);
                // pogrešan redak bez rješenja ne može se ispraviti; ostaje točan
                if (Bool(l["correct"]) != false || answer is null)
                {
                    lines.Add(new HomeworkLine(number, text, true, null, null, [], null, []));
                    continue;
                }
                var accepted = new List<string>();
                if (l["accepted"] is JsonArray acc)
                    foreach (var x in acc)
                        if (Text(x, 80) is { } a && Norm(a) != Norm(answer) && accepted.Count < 5)
                            accepted.Add(a);
                // savjet koji otkriva rješenje se baca
                var hints = new List<string>();
                if (l["hints"] is JsonArray h)
                    foreach (var x in h)
                        if (Text(x, 300) is { } hint && hints.Count < MaxHints && !Reveals(hint, answer))
                            hints.Add(hint);
                lines.Add(
                    new HomeworkLine(
                        number,
                        text,
                        false,
                        WithoutNumber(Text(l["prompt"], 150)) ?? "",
                        Text(l["answer_hint"], 60) ?? "",
                        hints,
                        answer,
                        accepted
                    )
                );
            }
            if (lines.Count == 0)
                return null;
            return new HomeworkResult(Text(root["subject"], 60) ?? "", Text(root["title"], 120) ?? "", lines);
        }

        // isto pravilo kao app.homework_norm u bazi: bez razmaka, velikih slova, zagrada i točke na kraju
        public static string Norm(string s) =>
            Regex.Replace(
                Regex.Replace(s, @"\s+", "").ToLowerInvariant().Replace("{", "").Replace("}", "").Replace(',', '.'),
                @"[.!]+$",
                ""
            );

        // savjet sadrži rješenje kao zasebnu riječ ili izraz ("ljudi", "{5/6}")
        public static bool Reveals(string hint, string answer)
        {
            var a = answer.Trim().Trim('.', '!').Replace("{", "").Replace("}", "");
            // jedna znamenka ili slovo ("6") se previše često javlja u savjetu da bi značila rješenje
            if (a.Length < 2)
                return false;
            var h = hint.Replace("{", "").Replace("}", "");
            return Regex.IsMatch(h, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(a)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase);
        }

        // "4. čovjek – čovjeci" -> "čovjek – čovjeci"; aplikacija sama piše broj ispred
        public static string? WithoutNumber(string? s)
        {
            if (s is null)
                return null;
            var t = Regex.Replace(s, @"^\s*\d{1,3}\s*[.)]\s+", "");
            return t.Length == 0 ? s : t;
        }

        private static int Next(HashSet<int> used)
        {
            var n = 1;
            while (used.Contains(n))
                n++;
            return n;
        }

        private static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

        private static int? Int(JsonNode? n) =>
            n is JsonValue v
                ? v.TryGetValue<int>(out var i) ? i
                : v.TryGetValue<string>(out var s) && int.TryParse(s.Trim().TrimEnd('.'), out var p) ? p
                : null
            : null;

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
