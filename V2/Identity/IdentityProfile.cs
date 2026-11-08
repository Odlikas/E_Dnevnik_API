using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using E_Dnevnik_API.ScrapingServices;
using HtmlAgilityPack;

namespace E_Dnevnik_API.V2.Identity
{
    // što /v2/identity treba s /personal_data
    public record IdentityProfile(
        string FullName,
        string? School,
        string? ClassRaw,
        string? Program,
        DateOnly? BirthDate,
        string? Username
    )
    {
        public string FirstName => FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    // čisti dio /v2/identity: parsiranje profila, hash, dob, razred
    public static class IdentityRules
    {
        public const int ConsentAge = 16;

        private static readonly Regex BirthDateRx = new(
            @"datum\s+ro(?:đ|d|dj)enja\D{0,120}?(\d{1,2})\s*\.\s*(\d{1,2})\s*\.\s*(\d{4})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );

        private static readonly Regex UsernameRx = new(
            @"^[a-z0-9][a-z0-9._-]*@skole\.hr$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );

        // ime, škola, razred i program kroz postojeći scraper; datum rođenja i AAI ime iz teksta stranice
        public static IdentityProfile? ParseProfile(string html)
        {
            var p = new StudentProfileScraperService().Parse(html).StudentProfile;
            var name = OrNull(p?.StudentName);
            if (name is null)
                return null;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var text = Regex.Replace(HtmlEntity.DeEntitize(doc.DocumentNode.InnerText), @"\s+", " ");

            DateOnly? birth = null;
            var m = BirthDateRx.Match(text);
            if (m.Success
                && DateOnly.TryParseExact(
                    $"{int.Parse(m.Groups[1].Value):00}.{int.Parse(m.Groups[2].Value):00}.{m.Groups[3].Value}",
                    "dd.MM.yyyy",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var d
                ))
                birth = d;

            // AAI ime je cijeli tekst jednog elementa (u spojenom tekstu bi se slijepilo s oznakom)
            var user = doc.DocumentNode.Descendants()
                .Where(n => n.NodeType == HtmlNodeType.Text)
                .Select(n => UsernameRx.Match(HtmlEntity.DeEntitize(n.InnerText).Trim()))
                .FirstOrDefault(m => m.Success && m.Index == 0);
            return new IdentityProfile(
                name,
                OrNull(p!.StudentSchool),
                OrNull(p.StudentClassRaw),
                OrNull(p.StudentProgram),
                birth,
                user?.Value.ToLowerInvariant()
            );
        }

        // za dnevnik kad datum rođenja nije pročitan: oznake polja i oblik teksta oko "rođ"
        // (znamenke -> 9, slova -> x), bez osobnih podataka
        public static string BirthShape(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var text = Regex.Replace(HtmlEntity.DeEntitize(doc.DocumentNode.InnerText), @"\s+", " ");
            static string Mask(string t) => Regex.Replace(Regex.Replace(t, @"\d", "9"), @"[^\W\d_]", "x");
            var hits = Regex.Matches(text, @"ro(?:đ|d|dj)en", RegexOptions.IgnoreCase)
                .Select(m => text.Substring(Math.Max(0, m.Index - 20), Math.Min(100, text.Length - Math.Max(0, m.Index - 20))))
                .Select(t => Regex.Replace(t, @"(?i)(datum|mjesto|ro(?:đ|d|dj)enja?)|(.)", m => m.Groups[1].Success ? m.Value : Mask(m.Value)))
                .Take(3);
            var labels = doc.DocumentNode.Descendants()
                .Where(n => n.GetAttributeValue("class", "").Contains("label"))
                .Select(n => HtmlEntity.DeEntitize(n.InnerText).Trim())
                .Where(t => t.Length is > 0 and < 40)
                .Take(20);
            return $"labels=[{string.Join(" | ", labels)}] near=[{string.Join(" || ", hits)}]";
        }

        // hex sha256(salt + lower(username)), UTF-8
        public static string Hash(string salt, string username)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(salt + username.Trim().ToLowerInvariant()));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        // ispod 16 (ili dob nepoznata) traži se pristanak roditelja
        public static bool NeedsParentConsent(DateOnly? birthDate, DateOnly today) =>
            birthDate is not { } b || b.AddYears(ConsentAge) > today;

        // "4.E-4" -> 4; null ako to nije razred srednje škole (1-4) ili je škola osnovna
        public static short? HighSchoolGrade(string? classRaw, string? school)
        {
            if (school is { } s && IsPrimarySchool(s))
                return null;
            var m = Regex.Match(classRaw ?? "", @"^\s*(\d{1,2})");
            if (!m.Success || !short.TryParse(m.Groups[1].Value, out var g) || g < 1 || g > 4)
                return null;
            return g;
        }

        private static bool IsPrimarySchool(string school)
        {
            var s = school.Trim();
            return s.StartsWith("OŠ", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("OS ", StringComparison.OrdinalIgnoreCase)
                || s.Contains("osnovna škola", StringComparison.OrdinalIgnoreCase)
                || s.Contains("osnovna skola", StringComparison.OrdinalIgnoreCase);
        }

        // kad profil ne pokazuje korisničko ime: svaki dio imena prije @ ("ivan.maric2") mora biti
        // dio imena na profilu ("Ivan Marić"), bez kvačica i brojeva. Inače bi tuđe korisničko ime
        // s vlastitim kolačićem preuzelo tuđi račun.
        public static bool UsernameMatchesName(string username, string fullName)
        {
            var at = username.IndexOf('@');
            var local = (at >= 0 ? username[..at] : username).ToLowerInvariant();
            var parts = Regex.Split(local, @"[._\-]+")
                .Select(p => Regex.Replace(p, @"\d+", ""))
                .Where(p => p.Length > 0)
                .ToList();
            if (parts.Count == 0)
                return false;
            // đ je u AAI imenima "d" ili "dj"
            var names = Regex.Split(Fold(fullName), @"[\s\-]+")
                .Where(n => n.Length > 0)
                .SelectMany(n => new[] { n, n.Replace("dj", "d") })
                .ToHashSet();
            return parts.All(p => names.Contains(Fold(p)));
        }

        // mala slova bez kvačica: "Marić" -> "maric", "Đurđa" -> "djurdja"
        public static string Fold(string text)
        {
            var sb = new StringBuilder();
            foreach (var ch in text.ToLowerInvariant())
            {
                sb.Append(
                    ch switch
                    {
                        'č' or 'ć' => "c",
                        'š' => "s",
                        'ž' => "z",
                        'đ' => "dj",
                        _ => ch.ToString(),
                    }
                );
            }
            return sb.ToString().Normalize(NormalizationForm.FormD).Where(c =>
                CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark
            ).Aggregate(new StringBuilder(), (b, c) => b.Append(c)).ToString();
        }

        public static string? OrNull(string? value) =>
            string.IsNullOrWhiteSpace(value) || value.Trim() == "N/A" ? null : value.Trim();
    }
}
