using E_Dnevnik_API.Models.ScrapeSubjects;

namespace E_Dnevnik_API.V2.Sync
{
    // što smo zadnji put vidjeli za jedan predmet (child_subjects.ednevnik_average / grades_fetched_at)
    public record SubjectFetchState(string? Average, DateTimeOffset? FetchedAt);

    // što ScrapeAsync skida: popis predmeta, profil i ispite uvijek; stranice ocjena i raspored po potrebi
    public record SyncFetchOptions(Func<IReadOnlyList<SubjectInfo>, ISet<string>> PickGradePages, bool FetchSchedule)
    {
        // sve stranice, kao prije inkrementalnog synca
        public static SyncFetchOptions All { get; } =
            new(list => list.Select(s => s.SubjectId).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(), true);
    }

    // inkrementalni sync: manje zahtjeva prema CARNET-u, a ništa se ne propušta dulje od par dana
    // /grade/new se NE koristi - otvaranje te stranice učeniku označi ocjene kao viđene
    public static class SyncFetchPlan
    {
        // toliko "najstarijih" predmeta osvježimo i kad im se prosjek nije promijenio
        // (nova ocjena jednaka prosjeku, npr. 5 na 5,00, ne mijenja prosjek)
        public const int RotationPerSync = 2;

        // predmet čija stranica nije skinuta ovoliko dugo uvijek se skida
        public static readonly TimeSpan MaxGradeAge = TimeSpan.FromDays(3);

        // raspored se rijetko mijenja
        public static readonly TimeSpan ScheduleMaxAge = TimeSpan.FromDays(7);

        // e-Dnevnik id-ovi predmeta čije stranice ocjena treba skinuti
        public static HashSet<string> PickGradePages(
            IEnumerable<SubjectInfo> subjects,
            IReadOnlyDictionary<string, SubjectFetchState> state,
            DateTimeOffset now,
            bool full
        )
        {
            var candidates = subjects
                .Where(s => !string.IsNullOrEmpty(s.SubjectId) && !SubjectMapper.IsHomeroom(s.SubjectName))
                .GroupBy(s => s.SubjectId)
                .Select(g => g.First())
                .ToList();
            if (full)
                return candidates.Select(s => s.SubjectId).ToHashSet();

            var pick = new HashSet<string>();
            var rest = new List<(string Id, DateTimeOffset FetchedAt)>();
            foreach (var s in candidates)
            {
                if (
                    !state.TryGetValue(s.SubjectId, out var st)
                    || st.FetchedAt is not { } fetchedAt
                    || !string.Equals(Normalize(st.Average), Normalize(s.Grade), StringComparison.Ordinal)
                    || now - fetchedAt >= MaxGradeAge
                )
                    pick.Add(s.SubjectId);
                else
                    rest.Add((s.SubjectId, fetchedAt));
            }

            foreach (var (id, _) in rest.OrderBy(r => r.FetchedAt).ThenBy(r => r.Id, StringComparer.Ordinal).Take(RotationPerSync))
                pick.Add(id);
            return pick;
        }

        public static bool ShouldFetchSchedule(DateTimeOffset? lastFetched, DateTimeOffset now, bool full) =>
            full || lastFetched is null || now - lastFetched.Value >= ScheduleMaxAge;

        private static string Normalize(string? average) => (average ?? "").Trim();
    }
}
