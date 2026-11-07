namespace E_Dnevnik_API.Tests.Unit;

// sintetičke stranice https://ocjene.skole.hr/grade/{id}, struktura kao na pravoj stranici
public static class GradePageFixtures
{
    // 3 elementa, 4 ocjene; 890289939970 ima class "row negative", 777000000003 "row positive"
    // i bilješku s HTML entitetima, 555000000004 nema bilješku
    public const string Subject = """
        <html><body>
        <div class="flex-table s  grades-table " data-schoolyear=2026  aria-label="GradesTable">
          <div class="row header first"><div class="cell">Elementi vrednovanja</div></div>
          <div class="row header"><div class="cell first"></div><div class="cell">IX</div><div class="cell">X</div><div class="cell">XI</div><div class="cell">XII</div><div class="cell">I</div><div class="cell">II</div><div class="cell">III</div><div class="cell">IV</div><div class="cell">V</div><div class="cell">VI</div></div>
          <div class="row">
            <div class="cell first"><span>funkcionalne sposobnosti</span></div>
            <div class="cell grade">   </div>
            <div class="cell grade">
              <span class="popup mobile-only" data-title="0012 funkcionalne sposobnosti">
                <span class=" positive " data-action-id="883595728650">3</span>
              </span>
            </div>
            <div class="cell grade"></div><div class="cell grade"></div><div class="cell grade"></div>
            <div class="cell grade"></div><div class="cell grade"></div><div class="cell grade"></div>
            <div class="cell grade"></div><div class="cell grade"></div>
          </div>
          <div class="row">
            <div class="cell first"><span>  motorička
                 znanja </span></div>
            <div class="cell grade"></div><div class="cell grade"></div>
            <div class="cell grade">
              <span class="popup mobile-only" data-title="motorička znanja">
                <span class=" positive " data-action-id="555000000004">4</span>
              </span>
            </div>
            <div class="cell grade"></div>
            <div class="cell grade">
              <span class="popup mobile-only" data-title="motorička znanja">
                <span class=" positive " data-action-id="777000000003">5</span>
              </span>
            </div>
            <div class="cell grade"></div><div class="cell grade"></div><div class="cell grade"></div>
            <div class="cell grade"></div><div class="cell grade"></div>
          </div>
          <div class="row">
            <div class="cell first"><span>odgojni ishodi</span></div>
            <div class="cell grade"></div>
            <div class="cell grade">
              <span class="popup mobile-only" data-title="odgojni ishodi">
                <span class=" negative " data-action-id="890289939970">1</span>
              </span>
            </div>
            <div class="cell grade"></div><div class="cell grade"></div><div class="cell grade"></div>
            <div class="cell grade"></div><div class="cell grade"></div><div class="cell grade"></div>
            <div class="cell grade"></div><div class="cell grade"></div>
          </div>
          <div class="row final-grade "><div class="cell first"><span>ZAKLJUČENO</span></div></div>
        </div>

        <div class="flex-table">
          <div class="row header"><div class="cell">Datum</div><div class="cell">Ocjena</div><div class="cell">Bilješka</div></div>
          <div class="row positive " data-action-id="777000000003">
            <div data-date="12.1.2027." class="cell"> <span>12.1.</span></div>
            <div class="box">
              <div class="cell"><span> 5</span></div>
              <div class="cell"> <pre class="note-content">Skok &amp; doskok &quot;u dalj&quot;</pre> </div>
            </div>
            <div class="cell"> </div>
          </div>
          <div class="row " data-action-id="555000000004">
            <div data-date="3.11.2026." class="cell"> <span>3.11.</span></div>
            <div class="box">
              <div class="cell"><span> 4</span></div>
              <div class="cell">  </div>
            </div>
            <div class="cell"> </div>
          </div>
          <div class="row negative " data-action-id="890289939970">
            <div data-date="28.10.2026." class="cell"> <span>28.10.</span></div>
            <div class="box">
              <div class="cell"><span> 1</span></div>
              <div class="cell"> <pre class="note-content">nije radila bez opravdanja</pre> </div>
            </div>
            <div class="cell"> </div>
          </div>
          <div class="row " data-action-id="883595728650">
            <div data-date="7.10.2026." class="cell"> <span>7.10.</span></div>
            <div class="box">
              <div class="cell"><span> 3</span></div>
              <div class="cell"> <pre class="note-content">Beep test</pre> </div>
            </div>
            <div class="cell"> </div>
          </div>
        </div>
        </body></html>
        """;

    // predmet bez ocjena: prazne ćelije, nema datiranih redaka
    public const string Empty = """
        <html><body>
        <div class="flex-table s  grades-table " data-schoolyear=2026  aria-label="GradesTable">
          <div class="row header"><div class="cell first"></div><div class="cell">IX</div><div class="cell">X</div></div>
          <div class="row">
            <div class="cell first"><span>usvojenost znanja</span></div>
            <div class="cell grade">   </div><div class="cell grade">   </div>
          </div>
        </div>
        </body></html>
        """;

    // data-date bez godine; action id nije u tablici elemenata; jedan redak s ne-brojčanom vrijednošću
    public const string NoYear = """
        <html><body>
        <div class="flex-table">
          <div class="row " data-action-id="444000000001">
            <div data-date="15.1." class="cell"> <span>15.1.</span></div>
            <div class="box">
              <div class="cell"><span> 2</span></div>
              <div class="cell"> <pre class="note-content"> </pre> </div>
            </div>
          </div>
          <div class="row " data-action-id="444000000002">
            <div data-date="20.9." class="cell"> <span>20.9.</span></div>
            <div class="box">
              <div class="cell"><span>+</span></div>
              <div class="cell"></div>
            </div>
          </div>
        </div>
        </body></html>
        """;
}
