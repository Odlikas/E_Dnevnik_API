using E_Dnevnik_API.ScrapingServices;
using RichardSzalay.MockHttp;

namespace E_Dnevnik_API.Tests.Scrapers;

public class ProfileScraperTests
{
    private const string ProfileHtml = """
        <html><body>
        <div id="page-wrapper">
          <div class="logged-in-user">
            <div class="user-name"><span>Ivan Horvat</span></div>
          </div>
          <div class="school-data">
            <div class="class">
              <span class="bold">2A</span>
              <span class="class-schoolyear">2025/2026</span>
            </div>
            <div class="school">
              <span class="school-name">Tehnicka skola</span>
              <span class="school-city">Zagreb</span>
              <div class="classmaster"><span>Razrednik</span><span>Marko Matic</span></div>
            </div>
          </div>
          <div class="l-two-columns"><span class="column bold t-right">Redni broj</span><span class="column">x</span></div>
          <div class="l-two-columns"><span class="column bold t-right">Ime</span><span class="column">x</span></div>
          <div class="l-two-columns"><span class="column bold t-right">Prezime</span><span class="column">x</span></div>
          <div class="l-two-columns"><span class="column bold t-right">OIB</span><span class="column">x</span></div>
          <div class="l-two-columns"><span class="column bold t-right">Datum rođenja</span><span class="column">x</span></div>
          <div class="l-two-columns"><span class="column bold t-right">Mjesto rođenja</span><span class="column">x</span></div>
          <div class="l-two-columns"><span class="column bold t-right">Adresa</span><span class="column">x</span></div>
          <div class="l-two-columns"><span class="column bold t-right">Program</span><span class="column">Tehnicar za racunalstvo<br></span></div>
          <!-- e-Dnevnik puts the parent's name right after Program: it must not be read as the programme -->
          <div class="l-two-columns"><span class="column bold t-right">Ime i prezime</span><span class="column">Ana Matic</span></div>
        </div>
        </body></html>
        """;

    [Fact]
    public async Task ScrapeStudentProfile_ParsesNameGradeSchoolCityAndProgram()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://ocjene.skole.hr/personal_data").Respond("text/html", ProfileHtml);
        var client = mockHttp.ToHttpClient();

        var service = new StudentProfileScraperService();
        var result = await service.ScrapeStudentProfile(client);

        Assert.Equal("Ivan Horvat", result.StudentProfile!.StudentName);
        Assert.Equal("2A", result.StudentProfile.StudentGrade);
        Assert.Equal("2025/2026", result.StudentProfile.StudentSchoolYear);
        Assert.Equal("Zagreb", result.StudentProfile.StudentSchoolCity);
        Assert.Equal("Tehnicar za racunalstvo", result.StudentProfile.StudentProgram);
        // the row after Program is a parent, not the programme (D317); its name is kept (D318)
        Assert.Equal(new[] { "Ana Matic" }, result.StudentProfile.ParentNames);
    }
}
