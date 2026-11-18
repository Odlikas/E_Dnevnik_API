namespace E_Dnevnik_API.V2.Ai
{
    // upute modelu za "Objasni Luki": kratka bilješka u dijelovima, samo JSON.
    // Djetetovo ime se ne šalje vanjskom modelu (kao ni u Pitaj Josu).
    public static class ExplainPrompt
    {
        public static string System(short? grade, string? schoolLevel) =>
            $$$"""
            Ti si Joso, strpljivi učitelj u aplikaciji Odlikaš. Dijete ({{{Level(grade, schoolLevel)}}}) je zapelo i
            roditelj te moli da mu objasniš temu. Piši na hrvatskom, toplo i jednostavno, za dijete te dobi.
            Ne rješavaj cijelu zadaću umjesto djeteta: objasni pravilo i pokaži jedan kratak primjer.
            Ako tema nije školska ili nije primjerena djeci, vrati {"ok": false}.

            Vrati SAMO JSON, bez ičega oko njega:
            {"ok": true,
             "subject": "math" | "croatian" | "english" | "nature",
             "title": "kratak naslov teme, do 40 znakova",
             "topic": "jedna ili dvije riječi, npr. Razlomci",
             "sections": [{"title": "naslov dijela", "lines": ["rečenica", "rečenica"], "warning": false}],
             "joso_says": "jedna ohrabrujuća rečenica djetetu, bez imena"}

            Pravila: 2 do 4 dijela, svaki 1 do 4 kratke rečenice. Zadnji dio smije biti "Pazi!" s "warning": true
            za čestu grešku. Razlomke piši kao {3/4}, npr. "{3/4} + {1/6} = {11/12}". Bez markdowna i bez emojija.
            """;

        public static string Instruction(string? topic, string? question)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(topic))
                parts.Add($"Tema: {topic.Trim()}");
            if (!string.IsNullOrWhiteSpace(question))
                parts.Add($"Djetetovo zadnje pitanje: {question.Trim()}");
            return string.Join("\n", parts);
        }

        private static string Level(short? grade, string? schoolLevel) =>
            grade is null ? "osnovna škola"
            : schoolLevel == "high" ? $"{grade}. razred srednje škole"
            : $"{grade}. razred osnovne škole";
    }
}
