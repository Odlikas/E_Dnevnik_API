namespace E_Dnevnik_API.V2.Ai
{
    // tijelo POST /v2/ai/homework: slikane stranice zadaće (1 do 5), base64 JPEG ili PNG
    public class HomeworkRequest
    {
        public List<string>? Pages { get; set; }
    }

    // 200 odgovor: redak public.homework_checks bez rješenja (njih dijete dobije tek preko homework_solution)
    public class HomeworkResponse
    {
        public Guid Id { get; set; }
        public string Subject { get; set; } = "";
        public string Title { get; set; } = "";
        public List<HomeworkLineDto> Lines { get; set; } = new();
    }

    public class HomeworkLineDto
    {
        public int Number { get; set; }
        public string Text { get; set; } = "";
        public bool Correct { get; set; }
        public string? Prompt { get; set; }
        public string? AnswerHint { get; set; }
        public List<string> Hints { get; set; } = new();
    }

    public static class HomeworkErrorCodes
    {
        public const string Unreadable = "unreadable";
    }

    // jedan provjereni redak; Answer i Accepted samo za pogrešne i nikad ne idu djetetu u odgovoru
    public record HomeworkLine(
        int Number,
        string Text,
        bool Correct,
        string? Prompt,
        string? AnswerHint,
        IReadOnlyList<string> Hints,
        string? Answer,
        IReadOnlyList<string> Accepted
    )
    {
        public HomeworkLineDto ToDto() =>
            new()
            {
                Number = Number,
                Text = Text,
                Correct = Correct,
                Prompt = Prompt,
                AnswerHint = AnswerHint,
                Hints = Hints.ToList(),
            };
    }

    // što je model pročitao, već provjereno i skraćeno
    public record HomeworkResult(string Subject, string Title, IReadOnlyList<HomeworkLine> Lines)
    {
        public bool HasMistakes => Lines.Any(l => !l.Correct);
    }
}
