using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace E_Dnevnik_API.V2.Sync
{
    // jedan sat iz /schedule; Weekday 1 = ponedjeljak ... 7 = nedjelja
    public record ParsedLesson(
        short Weekday,
        short Period,
        string Subject,
        TimeOnly? StartsAt,
        TimeOnly? EndsAt
    );

    // parsira sirovu stranicu https://ocjene.skole.hr/schedule (struktura kao kod ScheduleTableScraperService):
    // po danu div.flex-table-schedule[data-action-id=PON|UTO|...], svaki sat je div.row (ne header),
    // prva ćelija koja nije no-box je predmet; broj sata i vrijeme (ako ih ima) su u no-box ćelijama
    public static class SchedulePageParser
    {
        private static readonly (string Code, short Weekday)[] Days =
        {
            ("PON", 1),
            ("UTO", 2),
            ("SRI", 3),
            ("ČET", 4),
            ("PET", 5),
            ("SUB", 6),
            ("NED", 7),
        };

        private static readonly Regex TimeRange = new(
            @"(\d{1,2})[:.](\d{2})\s*[-–]\s*(\d{1,2})[:.](\d{2})",
            RegexOptions.Compiled
        );

        private static readonly Regex LeadingNumber = new(
            @"^(\d{1,2})\.?(?:\s|$)",
            RegexOptions.Compiled
        );

        // null: stranica nije prepoznata (ne diramo postojeći raspored)
        // prazna lista: e-Dnevnik kaže da raspored nije upisan ("Učenik nema upisan raspored.")
        public static List<ParsedLesson>? Parse(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return null;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var tables = doc.DocumentNode.SelectNodes(
                "//div[contains(@class, 'flex-table-schedule')]"
            );
            if (tables is null)
            {
                var noRecords = doc.DocumentNode.SelectSingleNode(
                    "//div[contains(concat(' ', normalize-space(@class), ' '), ' no-records ')]"
                );
                return noRecords is null ? null : new List<ParsedLesson>();
            }

            var lessons = new List<ParsedLesson>();
            var seen = new HashSet<(short, short)>();
            foreach (var (code, weekday) in Days)
            {
                var period = 0;
                foreach (
                    var table in tables.Where(t =>
                        t.GetAttributeValue("data-action-id", "") == code
                    )
                )
                {
                    var rows = table.SelectNodes(
                        ".//div[contains(concat(' ', normalize-space(@class), ' '), ' row ') "
                            + "and not(contains(@class, 'header'))]"
                    );
                    if (rows is null)
                        continue;
                    foreach (var row in rows)
                    {
                        period++;
                        var cells = row.SelectNodes(
                            ".//div[contains(concat(' ', normalize-space(@class), ' '), ' cell ')]"
                        );
                        if (cells is null)
                            continue;
                        var info = cells
                            .Where(c => IsNoBox(c))
                            .Select(c => Text(c.InnerText))
                            .ToList();
                        var subjectCell = cells.FirstOrDefault(c => !IsNoBox(c));

                        // broj sata iz no-box ćelije ("3." ili "3"), inače redni broj retka
                        var number = period;
                        foreach (var t in info)
                        {
                            var m = LeadingNumber.Match(t);
                            if (m.Success)
                            {
                                number = int.Parse(m.Groups[1].Value);
                                period = number;
                                break;
                            }
                        }

                        var subject = subjectCell is null ? "" : SubjectName(subjectCell);
                        if (subject.Length == 0)
                            continue;
                        if (!seen.Add((weekday, (short)number)))
                            continue;

                        var (start, end) = Times(
                            string.Join(" ", info.Append(Text(row.InnerText)))
                        );
                        lessons.Add(new ParsedLesson(weekday, (short)number, subject, start, end));
                    }
                }
            }
            return lessons;
        }

        private static bool IsNoBox(HtmlNode cell) =>
            cell.GetAttributeValue("class", "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("no-box");

        // prvi neprazni tekst u ćeliji; "Kemija, Fizika" (grupe) -> prvi predmet
        private static string SubjectName(HtmlNode cell)
        {
            var first = cell.DescendantsAndSelf()
                .Where(n => n.NodeType == HtmlNodeType.Text)
                .Select(n => Text(n.InnerText))
                .FirstOrDefault(t => t.Length > 0);
            if (first is null)
                return "";
            var name = first.Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            return name;
        }

        private static (TimeOnly?, TimeOnly?) Times(string text)
        {
            var m = TimeRange.Match(text);
            if (!m.Success)
                return (null, null);
            return (
                Time(m.Groups[1].Value, m.Groups[2].Value),
                Time(m.Groups[3].Value, m.Groups[4].Value)
            );
        }

        private static TimeOnly? Time(string h, string m)
        {
            var hour = int.Parse(h);
            var minute = int.Parse(m);
            return hour < 24 && minute < 60 ? new TimeOnly(hour, minute) : null;
        }

        private static string Text(string raw) =>
            Regex.Replace(WebUtility.HtmlDecode(raw), @"\s+", " ").Trim();
    }
}
