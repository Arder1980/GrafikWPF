// FILE: GrafikWPF/RunLogger.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GrafikWPF
{
    public enum LogMode { Info, Debug }

    public static class RunLogger
    {
        private static readonly object Gate = new();
        private static StreamWriter? _w;
        private static bool _active;
        private static bool _enabled = true;
        private static LogMode _mode = LogMode.Info;
        private static string _dir = "";
        private static string _solver = "XX";
        private static GrafikWejsciowy? _data;
        private static IReadOnlyList<SolverPriority>? _prio;
        private static DateTime _t0;

        public static void Configure(bool enabled, LogMode mode, string? logsDirectory)
        {
            lock (Gate)
            {
                _enabled = enabled;
                _mode = mode;
                _dir = string.IsNullOrWhiteSpace(logsDirectory)
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")
                    : logsDirectory!;
                Directory.CreateDirectory(_dir);
            }
        }

        public static void Start(string solverAcronym, GrafikWejsciowy dane, IReadOnlyList<SolverPriority> priorytety, string? startNote = null)
        {
            lock (Gate)
            {
                if (_active || !_enabled) { _solver = solverAcronym; _data = dane; _prio = priorytety; return; }

                _solver = solverAcronym;
                _data = dane;
                _prio = priorytety;
                _t0 = DateTime.Now;

                var firstDay = dane.DniWMiesiacu.OrderBy(d => d).FirstOrDefault();
                var yearMonth = firstDay == default ? $"{DateTime.Now:yyyy_MM}" : $"{firstDay:yyyy_MM}";
                var fname = $"{yearMonth}_{_solver}_{_mode.ToString().ToUpperInvariant()}_{_t0:yyyyMMdd_HHmmss}.txt";
                var baseDir = string.IsNullOrWhiteSpace(_dir) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs") : _dir;
                Directory.CreateDirectory(baseDir);
                var path = Path.Combine(baseDir, fname);

                _w = new StreamWriter(path, false, new UTF8Encoding(false));
                _active = true;

                WriteLine("====================================================================");
                WriteLine($"Start: {_t0:yyyy-MM-dd HH:mm:ss}");
                WriteLine($"Solver: {_solver}");
                if (!string.IsNullOrWhiteSpace(startNote)) WriteLine(startNote);
                WriteLine("====================================================================");
                WriteLine();

                // układ priorytetów (1 linia, numerowane)
                if (_prio != null && _prio.Count > 0)
                {
                    var priolist = _prio.Select((p, i) => $"{i + 1}. {PriName(p)}");
                    WriteLine($"Układ priorytetów: {string.Join(", ", priolist)}");
                    WriteLine();
                }

                // === TABELA DEKLARACJI ===
                WriteDeclarationsTable();
                WriteLine();
                WriteLine("REZ = HARD. Naruszenie REZ/URL/DYZ traktujemy jako błąd krytyczny.");
                WriteLine("Tryb logowania: " + _mode.ToString().ToUpperInvariant());
                WriteLine("--------------------------------------------------------------------");
                Flush();
            }
        }

        // ---------- PUBLIC API (info/trace/stop) ----------
        public static void Info(string msg) => WriteTagged("INFO", msg, LogMode.Info);
        public static void Debug(string msg) { if (_mode == LogMode.Debug) WriteTagged("DEBUG", msg, LogMode.Debug); }
        public static void TraceTry(string msg) { if (_mode == LogMode.Debug) WriteTagged("TRY", msg, LogMode.Debug); }
        public static void TraceOk(string msg) { if (_mode == LogMode.Debug) WriteTagged("OK", msg, LogMode.Debug); }
        public static void TraceFail(string msg) { if (_mode == LogMode.Debug) WriteTagged("FAIL", msg, LogMode.Debug); }
        public static void TraceBacktrack(string m) { if (_mode == LogMode.Debug) WriteTagged("BACKTRACK", m, LogMode.Debug); }
        public static void TraceImprove(string m) { if (_mode == LogMode.Debug) WriteTagged("IMPROVE", m, LogMode.Debug); }
        public static void TracePruneTT(string m) { if (_mode == LogMode.Debug) WriteTagged("PRUNE_TT", m, LogMode.Debug); }

        public static void StopIfActive()
        {
            lock (Gate)
            {
                if (!_active) return;
                WriteLine("== StopIfActive ==");
                Close();
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                if (!_active) return;
                WriteLine();
                WriteLine("PODSUMOWANIE PRIORYTETÓW: (brak – solver nie podał metryk końcowych)");
                WriteLine();
                Close();
            }
        }

        public static void Stop(RozwiazanyGrafik wynik)
        {
            lock (Gate)
            {
                if (!_active) return;

                // === UTWORZONY GRAFIK (pełny) ===
                if (wynik?.Przypisania != null && wynik.Przypisania.Count > 0)
                {
                    WriteLine();
                    WriteLine("UTWORZONY GRAFIK:");
                    foreach (var d in wynik.Przypisania.Keys.OrderBy(d => d))
                    {
                        var doc = wynik.Przypisania[d];
                        var sym = doc?.Symbol ?? "---";
                        WriteLine($"{d:dd.MM.yyyy} - {sym}");
                    }

                    // Zestawienie per lekarz
                    WriteLine();
                    WriteLine("ZESTAWIENIE DYŻURÓW (per lekarz):");
                    var activeDocs = _data!.Lekarze.Where(l => l.IsAktywny).OrderBy(l => l.Symbol).ToList();
                    foreach (var l in activeDocs)
                    {
                        var myDays = wynik.Przypisania
                            .Where(kv => kv.Value?.Symbol == l.Symbol)
                            .Select(kv => kv.Key)
                            .OrderBy(d => d)
                            .ToList();

                        string daysStr = myDays.Count == 0
                            ? "-"
                            : string.Join(", ", myDays.Select(d => d.Day.ToString("00")));

                        var limit = _data!.LimityDyzurow.GetValueOrDefault(l.Symbol, 0);
                        WriteLine($"{l.Symbol}: {daysStr} ({myDays.Count} / {limit})");
                    }
                }

                // === PODSUMOWANIE PRIORYTETÓW ===
                WriteLine();
                WriteLine("PODSUMOWANIE PRIORYTETÓW:");

                // Odczyt standardowych metryk z wyniku
                WriteLine($"1) Ciągłość początkowa: {wynik.DlugoscCiaguPoczatkowego} dni");
                WriteLine($"2) Obsada: {wynik.LiczbaDniObsadzonych} dni");
                WriteLine($"3) Wskaźnik Sprawiedliwości - σ obciążeń: {wynik.WskaznikRownomiernosci:F6}  (im mniej, tym lepiej)");
                WriteLine($"4) Wskaźnik Równomierności - rozrzut w miesiącu: {wynik.WskaznikRozlozeniaDyzurow:F6}  (im mniej, tym lepiej)");

                // Zgodność z ważnością deklaracji (BCH > CHC > MOG > WAR); REZ/URL/DYZ pomijamy w ocenie
                double zgodnosc = ObliczZgodnoscWaznosciDeklaracji(wynik);
                WriteLine($"5) Zgodność z ważnością deklaracji: {zgodnosc:F6}  (im wyższa, tym lepiej)");

                WriteLine();
                Close();
            }
        }

        // ---------- TABELA DEKLARACJI (nagłówek logu) ----------
        private static void WriteDeclarationsTable()
        {
            if (_data == null) return;

            var days = _data.DniWMiesiacu.OrderBy(d => d).ToList();
            var docs = _data.Lekarze.Where(l => l.IsAktywny).OrderBy(l => l.Symbol).ToList();
            if (docs.Count == 0 || days.Count == 0) { WriteLine("(brak danych)"); return; }

            // szerokość kolumn: >=5, >= len(symbol)+2
            var colW = docs.Select(d => Math.Max(5, d.Symbol.Length + 2)).ToArray();
            int leftW = 12;

            // separator
            string sep = new string('=', leftW + 1 + colW.Sum(w => w + 3) - 1); // przybliżony, „ładny” pasek

            // wiersz 1: symbole lekarzy
            var sb = new StringBuilder();
            sb.Append(new string(' ', leftW));
            sb.Append(" |");
            for (int i = 0; i < docs.Count; i++)
                sb.Append(' ').Append(Center(docs[i].Symbol, colW[i])).Append(" |");
            WriteLine(sb.ToString());
            WriteLine(sep);

            // wiersz 2: "Data/Limit:" + limity
            sb.Clear();
            sb.Append(Center("Data/Limit:", leftW));
            sb.Append(" |");
            for (int i = 0; i < docs.Count; i++)
            {
                var lim = _data.LimityDyzurow.GetValueOrDefault(docs[i].Symbol, 0);
                sb.Append(' ').Append(Center(lim.ToString(CultureInfo.InvariantCulture), colW[i])).Append(" |");
            }
            WriteLine(sb.ToString());
            WriteLine(sep);

            // wiersze dni
            foreach (var day in days)
            {
                sb.Clear();
                sb.Append(day.ToString("dd.MM.yyyy")).Append(new string(' ', Math.Max(0, leftW - 10)));
                sb.Append(" |");
                for (int i = 0; i < docs.Count; i++)
                {
                    var sym = docs[i].Symbol;
                    var td = TypDostepnosci.Niedostepny;
                    if (_data.Dostepnosc.TryGetValue(day, out var map) && map != null)
                        map.TryGetValue(sym, out td);

                    var code = CodeOf(td);
                    sb.Append(' ').Append(Center(code, colW[i])).Append(" |");
                }
                WriteLine(sb.ToString());
            }
        }

        // ---------- KODY / METRYKI POMOCNICZE ----------
        private static string CodeOf(TypDostepnosci t) => t switch
        {
            TypDostepnosci.Niedostepny => "---",
            TypDostepnosci.MogeWarunkowo => "WAR",
            TypDostepnosci.Moge => "MOG",
            TypDostepnosci.Chce => "CHC",
            TypDostepnosci.BardzoChce => "BCH",
            TypDostepnosci.Rezerwacja => "REZ",
            TypDostepnosci.DyzurInny => "DYZ",
            TypDostepnosci.Urlop => "URL",
            _ => "---"
        };

        private static string PriName(SolverPriority p) => p switch
        {
            SolverPriority.CiagloscPoczatkowa => "Ciągłość obsady",
            SolverPriority.LacznaLiczbaObsadzonychDni => "Obsada (łączna)",
            SolverPriority.SprawiedliwoscObciazenia => "Sprawiedliwość (σ obciążeń)",
            SolverPriority.RownomiernoscRozlozenia => "Równomierność (czasowa)",
            SolverPriority.ZgodnoscWaznosciDeklaracji => "Zgodność z ważnością deklaracji",
            _ => p.ToString()
        };

        // Normalizowana [0..1]: liczymy tylko miękkie deklaracje (BCH/CHC/MOG/WAR),
        // REZ/URL/DYZ pomijamy w ocenie. 1.0 = same BCH.
        private static double ObliczZgodnoscWaznosciDeklaracji(RozwiazanyGrafik wynik)
        {
            if (_data == null || wynik?.Przypisania == null || wynik.Przypisania.Count == 0) return 0.0;
            double sum = 0;
            int cnt = 0;

            foreach (var kv in wynik.Przypisania)
            {
                var date = kv.Key;
                var doc = kv.Value;
                if (doc == null) continue;

                if (!_data.Dostepnosc.TryGetValue(date, out var map) || map == null) continue;
                if (!map.TryGetValue(doc.Symbol, out var td)) continue;

                int w = td switch
                {
                    TypDostepnosci.BardzoChce => 4,
                    TypDostepnosci.Chce => 3,
                    TypDostepnosci.Moge => 2,
                    TypDostepnosci.MogeWarunkowo => 1,
                    _ => 0 // REZ/URL/DYZ/--- nie liczymy do zgodności
                };
                if (w > 0) { sum += w; cnt++; }
            }

            if (cnt == 0) return 0.0;
            return sum / (4.0 * cnt);
        }

        private static string Center(string s, int w)
        {
            if (s == null) s = "";
            if (s.Length >= w) return s;
            int pad = w - s.Length;
            int left = pad / 2;
            int right = pad - left;
            return new string(' ', left) + s + new string(' ', right);
        }

        // ---------- IO ----------
        private static void WriteTagged(string tag, string msg, LogMode lvl)
        {
            lock (Gate)
            {
                if (!_active || !_enabled) return;
                var ts = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                _w!.WriteLine($"[{ts}] [{_solver}] [{tag}] {msg}");
                Flush();
            }
        }
        private static void WriteLine(string s = "")
        {
            lock (Gate)
            {
                if (!_active || !_enabled) return;
                _w!.WriteLine(s);
            }
        }
        private static void Flush() { try { _w?.Flush(); } catch { } }
        private static void Close()
        {
            var t1 = DateTime.Now;
            WriteLine();
            WriteLine($"Stop: {t1:yyyy-MM-dd HH:mm:ss}  (czas: {(t1 - _t0).TotalSeconds:F3}s)");
            WriteLine("====================================================================");
            try { _w!.Flush(); _w!.Dispose(); } catch { }
            _w = null; _active = false;
        }
    }
}
