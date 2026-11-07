using System.Text;
using System.Text.RegularExpressions;

namespace E_Dnevnik_API.V2.Sync
{
    public record SubjectRow(short Id, string Code, string NameHr);

    // spaja ime predmeta iz e-Dnevnika s public.subjects po normaliziranom name_hr + aliasi
    public class SubjectMapper
    {
        // uobičajene varijante imena iz e-Dnevnika -> subjects.code
        private static readonly Dictionary<string, string> AliasToCode = new()
        {
            ["hrvatski"] = "HRV",
            ["hrvatski jezik i knjizevnost"] = "HRV",
            ["engleski"] = "ENG",
            ["engleski jezik prvi strani jezik"] = "ENG",
            ["engleski jezik drugi strani jezik"] = "ENG",
            ["engleski jezik izborni"] = "ENG",
            ["njemacki"] = "NJE",
            ["njemacki jezik prvi strani jezik"] = "NJE",
            ["njemacki jezik drugi strani jezik"] = "NJE",
            ["njemacki jezik izborni"] = "NJE",
            ["informatika izborni"] = "INF",
            ["informatika izborna"] = "INF",
        };

        private readonly Dictionary<string, SubjectRow> _byName = new();
        private readonly Dictionary<string, SubjectRow> _byCode = new(StringComparer.OrdinalIgnoreCase);

        public SubjectMapper(IEnumerable<SubjectRow> subjects)
        {
            foreach (var s in subjects)
            {
                _byName[Normalize(s.NameHr)] = s;
                _byCode[s.Code] = s;
            }
        }

        public SubjectRow? Map(string? ednevnikName)
        {
            if (string.IsNullOrWhiteSpace(ednevnikName))
                return null;

            foreach (var candidate in Candidates(ednevnikName))
            {
                if (_byName.TryGetValue(candidate, out var hit))
                    return hit;
                if (AliasToCode.TryGetValue(candidate, out var code) && _byCode.TryGetValue(code, out hit))
                    return hit;
            }
            return null;
        }

        // cijelo ime, pa bez zagrada, pa bez nastavka iza " - " ili ","
        private static IEnumerable<string> Candidates(string name)
        {
            yield return Normalize(name);
            var noParens = Regex.Replace(name, @"\([^)]*\)", " ");
            yield return Normalize(noParens);
            var head = Regex.Split(noParens, @"\s[-–]\s|,")[0];
            yield return Normalize(head);
        }

        // mala slova, bez dijakritika i interpunkcije, jedan razmak
        public static string Normalize(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text.Trim().ToLowerInvariant())
            {
                sb.Append(
                    ch switch
                    {
                        'č' or 'ć' => 'c',
                        'š' => 's',
                        'ž' => 'z',
                        'đ' => 'd',
                        _ when char.IsLetterOrDigit(ch) => ch,
                        _ => ' ',
                    }
                );
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }
    }
}
