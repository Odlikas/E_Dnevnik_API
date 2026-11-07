namespace E_Dnevnik_API.V2.Sync
{
    public record ParsedTest(string SubjectName, short? SubjectId, string Title, DateOnly Date);

    public static class SyncSummary
    {
        public const double WeakThreshold = 3.5;

        // predmet s najnižim prosjekom ocjena, samo ako je ispod 3.5
        public static (string? Subject, double? Average) Weakest(
            IEnumerable<(string SubjectName, IReadOnlyCollection<short> Values)> subjects
        )
        {
            string? name = null;
            double? best = null;
            foreach (var (subject, values) in subjects)
            {
                if (values.Count == 0)
                    continue;
                var avg = values.Average(v => (double)v);
                if (best is null || avg < best)
                {
                    best = avg;
                    name = subject;
                }
            }
            if (best is null || best >= WeakThreshold)
                return (null, null);
            return (name, Math.Round(best.Value, 2));
        }

        // prvi ispit od danas nadalje
        public static ParsedTest? NextTest(IEnumerable<ParsedTest> tests, DateOnly today) =>
            tests.Where(t => t.Date >= today).OrderBy(t => t.Date).FirstOrDefault();

        // profil skida točku iz razreda ("4b", "4E4"): vraćamo je iza broja razreda ("4.b", "4.E4")
        public static string? FormatClass(string? text)
        {
            var t = OrNull(text)?.Trim();
            if (t is null)
                return null;
            var m = System.Text.RegularExpressions.Regex.Match(t, @"^(\d)\s*\.?\s*(\S.*)$");
            return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : t;
        }

        // "N/A" i prazno iz scrapera -> null
        public static string? OrNull(string? text) =>
            string.IsNullOrWhiteSpace(text) || text.Trim() == "N/A" ? null : text.Trim();
    }
}
