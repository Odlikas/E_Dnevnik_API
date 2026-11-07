using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using E_Dnevnik_API.Models.SpecificSubject;

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

    // dated ocjena prije hashiranja
    public record ParsedGrade(DateOnly GradedOn, short Value, string GradingElement, string? Note);

    public static class GradeRows
    {
        public const string DefaultElement = "ocjena";

        // stupci tablice elemenata vrednovanja idu IX, X, ..., VI (pa VII, VIII ako ih ima)
        private static readonly int[] MonthColumns = { 9, 10, 11, 12, 1, 2, 3, 4, 5, 6, 7, 8 };

        // ocjena 1-5 iz teksta; null za ne-brojčane vrijednosti
        public static short? ParseValue(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var t = text.Trim();
            if (Regex.IsMatch(t, @"^[1-5]$"))
                return short.Parse(t);
            // npr. "5 (odličan)": točno jedna samostalna znamenka 1-5 i nijedna druga znamenka
            var digits = Regex.Matches(t, @"\d+");
            if (digits.Count == 1 && Regex.IsMatch(digits[0].Value, "^[1-5]$")
                && Regex.IsMatch(t, @"(^|[^\d])[1-5]([^\d]|$)"))
                return short.Parse(digits[0].Value);
            return null;
        }

        // sve ocjene iz jedne ćelije tablice elemenata ("4 5", "4,5", "45")
        public static List<short> ParseCellValues(string? cell)
        {
            var list = new List<short>();
            if (string.IsNullOrEmpty(cell))
                return list;
            foreach (var ch in cell)
                if (ch is >= '1' and <= '5')
                    list.Add((short)(ch - '0'));
            return list;
        }

        // dated ocjene predmeta + element vrednovanja izveden iz tablice elemenata
        public static List<ParsedGrade> Parse(SubjectDetails details, SchoolYear year)
        {
            var dated = new List<(DateOnly Date, short Value, string? Note)>();
            foreach (var month in details.MonthlyGrades ?? new())
            {
                foreach (var g in month.Grades)
                {
                    var date = year.ParseDate(g.GradeDate);
                    var value = ParseValue(g.Grade);
                    if (date is null || value is null)
                        continue;
                    var note = string.IsNullOrWhiteSpace(g.GradeNote) ? null : g.GradeNote.Trim();
                    dated.Add((date.Value, value.Value, note));
                }
            }

            // po elementu i mjesecu: koliko kojih vrijednosti još nije dodijeljeno
            var pools = new List<(string Name, Dictionary<int, List<short>> ByMonth)>();
            foreach (var el in details.EvaluationElements ?? new())
            {
                if (string.IsNullOrWhiteSpace(el.Name))
                    continue;
                var byMonth = new Dictionary<int, List<short>>();
                var cells = el.GradesByMonth ?? new();
                for (var i = 0; i < cells.Count && i < MonthColumns.Length; i++)
                    byMonth[MonthColumns[i]] = ParseCellValues(cells[i]);
                pools.Add((CleanElementName(el.Name), byMonth));
            }

            // stabilni redoslijed: po datumu, pa po redu na stranici
            var ordered = dated
                .Select((d, i) => (d, i))
                .OrderBy(x => x.d.Date)
                .ThenBy(x => x.i)
                .Select(x => x.d);

            var result = new List<ParsedGrade>();
            foreach (var (date, value, note) in ordered)
            {
                string? element = null;
                foreach (var pool in pools)
                {
                    if (pool.ByMonth.TryGetValue(date.Month, out var values) && values.Remove(value))
                    {
                        element = pool.Name;
                        break;
                    }
                }
                element ??= note ?? DefaultElement;
                result.Add(new ParsedGrade(date, value, element, note));
            }
            return result;
        }

        private static string CleanElementName(string name) =>
            Regex.Replace(name.Trim(), @"\s+", " ");

        // sha256 hex od child_id|subject_id|date|value|element|note|n
        // n = koliko je istih redaka već bilo prije, pa dvije iste ocjene isti dan ostaju dva retka
        public static List<GradeRow> WithHashes(Guid childId, short subjectId, IEnumerable<ParsedGrade> grades)
        {
            var seen = new Dictionary<string, int>();
            var rows = new List<GradeRow>();
            foreach (var g in grades)
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
                rows.Add(
                    new GradeRow(subjectId, g.GradedOn, g.Value, g.GradingElement, g.Note, Hash(key + "|" + n))
                );
            }
            return rows;
        }

        public static string Hash(string input) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
