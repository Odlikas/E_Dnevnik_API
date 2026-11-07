using E_Dnevnik_API.V2.Sync;

namespace E_Dnevnik_API.Tests.Unit;

public class GradePageParserTests
{
    private static readonly Guid Child = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly SchoolYear Year = new(2026);

    private static Dictionary<string, ParsedGrade> ById(string html) =>
        GradePageParser.Parse(html, Year).ToDictionary(g => g.ActionId!);

    [Fact]
    public void Parse_ReadsAllDatedRows_IncludingNegativeAndPositive()
    {
        var grades = GradePageParser.Parse(GradePageFixtures.Subject, Year);
        Assert.Equal(4, grades.Count);
        Assert.Equal(
            new[] { "777000000003", "555000000004", "890289939970", "883595728650" },
            grades.Select(g => g.ActionId)
        );
        Assert.Equal(new short[] { 5, 4, 1, 3 }, grades.Select(g => g.Value));
    }

    [Fact]
    public void Parse_JoinsElementByActionId()
    {
        var g = ById(GradePageFixtures.Subject);
        Assert.Equal("funkcionalne sposobnosti", g["883595728650"].GradingElement);
        Assert.Equal("odgojni ishodi", g["890289939970"].GradingElement);
        // razmaci i novi red u imenu elementa se stisnu
        Assert.Equal("motorička znanja", g["777000000003"].GradingElement);
        Assert.Equal("motorička znanja", g["555000000004"].GradingElement);
    }

    [Fact]
    public void Parse_DatesWithYear()
    {
        var g = ById(GradePageFixtures.Subject);
        Assert.Equal(new DateOnly(2026, 10, 7), g["883595728650"].GradedOn);
        Assert.Equal(new DateOnly(2026, 10, 28), g["890289939970"].GradedOn);
        Assert.Equal(new DateOnly(2026, 11, 3), g["555000000004"].GradedOn);
        Assert.Equal(new DateOnly(2027, 1, 12), g["777000000003"].GradedOn);
    }

    [Fact]
    public void Parse_NotesDecodedTrimmedOrNull()
    {
        var g = ById(GradePageFixtures.Subject);
        Assert.Equal("Beep test", g["883595728650"].Note);
        Assert.Equal("nije radila bez opravdanja", g["890289939970"].Note);
        Assert.Equal("Skok & doskok \"u dalj\"", g["777000000003"].Note);
        Assert.Null(g["555000000004"].Note);
    }

    [Fact]
    public void Parse_EmptySubjectPage_NoGrades()
    {
        Assert.Empty(GradePageParser.Parse(GradePageFixtures.Empty, Year));
        Assert.Empty(GradePageParser.Parse("", Year));
        Assert.Empty(GradePageParser.Parse(null, Year));
    }

    [Fact]
    public void Parse_DateWithoutYear_UsesSchoolYear_AndUnknownElementIsOcjena()
    {
        var grades = GradePageParser.Parse(GradePageFixtures.NoYear, Year);
        // "+" nije ocjena 1-5
        var g = Assert.Single(grades);
        Assert.Equal(new DateOnly(2027, 1, 15), g.GradedOn);
        Assert.Equal((short)2, g.Value);
        Assert.Equal("ocjena", g.GradingElement);
        Assert.Null(g.Note);
        Assert.Equal("444000000001", g.ActionId);
    }

    [Fact]
    public void Hash_ByActionId_StableAcrossParses()
    {
        var first = GradeRows.WithHashes(Child, 5, GradePageParser.Parse(GradePageFixtures.Subject, Year));
        var second = GradeRows.WithHashes(Child, 5, GradePageParser.Parse(GradePageFixtures.Subject, Year));

        Assert.Equal(first.Select(r => r.SourceHash), second.Select(r => r.SourceHash));
        Assert.Equal(4, first.Select(r => r.SourceHash).Distinct().Count());
        Assert.Equal(GradeRows.Hash($"{Child:D}|action:883595728650|3"), first.Single(r => r.Value == 3).SourceHash);
        // ne ovisi o predmetu, datumu ni elementu
        var moved = GradeRows.WithHashes(
            Child,
            9,
            new[] { new ParsedGrade(new DateOnly(2026, 9, 1), 3, "x", "y", "883595728650") }
        );
        Assert.Equal(first.Single(r => r.Value == 3).SourceHash, moved[0].SourceHash);
    }
}
