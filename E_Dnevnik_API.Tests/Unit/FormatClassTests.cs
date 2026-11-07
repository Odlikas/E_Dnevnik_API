using E_Dnevnik_API.V2.Sync;

namespace E_Dnevnik_API.Tests.Unit;

public class FormatClassTests
{
    [Theory]
    [InlineData("4b", "4.b")]
    [InlineData("4E4", "4.E4")]
    [InlineData("5.b", "5.b")]
    [InlineData("1 a", "1.a")]
    [InlineData("N/A", null)]
    [InlineData("", null)]
    public void Puts_the_dot_back_after_the_grade(string input, string? expected) =>
        Assert.Equal(expected, SyncSummary.FormatClass(input));
}
