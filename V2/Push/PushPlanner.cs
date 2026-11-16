using System.Text.RegularExpressions;

namespace E_Dnevnik_API.V2.Push
{
    // tko dobiva koji red i s kojim tekstom; bez baze i bez FCM-a, da se lako testira
    public static class PushPlanner
    {
        public const string NewGrade = "new_grade";

        // dijete traži pomoć od roditelja (help_requests, trigger help_requests_notify); samo roditeljima
        public const string HelpRequest = "help_request";

        // Odlikaš isključen na djetetovom mobitelu (devices.blocking_ok -> false, trigger
        // devices_blocking_off_notify, D265 u aplikaciji); jedan red po roditelju, samo roditeljima
        public const string Tamper = "tamper";

        // roditelj je djetetu dodao vrijeme ili odobrio zahtjev (unlock_grants, trigger
        // unlock_grants_time_added_notify, D269 u aplikaciji); minute su u payload 'value'; samo djetetu
        public const string TimeAdded = "time_added";

        // dijete traži još vremena (time_requests insert, trigger time_requests_notify, D291 u
        // aplikaciji); payload 'request_id' + minute u 'value'; samo roditeljima
        public const string TimeRequest = "time_request";

        // roditelj je odbio zahtjev za vrijeme (time_requests pending -> denied, isti trigger, D291);
        // samo djetetu. Odobrenje stiže kao time_added.
        public const string TimeDeclined = "time_declined";

        // djetetov mobitel se 24 h nije javio (device_live.reported_at; pg_cron notify-silent-phones,
        // D313 u aplikaciji); jedan red po roditelju s tamper_alerts, samo roditeljima
        public const string PhoneSilent = "phone_silent";

        // roditelj predlaže cilj za predmet (subject_goals pending, trigger
        // subject_goals_notify, A8 u aplikaciji); payload 'subject_id'; samo djetetu
        public const string GoalProposal = "goal_proposal";

        // dijete je prihvatilo ili odbilo prijedlog cilja (isti trigger, A8); samo roditeljima.
        // Aplikacija otvara Ocjene tog djeteta, pa treba 'child_id'
        public const string GoalAnswer = "goal_answer";

        // dijete moli slobodan dan (free_day_requests insert, trigger free_day_requests_notify, D338);
        // payload 'request_id'; samo roditeljima
        public const string FreeDayRequest = "free_day_request";

        // roditelj je dao slobodan dan, sam ili odobrenjem molbe (child_free_days insert, trigger
        // child_free_days_notify, D338); samo djetetu
        public const string FreeDayGiven = "free_day_given";

        // roditelj je odbio molbu (free_day_requests pending -> denied, D338); samo djetetu, bez ishoda
        public const string FreeDayDeclined = "free_day_declined";

        // novi izostanak iz e-Dnevnika koji čeka opravdanje (absences insert, trigger absences_notify,
        // D1); samo roditeljima
        public const string Absence = "absence";

        // dijete na iPhoneu moli roditelja da uključi Obiteljsko dijeljenje (send_family_sharing_guide,
        // D392, Q351); jedan red po roditelju, samo roditeljima
        public const string FamilySharingGuide = "family_sharing_guide";

        // srednjoškolac je prekinuo vezu s roditeljem (unlink_parent, D339); jedan red po roditelju
        public const string HsUnlinked = "hs_unlinked";

        // roditelj je prihvatio / odbio poziv srednjoškolca (C5) i potvrdio / odbio privolu (C4);
        // samo srednjoškolčevom mobitelu. Q233: srednjoškolci još nemaju push token, pa danas
        // nikome ne idu (red se ipak označi poslanim)
        public const string HsParentLinked = "hs_parent_linked";
        public const string HsInviteDeclined = "hs_invite_declined";
        public const string HsConsentConfirmed = "hs_consent_confirmed";
        public const string HsConsentDeclined = "hs_consent_declined";

        // tipovi za koje znamo napisati tekst; ostali redovi čekaju (ne dira ih se)
        public static readonly IReadOnlyCollection<string> KnownTypes = new[]
        {
            NewGrade, HelpRequest, Tamper, TimeAdded, TimeRequest, TimeDeclined, PhoneSilent,
            GoalProposal, GoalAnswer, FreeDayRequest, FreeDayGiven, FreeDayDeclined, Absence,
            FamilySharingGuide, HsUnlinked, HsParentLinked, HsInviteDeclined, HsConsentConfirmed,
            HsConsentDeclined,
        };

        // tipovi koje dobiva samo dijete (roditelji ih ne dobivaju)
        private static readonly HashSet<string> KidOnlyTypes = new()
        {
            TimeAdded, TimeDeclined, GoalProposal, FreeDayGiven, FreeDayDeclined,
            HsParentLinked, HsInviteDeclined, HsConsentConfirmed, HsConsentDeclined,
        };

        // tipovi koje dobiva i dijete na svom mobitelu (uz roditelje)
        private static readonly HashSet<string> KidTypes = new(KidOnlyTypes) { NewGrade };

        // B1 (Karlo 14. 11.): roditelj dobiva push samo za niske ocjene; dobre su u tjednom izvještaju
        public static bool IsLowGrade(short? value) => value is 1 or 2;

        private const string OpenApp = "Otvori Odlikaš i pogledaj.";

        // details=false: D213, tekst bez ocjene, predmeta i imena (prolazi kroz Google);
        // details=true: "Nova ocjena: Matematika 5" + bilješka (V2_PUSH_DETAILS=true)
        public static List<OutgoingPush> Plan(
            IReadOnlyList<PendingPush> rows,
            IReadOnlyList<PushRecipient> recipients,
            bool details
        )
        {
            // (token, tip) -> (primatelj, redovi) redom kojim su stigli: ocjene se spajaju, tipovi ne
            var perToken = new Dictionary<string, (PushRecipient Who, List<PendingPush> Rows)>();
            var order = new List<string>();
            foreach (var row in rows)
            {
                if (!KnownTypes.Contains(row.Type))
                    continue;
                foreach (var r in RecipientsOf(row, recipients))
                {
                    // D314: s detaljima se ocjene dvoje djece ne spajaju (naslov nosi ime jednog)
                    var key = r.Token + "|" + row.Type + (details ? "|" + row.ChildId : "");
                    if (!perToken.TryGetValue(key, out var slot))
                    {
                        slot = (r, new List<PendingPush>());
                        perToken[key] = slot;
                        order.Add(key);
                    }
                    if (!slot.Rows.Contains(row))
                        slot.Rows.Add(row);
                }
            }

            var result = new List<OutgoingPush>();
            foreach (var key in order)
            {
                var (who, list) = perToken[key];
                var token = who.Token;
                var (title, body) = Text(list, who.Kind, details);
                var data = new Dictionary<string, string>
                {
                    ["type"] = list[0].Type,
                    ["notification_id"] = list[0].Id.ToString(),
                    ["count"] = list.Count.ToString(),
                };
                // samo kad su svi redovi istog djeteta (bez detalja se spajaju redovi dvoje djece)
                if (list[0].ChildId is Guid child && list.All(r => r.ChildId == child))
                    data["child_id"] = child.ToString();
                // jedan zahtjev: aplikacija ga otvara izravno; više njih: otvara Obavijesti
                if (list.Count == 1 && list[0].RequestId is Guid request)
                    data["request_id"] = request.ToString();
                // isto za predmet i ocjenu: jedna ocjena otvara svoj predmet, više njih samo Odlikaš
                if (list.Count == 1 && list[0].SubjectId is short subject)
                    data["subject_id"] = subject.ToString();
                if (list.Count == 1 && list[0].GradeId is Guid grade)
                    data["grade_id"] = grade.ToString();
                result.Add(new OutgoingPush(token, who.Kind, title, body, data, list.Select(r => r.Id).ToList()));
            }
            return result;
        }

        // roditelji obitelji (svi kad je parent_id null) i djetetov trenutni mobitel
        public static IEnumerable<PushRecipient> RecipientsOf(PendingPush row, IReadOnlyList<PushRecipient> all)
        {
            foreach (var r in all)
            {
                if (r.Kind == RecipientKind.Parent)
                {
                    if (KidOnlyTypes.Contains(row.Type))
                        continue;
                    if (row.Type == NewGrade && !IsLowGrade(row.Value))
                        continue;
                    if (r.FamilyId == row.FamilyId && (row.ParentId == null || row.ParentId == r.Id))
                        yield return r;
                }
                else if (row.ParentId == null && row.ChildId != null && r.ChildId == row.ChildId
                         && KidTypes.Contains(row.Type))
                {
                    yield return r;
                }
            }
        }

        public static (string Title, string Body) Text(IReadOnlyList<PendingPush> rows, RecipientKind kind, bool details)
        {
            if (rows[0].Type == HelpRequest)
            {
                // D213: bez imena dok details nije uključen
                var child = details ? rows[0].ChildFirstName?.Trim() : null;
                return (string.IsNullOrEmpty(child) ? "Dijete treba pomoć" : child + " treba pomoć", OpenApp);
            }
            if (rows[0].Type == TimeAdded)
            {
                // D3: rodno neutralno; bez imena (ide samo djetetu)
                var minutes = rows.Sum(r => r.Value ?? 0);
                return (minutes > 0 ? $"Dodano ti je {minutes} min" : "Dodano ti je vrijeme",
                    "Imaš više slobodnog vremena danas. Otvori Odlikaš.");
            }
            if (rows[0].Type == TimeRequest)
            {
                // D213: bez imena dok details nije uključen
                var child = details ? rows[0].ChildFirstName?.Trim() : null;
                if (string.IsNullOrEmpty(child))
                    return ("Dijete traži još vremena", "Otvori Odlikaš.");
                var minutes = rows.Count == 1 ? rows[0].Value ?? 0 : 0;
                return (minutes > 0 ? $"{child} traži {minutes} min" : child + " traži još vremena", "Otvori Odlikaš.");
            }
            if (rows[0].Type == TimeDeclined || rows[0].Type == FreeDayDeclined)
            {
                // bez ishoda u tekstu (ide samo djetetu, preko Googlea); aplikacija pokaže odgovor
                return ("Roditelj je odgovorio", OpenApp);
            }
            if (rows[0].Type == Tamper)
            {
                // F10: bez imena dok details nije uključen
                var child = details ? rows[0].ChildFirstName?.Trim() : null;
                return (string.IsNullOrEmpty(child) ? "Odlikaš je isključen" : "Odlikaš je isključen na mobitelu (" + child + ")",
                    "Dopuštenje na djetetovom mobitelu je isključeno. Otvorite Odlikaš.");
            }
            if (rows[0].Type == PhoneSilent)
            {
                // F10: bez imena dok details nije uključen
                var child = details ? rows[0].ChildFirstName?.Trim() : null;
                return (string.IsNullOrEmpty(child) ? "Mobitel se ne javlja" : "Mobitel (" + child + ") se ne javlja",
                    "Djetetov mobitel nije bio na vezi više od 24 sata. Otvorite Odlikaš.");
            }
            if (rows[0].Type == GoalProposal)
                return ("Novi prijedlog cilja", "Roditelj ti je predložio cilj. " + OpenApp);
            if (rows[0].Type == GoalAnswer)
            {
                // bez ishoda i predmeta; ime samo s detaljima
                var child = details ? rows[0].ChildFirstName?.Trim() : null;
                return (string.IsNullOrEmpty(child) ? "Odgovor na prijedlog cilja" : child + ": odgovor na prijedlog cilja",
                    "Dijete je odgovorilo na vaš prijedlog. Otvorite Odlikaš.");
            }
            if (rows[0].Type == FreeDayRequest)
            {
                var child = details ? rows[0].ChildFirstName?.Trim() : null;
                return (string.IsNullOrEmpty(child) ? "Dijete traži slobodan dan" : child + " traži slobodan dan",
                    "Otvorite Odlikaš i odgovorite.");
            }
            if (rows[0].Type == FreeDayGiven)
                return ("Roditelj ti je dao slobodan dan", "Otvori Odlikaš i pogledaj kada.");
            if (rows[0].Type == Absence)
            {
                // D1 (Figma 1086:5967): bez imena, predmeta i razloga
                return (AbsenceTitle(rows.Count), "Dodirnite za datum i opravdanje u e-Dnevniku.");
            }
            if (rows[0].Type == FamilySharingGuide)
            {
                var child = details ? rows[0].ChildFirstName?.Trim() : null;
                return (string.IsNullOrEmpty(child) ? "Postavite Obiteljsko dijeljenje" : child + ": postavite Obiteljsko dijeljenje",
                    "Potrebno je da Odlikaš radi na djetetovom iPhoneu. Otvorite Odlikaš za upute.");
            }
            if (rows[0].Type == HsUnlinked)
            {
                // D339: roditelj više ne vidi ništa, pa ni ime
                return ("Dijeljenje je prekinuto", "Podaci iz srednje škole više se ne dijele s vama.");
            }
            if (rows[0].Type == HsParentLinked)
                return ("Roditelj je prihvatio poziv", OpenApp);
            if (rows[0].Type == HsConsentConfirmed)
                return ("Roditelj je potvrdio privolu", OpenApp);
            if (rows[0].Type == HsInviteDeclined || rows[0].Type == HsConsentDeclined)
            {
                // bez ishoda u tekstu, kao time_declined; aplikacija pokaže odgovor
                return ("Roditelj je odgovorio", OpenApp);
            }
            var count = rows.Count;
            if (!details)
                return (count == 1 ? "Nova ocjena" : CountTitle(count), OpenApp);

            var name = kind == RecipientKind.Parent ? rows[0].ChildFirstName?.Trim() : null;
            var prefix = string.IsNullOrEmpty(name) ? "" : name + ": ";
            if (count == 1)
            {
                var g = rows[0];
                var what = Join(g.SubjectName?.Trim(), g.Value?.ToString());
                var title = prefix == ""
                    ? (what == "" ? "Nova ocjena" : "Nova ocjena: " + what)
                    : prefix + (what == "" ? "nova ocjena" : "nova ocjena – " + what);
                return (title, BodyOf(g));
            }
            var list = string.Join(", ", rows.Select(r => Join(r.SubjectName?.Trim(), r.Value?.ToString())).Where(s => s != ""));
            return (prefix + CountTitle(count), list == "" ? OpenApp : list);
        }

        // bilješka nastavnika, inače element ocjenjivanja bez šifre škole ("0012 Usmeno" -> "Usmeno")
        public static string BodyOf(PendingPush g)
        {
            var note = Clean(g.Note);
            if (note != "")
                return note;
            var element = Regex.Replace(Clean(g.GradingElement), @"^\d+\s+", "");
            return element;
        }

        // 1 nova ocjena, 2-4 nove ocjene, 5+ novih ocjena (11-14 novih)
        public static string CountTitle(int n)
        {
            var mod10 = n % 10;
            var mod100 = n % 100;
            var words = mod10 == 1 && mod100 != 11 ? "nova ocjena"
                : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "nove ocjene"
                : "novih ocjena";
            return $"{n} {words}";
        }

        // 1 izostanak čeka, 2-4 izostanka čekaju, 5+ izostanaka čeka (11-14 izostanaka)
        public static string AbsenceTitle(int n)
        {
            if (n == 1)
                return "Izostanak čeka opravdanje";
            var mod10 = n % 10;
            var mod100 = n % 100;
            return mod10 == 1 && mod100 != 11 ? $"{n} izostanak čeka opravdanje"
                : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? $"{n} izostanka čekaju opravdanje"
                : $"{n} izostanaka čeka opravdanje";
        }

        private static string Join(string? a, string? b) =>
            string.Join(" ", new[] { a, b }.Where(s => !string.IsNullOrEmpty(s)));

        private static string Clean(string? s) => s == null ? "" : Regex.Replace(s, @"\s+", " ").Trim();
    }
}
