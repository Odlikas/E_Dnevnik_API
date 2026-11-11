namespace E_Dnevnik_API.Models.ScrapeStudentProfile
{
    public class StudentProfileInfo
    {
        public string? StudentSchool { get; set; }
        public string? StudentSchoolCity { get; set; }
        public string? StudentSchoolYear { get; set; }
        public string? StudentGrade { get; set; }

        // razred točno kako ga e-Dnevnik piše ("4.E-4"); StudentGrade je očišćen ("4E4")
        public string? StudentClassRaw { get; set; }
        public string? StudentName { get; set; }
        public string? ClassMaster { get; set; }
        public string? StudentProgram { get; set; }

        // "Ime i prezime" roditelja/skrbnika (redci nakon "Program"), najviše dva, redom s e-Dnevnika
        public List<string> ParentNames { get; set; } = new();
    }
}
