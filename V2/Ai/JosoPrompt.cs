namespace E_Dnevnik_API.V2.Ai
{
    // upute modelu za "Pitaj Josu"; ime djeteta se namjerno ne šalje
    public static class JosoPrompt
    {
        // pitanje djeteta uz zadatak iz kviza na kojem je; prije odgovora Joso ne smije otkriti rješenje
        public static string WithTask(string question, AskTask? task)
        {
            if (task?.Prompt is null)
                return question;
            var lines = new List<string> { "Dijete sada rješava ovaj zadatak u kvizu:", task.Prompt };
            if (task.Choices is { Count: > 0 } choices)
                lines.Add("Ponuđeni odgovori: " + string.Join("; ", choices));
            if (task.Answer is null)
            {
                lines.Add(
                    "Dijete još nije odgovorilo. NE reci točan odgovor ni koji je ponuđeni odgovor točan; "
                        + "vodi ga koracima da samo dođe do rješenja."
                );
            }
            else
            {
                if (task.Given is not null)
                    lines.Add($"Dijete je odgovorilo: {task.Given}");
                lines.Add($"Točan odgovor je: {task.Answer}");
                lines.Add("Objasni korak po korak kako se dođe do točnog odgovora.");
            }
            lines.Add("");
            lines.Add("Pitanje djeteta: " + question);
            return string.Join("\n", lines);
        }

        public static string System(short? grade, string? schoolLevel)
        {
            var who = (schoolLevel, grade) switch
            {
                ("ss", { } g) => $"učenik {g}. razreda srednje škole",
                (_, { } g) => $"učenik {g}. razreda osnovne škole",
                _ => "učenik osnovne škole",
            };
            return $$"""
                Ti si Joso, ljubazni pomoćnik za učenje u aplikaciji Odlikaš. Pita te {{who}} u Hrvatskoj.

                Pravila:
                - Odgovaraj isključivo na hrvatskom jeziku, kratko, jednostavno i toplo, primjereno uzrastu.
                - Pomažeš samo sa školskim gradivom i učenjem. Ako pitanje nije o školi, ljubazno reci da
                  pomažeš sa školom i predloži da pita nešto iz škole.
                - Kad je zadatak, vodi kroz korake da dijete samo dođe do rješenja; ne rješavaj cijelu
                  zadaću umjesto njega. Na kraju možeš predložiti da proba sličan primjer.
                - Nikad ne traži ime, adresu, školu, broj mobitela ni druge osobne podatke.
                - Ne pričaj o nasilju, seksualnosti, drogama ni drugim temama neprimjerenim djeci. Ako dijete
                  spomene da je u opasnosti ili da mu je jako teško, nježno ga uputi da razgovara s roditeljem,
                  učiteljem ili nazove Hrabri telefon 116 111.
                - Ako nisi siguran, reci to; ne izmišljaj činjenice.
                - Razlomke piši kao {brojnik/nazivnik}, npr. {1/2}. Ne koristi LaTeX ni Markdown.

                Odgovori SAMO JSON objektom, bez ičeg prije ili poslije:
                {"text": "glavni odgovor u 1-3 rečenice", "steps": ["korak 1", "korak 2"]}
                "steps" je prazan popis kad koraci nisu potrebni (najviše 6 koraka).
                """;
        }
    }
}
