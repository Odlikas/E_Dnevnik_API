using System.Net;

namespace E_Dnevnik_API.ScrapingServices
{
    // izvor zahtjeva prema ocjene.skole.hr - koristi se samo za brojanje
    public enum CarnetRequestSource
    {
        Login,
        UserScrape,
        Background,
    }

    // jedino mjesto gdje se rade http klijenti za ocjene.skole.hr
    // svaki klijent nosi Odlikas User-Agent i broji odlazne zahtjeve
    public static class CarnetHttp
    {
        public const string UserAgent =
            "Odlikas/1.0 (+https://odlikas.hr; info@odlikas.hr)";

        // 120 sekundi zbog težih endpointa koji prolaze kroz puno stranica zaredom (npr. different grades)
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

        // pozivatelj je odgovoran za dispose klijenta (using var)
        // userKey (email) se koristi samo za brojanje jedinstvenih korisnika i nikad se ne logira
        public static HttpClient CreateClient(
            CookieContainer cookies,
            CarnetRequestSource source,
            string? userKey,
            TimeSpan? timeout = null,
            CarnetRequestStats? stats = null
        )
        {
            var handler = new HttpClientHandler { UseCookies = true, CookieContainer = cookies };
            var counting = new CarnetRequestCountingHandler(
                stats ?? CarnetRequestStats.Shared,
                source,
                userKey
            )
            {
                InnerHandler = handler,
            };

            var client = new HttpClient(counting) { Timeout = timeout ?? DefaultTimeout };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            return client;
        }
    }

    // broji svaki zahtjev koji stvarno ode prema CARNET-u
    // redirecte koje HttpClientHandler sam prati ne vidi, pa je broj donja granica
    public class CarnetRequestCountingHandler : DelegatingHandler
    {
        private readonly CarnetRequestStats _stats;
        private readonly CarnetRequestSource _source;
        private readonly string? _userKey;

        public CarnetRequestCountingHandler(
            CarnetRequestStats stats,
            CarnetRequestSource source,
            string? userKey
        )
        {
            _stats = stats;
            _source = source;
            _userKey = userKey;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            _stats.RecordRequest(_source, _userKey);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
