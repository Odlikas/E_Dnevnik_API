using System.Text.RegularExpressions;
using E_Dnevnik_API.Models.ScrapeStudentProfile;
using HtmlAgilityPack;

namespace E_Dnevnik_API.ScrapingServices
{
    // skida osobne podatke učenika - ime, razred, škola, razrednik...
    public class StudentProfileScraperService
    {
        public async Task<StudentProfileResult> ScrapeStudentProfile(HttpClient client)
        {
            var scrapeResponse = await client.GetAsync("https://ocjene.skole.hr/personal_data");
            if (!scrapeResponse.IsSuccessStatusCode)
                throw new ScraperException(
                    (int)scrapeResponse.StatusCode,
                    "nije uspjelo dohvatiti osobne podatke."
                );

            var scrapeHtmlContent = await scrapeResponse.Content.ReadAsStringAsync();
            return ExtractScrapeData(scrapeHtmlContent);
        }

        // isto parsiranje za HTML koji je netko drugi već skinuo (/v2/identity)
        public StudentProfileResult Parse(string htmlContent) => ExtractScrapeData(htmlContent);

        private StudentProfileResult ExtractScrapeData(string htmlContent)
        {
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(htmlContent);

            var studentNameNode = htmlDoc.DocumentNode.SelectSingleNode(
                "//div[@id='page-wrapper']//div[@class='logged-in-user']//div[@class='user-name']/span"
            );
            var studentName =
                studentNameNode != null ? CleanText(studentNameNode.InnerText) : "N/A";

            var studentGradeNode = htmlDoc.DocumentNode.SelectSingleNode(
                "//div[@id='page-wrapper']//div[@class='school-data']//div[@class='class']//span[@class='bold']"
            );
            var studentGrade =
                studentGradeNode != null ? CleanText(studentGradeNode.InnerText) : "N/A";
            var studentClassRaw =
                studentGradeNode != null
                    ? Regex.Replace(HtmlEntity.DeEntitize(studentGradeNode.InnerText), "\\s+", " ").Trim()
                    : null;

            var studentSchoolYearNode = htmlDoc.DocumentNode.SelectSingleNode(
                "//div[@id='page-wrapper']//div[@class='school-data']//div[@class='class']//span[@class='class-schoolyear']"
            );
            var studentSchoolYear =
                studentSchoolYearNode != null ? CleanText(studentSchoolYearNode.InnerText) : "N/A";

            var studentSchoolNode = htmlDoc.DocumentNode.SelectSingleNode(
                "//div[@id='page-wrapper']//div[@class='school-data']//div[@class='school']//span[@class='school-name']"
            );
            var studentSchool =
                studentSchoolNode != null ? CleanText(studentSchoolNode.InnerText) : "N/A";

            var studentSchoolCityNode = htmlDoc.DocumentNode.SelectSingleNode(
                "//div[@id='page-wrapper']//div[@class='school-data']//div[@class='school']//span[@class='school-city']"
            );
            var studentSchoolCity =
                studentSchoolCityNode != null ? CleanText(studentSchoolCityNode.InnerText) : "N/A";

            var classMasterNode = htmlDoc.DocumentNode.SelectSingleNode(
                "//div[@id='page-wrapper']//div[@class='school-data']//div[@class='school']//div[@class='classmaster']/span[2]"
            );
            var classMaster =
                classMasterNode != null ? CleanText(classMasterNode.InnerText) : "N/A";

            // program učenja: redak s oznakom "Program" (redoslijed redaka nije stalan; na 9. mjestu
            // može biti ime roditelja)
            var studentProgramNode = htmlDoc.DocumentNode.SelectSingleNode(
                "//div[@class='l-two-columns'][span[contains(@class,'bold')][normalize-space()='Program']]/span[@class='column']"
            );
            var studentProgram =
                studentProgramNode != null ? CleanText(studentProgramNode.InnerText) : "N/A";

            // roditelji: redci "Ime i prezime" nakon retka "Program" (učenikovo ime su zasebni "Ime" i "Prezime")
            var parentNames = new List<string>();
            var rows = htmlDoc.DocumentNode.SelectNodes("//div[@class='l-two-columns']");
            if (rows != null)
            {
                var afterProgram = false;
                foreach (var row in rows)
                {
                    var label = Plain(row.SelectSingleNode("span[contains(@class,'bold')]")?.InnerText);
                    if (label == "Program")
                        afterProgram = true;
                    else if (afterProgram && label == "Ime i prezime")
                    {
                        var parent = Plain(row.SelectSingleNode("span[@class='column']")?.InnerText);
                        if (parent.Length is > 0 and <= 120 && !parentNames.Contains(parent) && parentNames.Count < 2)
                            parentNames.Add(parent);
                    }
                }
            }

            var studentProfile = new StudentProfileInfo
            {
                StudentName = studentName,
                StudentGrade = studentGrade,
                StudentClassRaw = studentClassRaw,
                StudentSchoolYear = studentSchoolYear,
                StudentSchool = studentSchool,
                StudentSchoolCity = studentSchoolCity,
                ClassMaster = classMaster,
                StudentProgram = studentProgram,
                ParentNames = parentNames,
            };

            return new StudentProfileResult { StudentProfile = studentProfile };
        }

        // čisti tekst - uklanja razmake i specijalne znakove, ostavlja samo slova, brojke i hrvatska slova
        // tekst kako piše (crtice i apostrofi u imenima ostaju), jedan razmak
        private static string Plain(string? text) =>
            Regex.Replace(HtmlEntity.DeEntitize(text ?? ""), "\\s+", " ").Trim();

        private string CleanText(string text)
        {
            text = text.Trim();
            text = Regex.Replace(text, @"[^a-zA-Z0-9čćšđžČĆŠĐŽ\s/]", "");
            text = Regex.Replace(text, "\\s+", " ");
            return text;
        }
    }
}
