using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace E_Dnevnik_API.V2.Ai
{
    public interface IAiChatClient
    {
        bool IsConfigured { get; }
        string Provider { get; }

        // baca HttpRequestException / TaskCanceledException kad model ne odgovori
        Task<AiReply> AskAsync(string system, IReadOnlyList<ChatTurn> history, string question, CancellationToken ct);
    }

    // OpenAI-kompatibilni /chat/completions (freellmapi sada; drugi pružatelj = samo env postavke)
    public class AiChatClient : IAiChatClient
    {
        public const string HttpClientName = "ai";

        private readonly IHttpClientFactory _factory;
        private readonly AiOptions _options;

        public AiChatClient(IHttpClientFactory factory, IOptions<AiOptions> options)
        {
            _factory = factory;
            _options = options.Value;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);
        public string Provider => _options.Provider;

        public async Task<AiReply> AskAsync(
            string system,
            IReadOnlyList<ChatTurn> history,
            string question,
            CancellationToken ct
        )
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
            foreach (var turn in history)
                messages.Add(new JsonObject { ["role"] = turn.FromChild ? "user" : "assistant", ["content"] = turn.Text });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = question });
            var body = new JsonObject
            {
                ["model"] = _options.Model,
                ["messages"] = messages,
                ["temperature"] = 0.4,
                ["max_tokens"] = 700,
            };

            var client = _factory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"ai answered {(int)response.StatusCode}", null, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            var content = root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var c)
                && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(content))
                throw new HttpRequestException("ai answered without content");

            int Tokens(string name) =>
                root.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var t) && t.TryGetInt32(out var n) ? n : 0;
            return new AiReply(AskAnswerParser.Parse(content), Tokens("prompt_tokens"), Tokens("completion_tokens"));
        }
    }
}
