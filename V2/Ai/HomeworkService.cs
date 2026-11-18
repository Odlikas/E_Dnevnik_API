using System.Diagnostics;

namespace E_Dnevnik_API.V2.Ai
{
    public enum HomeworkOutcomeKind
    {
        Ok,
        NotAChild,
        LimitReached,
        Unreadable,
        AiUnavailable,
    }

    public record HomeworkOutcome(HomeworkOutcomeKind Kind, HomeworkResponse? Response = null);

    public interface IHomeworkService
    {
        bool IsConfigured { get; }
        Task<HomeworkOutcome> CheckAsync(Guid authUserId, IReadOnlyList<(byte[] Bytes, string Mime)> pages, CancellationToken ct);
    }

    // "Provjera zadaće": slikane stranice -> model s vidom -> homework_checks; slike se ovdje ne spremaju
    public class HomeworkService : IHomeworkService
    {
        // provjere na dan; bonus minute broje samo 2 zadaće, ostalo su ponovna slikanja
        public const int HardDailyCap = 8;
        public const int MaxPages = 5;

        private readonly IAiRepository _ai;
        private readonly IHomeworkRepository _repo;
        private readonly IAiChatClient _client;
        private readonly TimeProvider _time;
        private readonly ILogger<HomeworkService> _logger;

        public HomeworkService(
            IAiRepository ai,
            IHomeworkRepository repo,
            IAiChatClient client,
            TimeProvider time,
            ILogger<HomeworkService> logger
        )
        {
            _ai = ai;
            _repo = repo;
            _client = client;
            _time = time;
            _logger = logger;
        }

        public bool IsConfigured => _ai.IsConfigured && _repo.IsConfigured && _client.IsConfigured;

        public async Task<HomeworkOutcome> CheckAsync(
            Guid authUserId,
            IReadOnlyList<(byte[] Bytes, string Mime)> pages,
            CancellationToken ct
        )
        {
            var asker = await _ai.GetAskerAsync(authUserId, ct);
            if (asker is null)
                return new HomeworkOutcome(HomeworkOutcomeKind.NotAChild);
            var taken = await _repo.CountChecksAsync(asker.ChildId, AskDay.StartOf(_time.GetUtcNow()), ct);
            if (taken >= HardDailyCap)
                return new HomeworkOutcome(HomeworkOutcomeKind.LimitReached);

            var watch = Stopwatch.StartNew();
            AiCompletion completion;
            try
            {
                completion = await _client.ReadImagesAsync(
                    HomeworkPrompt.System(asker.Grade, asker.SchoolLevel),
                    HomeworkPrompt.Instruction,
                    pages,
                    ct
                );
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("[v2/ai/homework] model failed for family {FamilyId}: {Message}", asker.FamilyId, ex.Message);
                await _ai.LogCallAsync(
                    new AiCall(asker.FamilyId, "homework", _client.Provider, false, pages.Count, 0, 0, (int)watch.ElapsedMilliseconds),
                    ct
                );
                return new HomeworkOutcome(HomeworkOutcomeKind.AiUnavailable);
            }

            var result = HomeworkAnswerParser.Parse(completion.Content);
            await _ai.LogCallAsync(
                new AiCall(
                    asker.FamilyId,
                    "homework",
                    $"{_client.Provider}/{completion.Model}",
                    result is not null,
                    pages.Count,
                    completion.InputTokens,
                    completion.OutputTokens,
                    (int)watch.ElapsedMilliseconds
                ),
                ct
            );
            if (result is null)
                return new HomeworkOutcome(HomeworkOutcomeKind.Unreadable);

            var id = await _repo.SaveCheckAsync(asker, result, ct);
            return new HomeworkOutcome(
                HomeworkOutcomeKind.Ok,
                new HomeworkResponse
                {
                    Id = id,
                    Subject = result.Subject,
                    Title = result.Title,
                    Lines = result.Lines.Select(l => l.ToDto()).ToList(),
                }
            );
        }
    }
}
