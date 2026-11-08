using E_Dnevnik_API.V2.Sync;

namespace E_Dnevnik_API.Tests.Unit;

public class SchedulePageParserTests
{
    private static readonly SubjectRow[] Seed =
    {
        new(1, "MAT", "Matematika"),
        new(2, "HRV", "Hrvatski jezik"),
        new(3, "ENG", "Engleski jezik"),
        new(8, "FIZ", "Fizika"),
    };

    [Fact]
    public void Parse_Week()
    {
        var lessons = SchedulePageParser.Parse(SchedulePageFixtures.Week)!;

        var monday = lessons.Where(l => l.Weekday == 1).ToList();
        // 4. sat je prazan, ne dolazi
        Assert.Equal(new short[] { 1, 2, 3, 5, 6 }, monday.Select(l => l.Period));
        Assert.Equal(
            new[] { "Matematika", "Engleski jezik", "Hrvatski jezik", "Fizika", "Matematika" },
            monday.Select(l => l.Subject)
        );
        Assert.Equal(new TimeOnly(8, 0), monday[0].StartsAt);
        Assert.Equal(new TimeOnly(8, 45), monday[0].EndsAt);
        // "12.20-13.05"
        Assert.Equal(new TimeOnly(12, 20), monday[4].StartsAt);
        Assert.Equal(new TimeOnly(13, 5), monday[4].EndsAt);

        // bez broja sata i vremena: redni broj retka
        var tuesday = lessons.Where(l => l.Weekday == 2).ToList();
        Assert.Equal(new short[] { 1, 2 }, tuesday.Select(l => l.Period));
        Assert.All(tuesday, l => Assert.Null(l.StartsAt));
        Assert.Equal("Fizika", tuesday[0].Subject);

        var wednesday = lessons.Where(l => l.Weekday == 3).ToList();
        Assert.Equal(new[] { "Politika i gospodarstvo", "Sat razrednika" }, wednesday.Select(l => l.Subject));
        Assert.Equal(new TimeOnly(14, 0), wednesday[0].StartsAt);

        Assert.DoesNotContain(lessons, l => l.Weekday >= 4);
        Assert.Equal(9, lessons.Count);
    }

    [Fact]
    public void Parse_NoTimetableIsEmpty() => Assert.Empty(SchedulePageParser.Parse(SchedulePageFixtures.NoTimetable)!);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(SchedulePageFixtures.Unknown)]
    public void Parse_UnknownPageIsNull(string? html) => Assert.Null(SchedulePageParser.Parse(html));

    [Fact]
    public async Task Timetable_MapsSubjectsLikeGrades()
    {
        var lookups = new List<string>();
        var resolver = new SubjectResolver(
            new SubjectMapper(Seed),
            (name, _) =>
            {
                lookups.Add(name);
                return Task.FromResult((short)(100 + lookups.Count));
            }
        );
        // Matematika je u ovom syncu već dobila id s popisa predmeta
        var idByName = new Dictionary<string, short> { [SubjectMapper.Normalize("Matematika")] = 1 };

        var rows = (await SyncService.TimetableAsync(SchedulePageFixtures.Week, idByName, resolver))!;

        Assert.Equal(9, rows.Count);
        Assert.Equal(new TimetableRow(1, 1, 1, new TimeOnly(8, 0), new TimeOnly(8, 45)), rows[0]);
        Assert.Equal((short)3, rows[1].SubjectId);
        Assert.Equal((short)2, rows[2].SubjectId);
        Assert.Equal((short)8, rows[3].SubjectId);
        // nepoznat predmet i sat razrednika: stvara ih baza, svaki jednom
        Assert.Equal(new[] { "Politika i gospodarstvo", "Sat razrednika" }, lookups);
        Assert.Equal(new short[] { 101, 102 }, rows.Where(r => r.Weekday == 3).Select(r => r.SubjectId));
    }

    [Fact]
    public async Task Timetable_NullWhenPageMissing()
    {
        var resolver = new SubjectResolver(new SubjectMapper(Seed), (_, _) => Task.FromResult((short)0));
        Assert.Null(await SyncService.TimetableAsync(null, new Dictionary<string, short>(), resolver));
        Assert.Empty((await SyncService.TimetableAsync(SchedulePageFixtures.NoTimetable, new Dictionary<string, short>(), resolver))!);
    }
}
