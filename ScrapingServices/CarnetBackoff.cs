namespace E_Dnevnik_API.ScrapingServices
{
    // odlučuje treba li prekinuti pozadinski posao jer je CARNET preopterećen
    // 429, 5xx, timeout ili mrežna greška = stajemo i čekamo idući zakazani run
    public static class CarnetBackoff
    {
        public static bool ShouldBackOff(Exception ex) =>
            ex switch
            {
                ScraperException se => IsOverloadStatus(se.StatusCode),
                HttpRequestException hre => hre.StatusCode is null
                    || IsOverloadStatus((int)hre.StatusCode),
                // HttpClient timeout baca TaskCanceledException (s TimeoutException unutra)
                TaskCanceledException => true,
                TimeoutException => true,
                _ => false,
            };

        public static bool IsOverloadStatus(int statusCode) =>
            statusCode == 429 || statusCode >= 500;
    }
}
