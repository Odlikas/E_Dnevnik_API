using System.Text.RegularExpressions;

namespace E_Dnevnik_API.V2.Sync
{
    // školska godina: mjeseci 8-12 pripadaju početnoj godini, 1-7 idućoj
    public readonly record struct SchoolYear(int StartYear)
    {
        public DateOnly FirstDay => new(StartYear, 8, 1);
        public DateOnly LastDay => new(StartYear + 1, 7, 31);

        // npr. "2026/2027" za child_subjects.school_year
        public string Label => $"{StartYear}/{StartYear + 1}";

        public static SchoolYear Containing(DateOnly day) =>
            new(day.Month >= 8 ? day.Year : day.Year - 1);

        public static SchoolYear Current(TimeProvider time) => Containing(ZagrebToday(time));

        public static DateOnly ZagrebToday(TimeProvider time)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb");
            var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
            return DateOnly.FromDateTime(local.DateTime);
        }

        public int YearForMonth(int month) => month >= 8 ? StartYear : StartYear + 1;

        private static readonly Regex DateRegex = new(
            @"(?<d>\d{1,2})\s*\.\s*(?<m>\d{1,2})\s*\.(?:\s*(?<y>\d{4})\s*\.?)?",
            RegexOptions.Compiled
        );

        // "10.12." -> 10.12. u ovoj školskoj godini; "10.12.2026." -> taj dan; null ako nije datum
        public DateOnly? ParseDate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var m = DateRegex.Match(text);
            if (!m.Success)
                return null;

            var day = int.Parse(m.Groups["d"].Value);
            var month = int.Parse(m.Groups["m"].Value);
            if (month is < 1 or > 12)
                return null;
            var year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value) : YearForMonth(month);
            if (day < 1 || day > DateTime.DaysInMonth(year, month))
                return null;
            return new DateOnly(year, month, day);
        }
    }
}
