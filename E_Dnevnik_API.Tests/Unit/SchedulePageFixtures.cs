namespace E_Dnevnik_API.Tests.Unit;

// stranice https://ocjene.skole.hr/schedule
public static class SchedulePageFixtures
{
    // SINTETIČKA: testni račun nema upisan raspored, pa prave stranice s rasporedom nemamo.
    // Struktura je ona koju čita stari ScheduleTableScraperService (flex-table-schedule po danu s
    // data-action-id, retci "row", predmet u prvoj ćeliji bez "no-box"); broj sata i vrijeme u
    // no-box ćelijama su pretpostavka. PON: 6 sati s vremenima, 4. sat prazan, 2. sat grupe;
    // UTO: bez broja i vremena (redni broj retka); SRI: nepoznat predmet i sat razrednika.
    public const string Week = """
        <html><body>
        <div class="content-wrapper"><div class="content">
        <div class="flex-table-schedule" data-action-id="PON">
          <div class="row header"><div class="cell no-box">Sat</div><div class="cell no-box">Vrijeme</div><div class="cell no-box">PON</div></div>
          <div class="row"><div class="cell no-box">1.</div><div class="cell no-box">08:00 - 08:45</div><div class="cell"><span>Matematika</span></div></div>
          <div class="row"><div class="cell no-box">2.</div><div class="cell no-box">08:50 - 09:35</div><div class="cell"><span>Engleski jezik, Njemački jezik</span></div></div>
          <div class="row"><div class="cell no-box">3.</div><div class="cell no-box">09:45 - 10:30</div><div class="cell"><span>Hrvatski&nbsp;jezik</span></div></div>
          <div class="row"><div class="cell no-box">4.</div><div class="cell no-box">10:40 - 11:25</div><div class="cell"></div></div>
          <div class="row"><div class="cell no-box">5.</div><div class="cell no-box">11:30 - 12:15</div><div class="cell">
              <span>  Fizika </span>
            </div></div>
          <div class="row"><div class="cell no-box">6.</div><div class="cell no-box">12.20-13.05</div><div class="cell"><span>Matematika</span></div></div>
        </div>
        <div class="flex-table-schedule" data-action-id="UTO">
          <div class="row header"><div class="cell no-box">UTO</div></div>
          <div class="row"><div class="cell"><span>Fizika</span></div></div>
          <div class="row"><div class="cell"><span>Matematika</span></div></div>
        </div>
        <div class="flex-table-schedule" data-action-id="SRI">
          <div class="row header"><div class="cell no-box">SRI</div></div>
          <div class="row"><div class="cell no-box">1.</div><div class="cell no-box">14:00 - 14:45</div><div class="cell"><span>Politika i gospodarstvo</span></div></div>
          <div class="row"><div class="cell no-box">2.</div><div class="cell no-box">14:50 - 15:35</div><div class="cell"><span>Sat razrednika</span></div></div>
        </div>
        <div class="flex-table-schedule" data-action-id="ČET">
          <div class="row header"><div class="cell no-box">ČET</div></div>
        </div>
        </div></div>
        </body></html>
        """;

    // PRAVA stranica testnog računa (2026-11-08), bez zaglavlja s imenima
    public const string NoTimetable = """
        <html><body>
        <div id="page-wrapper">
        <div class="content-wrapper">
        <div class="content no-records">
        <div class="section-text no-title">Učenik nema upisan raspored.</div>
        </div>
        </div>
        </div>
        </body></html>
        """;

    // npr. stranica greške ili promijenjena struktura
    public const string Unknown = """
        <html><body><div class="content"><p>Došlo je do pogreške.</p></div></body></html>
        """;
}
