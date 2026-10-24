using System.Net;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Http;

namespace E_Dnevnik_API.ScrapingServices
{
    public record LoginResult(
        HttpClient? Client,
        CookieContainer? Cookies,
        int StatusCode,
        string Error
    );

    public static class EduHrLoginService
    {
        private const string LoginUrl = "https://ocjene.skole.hr/login";

        // logira korisnika na e-dnevnik i vraća http klijent s aktivnom sesijom
        // pozivatelj je odgovoran za dispose klijenta (using var)
        public static async Task<LoginResult> LoginAsync(string email, string password)
        {
            var cookies = new CookieContainer();
            var client = CarnetHttp.CreateClient(cookies, CarnetRequestSource.Login, email);

            var loginPageResponse = await client.GetAsync(LoginUrl);
            var loginPageContent = await loginPageResponse.Content.ReadAsStringAsync();

            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(loginPageContent);
            var csrfToken = htmlDoc
                .DocumentNode.SelectSingleNode("//input[@name='csrf_token']")
                ?.Attributes["value"]
                ?.Value;

            if (string.IsNullOrEmpty(csrfToken))
            {
                client.Dispose();
                return new LoginResult(
                    null,
                    null,
                    StatusCodes.Status500InternalServerError,
                    "csrf token nije pronađen."
                );
            }

            var formData = new Dictionary<string, string>
            {
                ["username"] = email,
                ["password"] = password,
                ["csrf_token"] = csrfToken,
            };

            var loginContent = new FormUrlEncodedContent(formData);
            client.DefaultRequestHeaders.Referrer = new Uri(LoginUrl);
            var loginResponse = await client.PostAsync(LoginUrl, loginContent);

            if (!loginResponse.IsSuccessStatusCode)
            {
                client.Dispose();
                return new LoginResult(
                    null,
                    null,
                    (int)loginResponse.StatusCode,
                    "prijava nije uspjela, provjeri email i lozinku."
                );
            }

            // aktiviramo najnoviji razred kako bi /course i ostali endpointi vraćali ispravne podatke
            // kad školska godina završi, e-dnevnik nema "aktivni" razred pa /course vraća prazno
            await ActivateMostRecentClassAsync(client);

            // i klijent i cookie container vraćamo - pozivatelj odlučuje što zadržati
            return new LoginResult(
                client,
                cookies,
                StatusCodes.Status200OK,
                string.Empty
            );
        }

        // navigira na /class, uzima prvi link i GETa ga da aktivira razred server-side
        private static async Task ActivateMostRecentClassAsync(HttpClient client)
        {
            try
            {
                var classPageResponse = await client.GetAsync("https://ocjene.skole.hr/class");
                if (!classPageResponse.IsSuccessStatusCode)
                    return;

                var classPageContent = await classPageResponse.Content.ReadAsStringAsync();
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(classPageContent);

                var classLink = htmlDoc
                    .DocumentNode.SelectSingleNode(
                        "//div[contains(@class,'class-menu-vertical')]//div[@class='class-info']/a"
                    )
                    ?.Attributes["href"]
                    ?.Value;

                if (string.IsNullOrEmpty(classLink))
                    return;

                await client.GetAsync($"https://ocjene.skole.hr{classLink}");
            }
            catch (Exception)
            {
                // ne prekidamo login ako aktivacija razreda ne uspije
            }
        }
    }
}
