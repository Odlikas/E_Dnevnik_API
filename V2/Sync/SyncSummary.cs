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

        // "N/A" i prazno iz scrapera -> null
        public static string? OrNull(string? text) =>
            string.IsNullOrWhiteSpace(text) || text.Trim() == "N/A" ? null : text.Trim();
    }
}
