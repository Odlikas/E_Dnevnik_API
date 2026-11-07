using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace E_Dnevnik_API.V2.Sync
{
    // jedan redak za public.grades
    public record GradeRow(
        short SubjectId,
        DateOnly GradedOn,
        short Value,
        string GradingElement,
        string? Note,
        string SourceHash
    );

    // dated ocjena prije hashiranja; ActionId je data-action-id s e-Dnevnika
    public record ParsedGrade(
        DateOnly GradedOn,
        short Value,
        string GradingElement,
        string? Note,
        string? ActionId = null
    );

    public static class GradeRows
    {
        public const string DefaultElement = "ocjena";

        // ocjena 1-5 iz teksta; null za sve ostalo
        public static short? ParseValue(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var t = text.Trim();
            return Regex.IsMatch(t, "^[1-5]$") ? short.Parse(t) : null;
        }

        // s action id-em: sha256 hex od child_id|action:<id>|value
        //   (stabilno kroz syncove i kad nastavnik upiše ocjenu unatrag)
        // bez njega: child_id|subject_id|date|value|element|note|n, n = koliko je istih redaka već bilo
        public static List<GradeRow> WithHashes(Guid childId, short subjectId, IEnumerable<ParsedGrade> grades)
        {
            var seen = new Dictionary<string, int>();
            var rows = new List<GradeRow>();
            foreach (var g in grades)
            {
                string hash;
                if (!string.IsNullOrEmpty(g.ActionId))
                {
                    hash = Hash($"{childId:D}|action:{g.ActionId}|{g.Value}");
                }
                else
                {
                    var key = string.Join(
                        '|',
                        childId.ToString("D"),
                        subjectId.ToString(),
                        g.GradedOn.ToString("yyyy-MM-dd"),
                        g.Value.ToString(),
                        g.GradingElement,
                        g.Note ?? ""
                    );
                    seen.TryGetValue(key, out var n);
                    seen[key] = n + 1;
                    hash = Hash(key + "|" + n);
                }
                rows.Add(new GradeRow(subjectId, g.GradedOn, g.Value, g.GradingElement, g.Note, hash));
            }
            return rows;
        }

        public static string Hash(string input) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
