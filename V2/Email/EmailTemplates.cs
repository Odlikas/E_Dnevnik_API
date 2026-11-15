using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace E_Dnevnik_API.V2.Email
{
    // tekst e-maila za svaki kind iz email_outbox; samo polja iz payloada, bez baze, da se lako testira.
    // Tekstovi su isti kao pregled u aplikaciji (Pogledaj što roditelj dobiva, strings.dart hsEmail*).
    public static class EmailTemplates
    {
        // privola roditelja za srednjoškolca mlađeg od 16 (+ neobavezni poziv u aplikaciju), C4+C5, D422;
        // red stavlja app.queue_consent_email (migracija 20261115000503_parent_consent)
        public const string HsConsent = "hs_consent";

        // kindovi za koje znamo napisati e-mail; ostali redovi čekaju (ne dira ih se)
        public static readonly IReadOnlyCollection<string> KnownKinds = new[] { HsConsent };

        public const string InviteLinkBase = "https://odlikas.hr/poziv/";

        // children.prefs.hs_shared -> public.shared_area (app.areas_from_prefs), redom kao u aplikaciji;
        // 'matura' je u aplikaciji "napredak" (SharedArea.progress)
        private static readonly (string Area, string Label)[] SharedLabels =
        {
            ("grades", "ocjene i prosjeke"),
            ("matura", "napredak"),
            ("streak", "niz"),
            ("exams", "ispite i kalendar"),
        };

        private const string Orange = "#EC9120";
        private const string TextColor = "#353535";
        private const string Muted = "#717171";
        private const string Surface = "#F4F4F4";

        // null = payload nema što treba (red se označi greškom i više ne šalje)
        public static RenderedEmail? Render(string kind, JsonObject payload) =>
            kind switch
            {
                HsConsent => HsConsentEmail(payload),
                _ => null,
            };

        // link istekao (payload 'expires_at' u prošlosti): e-mail više nema smisla slati
        public static bool IsExpired(JsonObject payload, DateTimeOffset now) =>
            Str(payload, "expires_at") is string s
            && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            && at < now;

        private static RenderedEmail? HsConsentEmail(JsonObject p)
        {
            var consentUrl = Str(p, "consent_url");
            if (consentUrl == null || !IsHttps(consentUrl))
                return null;

            var first = Str(p, "first_name");
            var hasName = first != null;
            // D3: rodno neutralno; bez imena "Vaše dijete" / "dijete"
            var subject = (hasName ? first : "Vaše dijete") + " treba vašu potvrdu za Odlikaš";
            var who = hasName ? first! : "dijete";
            var whoCap = hasName ? first! : "Dijete";

            // razred i škola samo u tijelu, nikad u naslovu
            var where = string.Join(" · ", new[] { Str(p, "class_label"), Str(p, "school") }.Where(s => s != null));
            var intro = (hasName ? first : "Vaše dijete") + (where == "" ? "" : " (" + where + ")")
                + " želi koristiti Odlikaš, aplikaciju za učenje. Budući da " + who
                + " ima manje od 16 godina, trebamo vašu potvrdu.";

            const string consentTitle = "1 · Potvrda (potrebna)";
            var consentBody = "Potvrđujete da " + who + " smije koristiti Odlikaš. Time ne dobivate pristup ocjenama.";

            // poziv samo kad ga ima (učenik je uključio "Pozovi roditelja i u aplikaciju") i kad nešto dijeli
            string? inviteBody = null, inviteUrl = null, code = null;
            if (p["invite"] is JsonObject invite)
            {
                var shared = SharedOf(p);
                var token = Str(invite, "token");
                code = Str(invite, "code");
                if (shared.Count > 0 && (token != null || code != null))
                {
                    inviteBody = whoCap + " s vama želi dijeliti: " + AndList(shared)
                        + ". Ne vidite razgovore s Pitaj AI, bilješke ni detalje fokusa.";
                    if (token != null && token.All(char.IsLetterOrDigit))
                        inviteUrl = InviteLinkBase + token;
                }
            }
            const string inviteTitle = "2 · Pratite napredak (neobavezno)";
            var codeLine = code == null ? null : "Ako aplikacija ne prepozna poziv, upišite kod " + code + ".";

            const string footer = "Poveznice vrijede 7 dana. Ovaj e-mail ste dobili jer je vaša adresa upisana u Odlikaš. "
                + "Ako to niste vi, zanemarite ga.";

            // --- običan tekst ---
            var text = new StringBuilder();
            text.Append("Dobar dan,\n\n").Append(intro).Append("\n\n");
            text.Append(consentTitle).Append('\n').Append(consentBody).Append('\n');
            text.Append("Odgovorite ovdje (POTVRĐUJEM ili Ne potvrđujem): ").Append(consentUrl).Append("\n\n");
            if (inviteBody != null)
            {
                text.Append(inviteTitle).Append('\n').Append(inviteBody).Append('\n');
                if (inviteUrl != null)
                    text.Append("Prihvatite poziv: ").Append(inviteUrl).Append('\n');
                if (codeLine != null)
                    text.Append(codeLine).Append('\n');
                text.Append('\n');
            }
            text.Append(footer).Append("\n\nOdlikaš\n");

            // --- HTML ---
            var html = new StringBuilder();
            html.Append("<!doctype html><html lang=\"hr\"><head><meta charset=\"utf-8\">")
                .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
                .Append("<title>").Append(H(subject)).Append("</title></head>")
                .Append("<body style=\"margin:0;padding:0;background:#FFFFFF;\">")
                .Append("<div style=\"max-width:560px;margin:0 auto;padding:24px 16px;font-family:Arial,Helvetica,sans-serif;")
                .Append("font-size:16px;line-height:1.5;color:").Append(TextColor).Append(";\">")
                .Append("<p style=\"margin:0 0 16px;font-size:20px;font-weight:bold;\">Odlikaš</p>")
                .Append("<p style=\"margin:0 0 8px;\">Dobar dan,</p>")
                .Append("<p style=\"margin:0 0 20px;\">").Append(H(intro)).Append("</p>");
            Section(html, consentTitle, consentBody);
            html.Append(Button("POTVRĐUJEM", consentUrl))
                .Append("<p style=\"margin:10px 0 0;text-align:center;font-size:14px;\">")
                .Append("<a href=\"").Append(H(consentUrl)).Append("\" style=\"color:").Append(TextColor).Append(";\">")
                .Append("Ne potvrđujem</a></p></div>");
            if (inviteBody != null)
            {
                Section(html, inviteTitle, inviteBody);
                if (inviteUrl != null)
                    html.Append(Button("PRIHVATI POZIV", inviteUrl));
                if (codeLine != null)
                    html.Append("<p style=\"margin:10px 0 0;font-size:14px;color:").Append(Muted).Append(";\">")
                        .Append(H(codeLine)).Append("</p>");
                html.Append("</div>");
            }
            html.Append("<p style=\"margin:20px 0 0;font-size:13px;color:").Append(Muted).Append(";\">")
                .Append(H(footer)).Append("</p></div></body></html>");

            return new RenderedEmail(subject, text.ToString(), html.ToString());
        }

        // otvara sivu karticu; zatvara je pozivatelj ("</div>")
        private static void Section(StringBuilder html, string title, string body) =>
            html.Append("<div style=\"background:").Append(Surface).Append(";border-radius:16px;padding:16px;margin:0 0 12px;\">")
                .Append("<p style=\"margin:0 0 10px;font-weight:bold;\">").Append(H(title)).Append("</p>")
                .Append("<p style=\"margin:0 0 14px;\">").Append(H(body)).Append("</p>");

        private static string Button(string label, string url) =>
            "<a href=\"" + H(url) + "\" style=\"display:block;background:" + Orange
            + ";color:#FFFFFF;text-decoration:none;font-weight:bold;text-align:center;padding:14px 16px;border-radius:12px;\">"
            + H(label) + "</a>";

        // nazivi iz payload 'shared_areas' (nepoznati se preskaču)
        public static List<string> SharedOf(JsonObject p)
        {
            var areas = new HashSet<string>();
            if (p["shared_areas"] is JsonArray arr)
                foreach (var a in arr)
                    if (a is JsonValue v && v.TryGetValue<string>(out var s))
                        areas.Add(s);
            return SharedLabels.Where(x => areas.Contains(x.Area)).Select(x => x.Label).ToList();
        }

        // "a", "a i b", "a, b i c" (kao andList u aplikaciji)
        public static string AndList(IReadOnlyList<string> items) =>
            items.Count < 2 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " i " + items[^1];

        private static string? Str(JsonObject p, string key) =>
            p[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

        private static bool IsHttps(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

        private static string H(string s) => WebUtility.HtmlEncode(s);
    }
}
