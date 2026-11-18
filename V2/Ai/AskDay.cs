namespace E_Dnevnik_API.V2.Ai
{
    // "Nova pitanja stižu sutra u 7:00": dan za limit počinje u 7:00 po zagrebačkom vremenu
    public static class AskDay
    {
        private static readonly TimeZoneInfo Zagreb = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb");

        public static DateTimeOffset StartOf(DateTimeOffset now)
        {
            var local = TimeZoneInfo.ConvertTime(now, Zagreb);
            var day = local.Hour < 7 ? local.Date.AddDays(-1) : local.Date;
            var start = day.AddHours(7);
            return new DateTimeOffset(start, Zagreb.GetUtcOffset(start));
        }
    }
}
