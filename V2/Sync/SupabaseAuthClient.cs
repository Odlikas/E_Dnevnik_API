using System.Net.Http.Headers;
using System.Text.Json;

namespace E_Dnevnik_API.V2.Sync
{
    public interface ISupabaseAuthClient
    {
        bool IsConfigured { get; }

        // id korisnika ili null ako token ne vrijedi; baca HttpRequestException ako Supabase ne odgovara
        Task<Guid?> GetUserIdAsync(string accessToken, CancellationToken ct);
    }

    // provjera JWT-a preko GET {Supabase:Url}/auth/v1/user - bez JWT paketa
    public class SupabaseAuthClient : ISupabaseAuthClient
    {
        public const string HttpClientName = "supabase-auth";

        private readonly IHttpClientFactory _factory;
        private readonly string? _url;
        private readonly string? _apiKey;

        public SupabaseAuthClient(IHttpClientFactory factory, IConfiguration config)
        {
            _factory = factory;
            _url = config["Supabase:Url"]?.TrimEnd('/');
            _apiKey = config["Supabase:ApiKey"];
        }

        public bool IsConfigured => !string.IsNullOrEmpty(_url) && !string.IsNullOrEmpty(_apiKey);

        public async Task<Guid?> GetUserIdAsync(string accessToken, CancellationToken ct)
        {
            var client = _factory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_url}/auth/v1/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("apikey", _apiKey);

            using var response = await client.SendAsync(request, ct);
            if ((int)response.StatusCode >= 500)
                throw new HttpRequestException("supabase auth unavailable", null, response.StatusCode);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && Guid.TryParse(id.GetString(), out var userId))
                return userId;
            return null;
        }
    }
}
