using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace E_Dnevnik_API.V2.Sync
{
    // čita stranicu https://ocjene.skole.hr/grade/{id} za /v2/sync
    // svaka ocjena ima data-action-id i na datiranom retku i na spanu u ćeliji mjeseca
    // u tablici elemenata, pa element vrednovanja dobivamo spajanjem po tom id-u
    public static class GradePageParser
    {
        public static List<ParsedGrade> Parse(string? html, SchoolYear year)
        {
            var result = new List<ParsedGrade>();
            if (string.IsNullOrWhiteSpace(html))
                return result;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var elements = ElementsByActionId(doc);

            var rows = doc.DocumentNode.SelectNodes("//div[@data-action-id][div[@data-date]]");
            if (rows == null)
                return result;

            var seen = new HashSet<string>();
            foreach (var row in rows)
            {
                if (!row.HasClass("row"))
                    continue;
                var actionId = row.GetAttributeValue("data-action-id", "").Trim();
                if (actionId.Length == 0 || !seen.Add(actionId))
                    continue;

                var dateCell = row.SelectSingleNode("div[@data-date]");
                var date =
                    year.ParseDate(dateCell.GetAttributeValue("data-date", "")) ?? year.ParseDate(Text(dateCell));
                if (date is null)
                    continue;

                var cells = row.SelectNodes("div[contains(concat(' ', normalize-space(@class), ' '), ' box ')]/div")
                    ?.Where(c => c.HasClass("cell"))
                    .ToList();
                if (cells == null || cells.Count == 0)
                    continue;

                var value = GradeRows.ParseValue(Text(cells[0]));
                if (value is null)
                    continue;

                var note = cells.Count > 1 ? Text(cells[1]) : "";
                result.Add(
                    new ParsedGrade(
                        date.Value,
                        value.Value,
                        elements.TryGetValue(actionId, out var el) ? el : GradeRows.DefaultElement,
                        note.Length == 0 ? null : note,
                        actionId
                    )
                );
            }
            return result;
        }

        // actionId -> ime elementa vrednovanja (redak tablice s "cell first")
        private static Dictionary<string, string> ElementsByActionId(HtmlDocument doc)
        {
            var map = new Dictionary<string, string>();
            var firstCells = doc.DocumentNode.SelectNodes(
                "//div[contains(concat(' ', normalize-space(@class), ' '), ' first ')]"
            );
            if (firstCells == null)
                return map;

            foreach (var first in firstCells)
            {
                var row = first.ParentNode;
                if (
                    !first.HasClass("cell")
                    || row == null
                    || !row.HasClass("row")
                    || row.HasClass("header")
                    || row.HasClass("final-grade")
                )
                    continue;
                var name = Collapse(Text(first.SelectSingleNode(".//span") ?? first));
                if (name.Length == 0)
                    continue;
                var ids = row.SelectNodes(".//*[@data-action-id]");
                if (ids == null)
                    continue;
                foreach (var node in ids)
                {
                    var id = node.GetAttributeValue("data-action-id", "").Trim();
                    if (id.Length > 0)
                        map.TryAdd(id, name);
                }
            }
            return map;
        }

        // InnerText bez HTML entiteta, obrezan
        private static string Text(HtmlNode node) => WebUtility.HtmlDecode(node.InnerText).Trim();

        private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    }
}
