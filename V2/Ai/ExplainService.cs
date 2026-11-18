using System.Diagnostics;

namespace E_Dnevnik_API.V2.Ai
{
    public enum ExplainOutcomeKind
    {
        Ok,
        NotFound,
        PremiumRequired,
        LimitReached,
        NothingToExplain,
        AiUnavailable,
    }

    public record ExplainOutcome(ExplainOutcomeKind Kind, ExplainResponse? Response = null);

    public interface IExplainService
    {
        bool IsConfigured { get; }
        Task<ExplainOutcome> ExplainAsync(Guid parentId, Guid notificationId, CancellationToken ct);
    }

    // "Objasni Luki": roditelj -> Joso piše bilješku -> djetetove Bilješke.
    // Besplatni paket: jedno objašnjenje po obitelji, zatim Odlikaš+. Isti zahtjev dvaput vraća istu bilješku.
    public class ExplainService : IExplainService
    {
        public const int FreeExplanations = 1;

        // Odlikaš+: kočnica protiv zloupotrebe
        public const int PremiumDailyCap = 20;

        private readonly IExplainRepository _repo;
        private readonly IAiRepository _calls;
        private readonly IAiChatClient _ai;
        private readonly TimeProvider _time;
        private readonly ILogger<ExplainService> _logger;

        public ExplainService(
            IExplainRepository repo,
            IAiRepository calls,
            IAiChatClient ai,
            TimeProvider time,
            ILogger<ExplainService> logger
        )
        {
            _repo = repo;
            _calls = calls;
            _ai = ai;
            _time = time;
            _logger = logger;
        }

        public bool IsConfigured => _repo.IsConfigured && _calls.IsConfigured && _ai.IsConfigured;

        public async Task<ExplainOutcome> ExplainAsync(Guid parentId, Guid notificationId, CancellationToken ct)
        {
            var help = await _repo.GetContextAsync(parentId, notificationId, ct);
            if (help is null)
                return new ExplainOutcome(ExplainOutcomeKind.NotFound);

            if (await _repo.FindNoteAsync(help.FamilyId, help.Source, ct) is { } existing)
                return Done(existing.Id, existing.Title, existing.Steps, wasFree: false);

            var wasFree = false;
            if (help.Premium)
            {
                var today = await _repo.CountExplanationsAsync(help.FamilyId, AskDay.StartOf(_time.GetUtcNow()), ct);
                if (today >= PremiumDailyCap)
                    return new ExplainOutcome(ExplainOutcomeKind.LimitReached);
            }
            else
            {
                var ever = await _repo.CountExplanationsAsync(help.FamilyId, DateTimeOffset.MinValue, ct);
                if (ever >= FreeExplanations)
                    return new ExplainOutcome(ExplainOutcomeKind.PremiumRequired);
                wasFree = true;
            }

            var instruction = ExplainPrompt.Instruction(help.Topic, help.LastQuestion);
            if (instruction.Length == 0)
                return new ExplainOutcome(ExplainOutcomeKind.NothingToExplain);

            var watch = Stopwatch.StartNew();
            AiCompletion completion;
            try
            {
                completion = await _ai.WriteAsync(
                    ExplainPrompt.System(help.Grade, help.SchoolLevel),
                    instruction,
                    ct
                );
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("[v2/ai/explain] model failed for family {FamilyId}: {Message}", help.FamilyId, ex.Message);
                await _calls.LogCallAsync(
                    new AiCall(help.FamilyId, "explain", _ai.Provider, false, 0, 0, 0, (int)watch.ElapsedMilliseconds),
                    ct
                );
                return new ExplainOutcome(ExplainOutcomeKind.AiUnavailable);
            }

            var note = ExplainAnswerParser.Parse(completion.Content);
            await _calls.LogCallAsync(
                new AiCall(
                    help.FamilyId,
                    "explain",
                    $"{_ai.Provider}/{completion.Model}",
                    note is not null,
                    0,
                    completion.InputTokens,
                    completion.OutputTokens,
                    (int)watch.ElapsedMilliseconds
                ),
                ct
            );
            if (note is null)
                return new ExplainOutcome(ExplainOutcomeKind.AiUnavailable);

            var id = await _repo.SaveNoteAsync(help, note, DetailOf(note.Sections.Count), ct);
            return Done(id, note.Title, note.Sections.Count, wasFree);
        }

        // ispod predmeta u popisu bilješki: "Objasnio Joso · 3 dijela"
        public static string DetailOf(int sections) => $"Objasnio Joso · {sections} {(sections == 1 ? "dio" : "dijela")}";

        private static ExplainOutcome Done(Guid id, string title, int steps, bool wasFree) =>
            new(
                ExplainOutcomeKind.Ok,
                new ExplainResponse
                {
                    NoteId = id,
                    Title = title,
                    Steps = steps,
                    WasFree = wasFree,
                }
            );
    }
}
