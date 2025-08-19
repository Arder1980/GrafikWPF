// FILE: GrafikWPF/RunLogger.cs
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace GrafikWPF
{
    public enum LogMode { Info, Debug }

    /// <summary>
    /// Jednolity logger uruchomień solverów.
    /// - Nazwa pliku: YYYY_MM_{SolverAcronym}_{Info|Debug}_{yyyyMMdd_HHmmss}.txt
    /// - Nagłówek: tabela deklaracji (---/WAR/MOG/CHC/BCH/REZ/DYZ/URL) + wiersz „Data/Limit”
    /// - Treść: wpisy INFO/DEBUG/IMPROVE + na końcu „pełny grafik”, zestawienia per lekarz oraz „PODSUMOWANIE PRIORYTETÓW”
    /// - Korzysta z Deklaracje.cs (kody, pełne nazwy, wagi preferencji)
    /// </summary>
    public static class RunLogger
    {
        private static readonly object Gate = new();

        private static StreamWriter? _w;
        private static bool _active;
        private static bool _enabled = true;
        private static LogMode _mode = LogMode.Info;
        private static string _dir = string.Empty;

        private static string _solver = "?";
        private static GrafikWejsciowy? _in;
        private static IReadOnlyList<SolverPriority> _prio = Array.Empty<SolverPriority>();
        private static DateTime _startedAtUtc;
        private static string _filePath = string.Empty;

        // Ustawienia nagłówka tabeli
        private const int DateColWidth = 12; // „dd.MM.yyyy”
        private const int ColWidthMin = 4;   // min. szerokość kolumny lekarza

        // ---------------- API ----------------

        /// <summary>Konfiguracja logowania (wywołaj przy starcie aplikacji i/lub po zapisaniu ustawień w oknie Ustawienia).</summary>
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

        /// <summary>Wewnętrzny przełącznik – czy wolno wypisywać cięższe logi.</summary>
        public static bool IsDebug => _enabled && _active && _mode == LogMode.Debug;

        /// <summary>Start jednego przebiegu solvera (tworzy plik i pisze nagłówek z tabelą deklaracji).</summary>
        public static void Start(string solverAcronym, GrafikWejsciowy input, IReadOnlyList<SolverPriority> priorytety, string? startNote = null)
        {
            lock (Gate)
            {
                if (_active || !_enabled) { _solver = solverAcronym; _in = input; _prio = priorytety; return; }

                _solver = solverAcronym;
                _in = input;
                _prio = priorytety ?? Array.Empty<SolverPriority>();
                _startedAtUtc = DateTime.UtcNow;

                string ym = (_in.DniWMiesiacu.Count > 0 ? _in.DniWMiesiacu[0] : DateTime.Today).ToString("yyyy_MM", CultureInfo.InvariantCulture);
                string ts = _startedAtUtc.ToLocalTime().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string file = $"{ym}_{_solver}_{_mode.ToString().ToUpperInvariant()}_{ts}.txt";
                _filePath = Path.Combine(_dir, file);

                _w = new StreamWriter(new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                _active = true;

                // Nagłówek
                WriteLine($"Solver: {_solver}");
                WriteLine($"Start:  {_startedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                if (!string.IsNullOrWhiteSpace(startNote)) WriteLine(startNote);

                // Układ priorytetów w jednej linii
                if (_prio.Count > 0)
                {
                    var items = _prio.Select((p, i) => $"{i + 1}. {GetEnumDescription(p)}").ToList();
                    // dopisz Priorytet 5, jeśli nie ma go w liście użytkownika
                    if (!_prio.Contains(SolverPriority.ZgodnoscWaznosciDeklaracji))
                        items.Add("5. Zgodność z ważnością deklaracji");
                    WriteLine($"Układ priorytetów: {string.Join(", ", items)}");
                }

                WriteLine("");
                WriteDeklaracjeTable();
                WriteLine("");
            }
        }

        /// <summary>Wiadomość informacyjna (zawsze trafia do logu, jeśli logowanie włączone).</summary>
        public static void Info(string message)
        {
            lock (Gate)
            {
                if (!_active || !_enabled || _w is null) return;
                WriteLine(message);
            }
        }

        /// <summary>Wiadomość debug (tylko gdy tryb Debug).</summary>
        public static void Debug(string message)
        {
            lock (Gate)
            {
                if (!_active || !_enabled || _w is null) return;
                if (_mode != LogMode.Debug) return;
                WriteLine(message);
            }
        }

        /// <summary>Znacznik poprawy (np. lepszy „best-so-far”).</summary>
        public static void TraceImprove(string message)
        {
            Info($"IMPROVE: {message}");
        }

        // --- Nakładki kompatybilnościowe używane przez BacktrackingSolver ---
        public static void TraceTry() => Debug("[TRY]");
        public static void TraceTry(string message) => Debug($"[TRY] {message}");
        public static void TraceTry(string format, params object[] args) =>
            Debug("[TRY] " + string.Format(CultureInfo.InvariantCulture, format, args));

        public static void TraceOk() => Info("[OK]");
        public static void TraceOk(string message) => Info($"[OK] {message}");
        public static void TraceOk(string format, params object[] args) =>
            Info("[OK] " + string.Format(CultureInfo.InvariantCulture, format, args));

        public static void TraceFail() => Info("[FAIL]");
        public static void TraceFail(string message) => Info($"[FAIL] {message}");
        public static void TraceFail(string format, params object[] args) =>
            Info("[FAIL] " + string.Format(CultureInfo.InvariantCulture, format, args));
        // --------------------------------------------------------------------

        /// <summary>Finalizacja bieżącego przebiegu: zapisuje pełny grafik, zestawienia i podsumowanie priorytetów.</summary>
        public static void Stop(RozwiazanyGrafik wynik)
        {
            lock (Gate)
            {
                if (!_active || !_enabled || _w is null) return;

                try
                {
                    WriteLine("");
                    WriteLine("========== UTWORZONY GRAFIK ==========");
                    WritePelnyGrafik(wynik);

                    WriteLine("");
                    WriteLine("========== ZESTAWIENIE PER LEKARZ ==========");
                    WritePerLekarzSummary(wynik);

                    WriteLine("");
                    WriteLine("========== PODSUMOWANIE PRIORYTETÓW ==========");
                    WritePodsumowaniePriorytetow(wynik);

                    WriteLine("");
                    WriteLine($"Koniec: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                }
                finally
                {
                    _w.Flush();
                    _w.Dispose();
                    _w = null;
                    _active = false;
                    _in = null;
                    _prio = Array.Empty<SolverPriority>();
                }
            }
        }

        // ---------------- Implementacja szczegółów ----------------

        private static void WriteLine(string s)
        {
            _w!.WriteLine(s);
        }

        private static void WriteDeklaracjeTable()
        {
            if (_in is null) return;

            var dni = _in.DniWMiesiacu;
            var lekarze = _in.Lekarze.Where(l => l.IsAktywny).ToList();
            var sym = lekarze.Select(l => l.Symbol).ToList();
            int colW = Math.Max(ColWidthMin, sym.Max(s => s?.Length ?? 0));

            string Cell(string txt, int w) => " " + (txt ?? "").PadRight(w) + " ";
            string LCell(string txt) => (txt ?? "").PadRight(DateColWidth);

            // Nagłówek kolumn
            var header = new StringBuilder();
            header.Append(LCell("")); header.Append("|");
            foreach (var s in sym) { header.Append(Cell(s, colW)); header.Append("|"); }
            WriteLine(header.ToString());

            // Pasek '=' (dopasowany długością do nagłówka)
            WriteLine(new string('=', header.Length));

            // Wiersz „Data/Limit” z limitami
            var wLimit = new StringBuilder();
            wLimit.Append(LCell("Data/Limit:")); wLimit.Append("|");
            foreach (var l in lekarze)
            {
                int lim = _in.LimityDyzurow.TryGetValue(l.Symbol, out var v) ? v : 0;
                wLimit.Append(Cell(lim.ToString(CultureInfo.InvariantCulture), colW)); wLimit.Append("|");
            }
            WriteLine(wLimit.ToString());

            // Pasek '='
            WriteLine(new string('=', header.Length));

            // Każdy dzień: kody deklaracji w kolumnach
            foreach (var d in dni)
            {
                var row = new StringBuilder();
                row.Append(LCell(d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)));
                row.Append("|");
                foreach (var l in lekarze)
                {
                    var av = _in.Dostepnosc.TryGetValue(d, out var map) && map.TryGetValue(l.Symbol, out var t)
                        ? t : TypDostepnosci.Niedostepny;
                    row.Append(Cell(Deklaracje.Kod(av), colW)).Append("|");
                }
                WriteLine(row.ToString());
            }
        }

        private static void WritePelnyGrafik(RozwiazanyGrafik wynik)
        {
            if (_in is null) return;
            foreach (var d in _in.DniWMiesiacu)
            {
                _ = wynik.Przypisania.TryGetValue(d, out var lek);
                string who = lek?.Symbol ?? "---";
                WriteLine($"{d:dd.MM.yyyy} - {who}");
            }
        }

        private static void WritePerLekarzSummary(RozwiazanyGrafik wynik)
        {
            if (_in is null) return;

            // Przygotuj listy dat per lekarz
            var per = _in.Lekarze.Where(l => l.IsAktywny).ToDictionary(l => l.Symbol, _ => new List<DateTime>());
            foreach (var kv in wynik.Przypisania)
            {
                if (kv.Value != null && per.TryGetValue(kv.Value.Symbol, out var list))
                    list.Add(kv.Key);
            }

            // Wypisz
            foreach (var l in _in.Lekarze.Where(l => l.IsAktywny))
            {
                per.TryGetValue(l.Symbol, out var lista);
                lista ??= new List<DateTime>();
                lista.Sort();

                int lim = _in.LimityDyzurow.TryGetValue(l.Symbol, out var v) ? v : 0;

                string daty = lista.Count == 0
                    ? "-"
                    : string.Join(", ", lista.Select(d => d.Day.ToString("00", CultureInfo.InvariantCulture)));

                WriteLine($"{l.Symbol}: {daty} ({lista.Count} / {lim})");
            }
        }

        private static void WritePodsumowaniePriorytetow(RozwiazanyGrafik m)
        {
            // Kolejność raportowania = kolejność priorytetów ustawiona przez użytkownika
            foreach (var p in _prio)
            {
                switch (p)
                {
                    case SolverPriority.CiagloscPoczatkowa:
                        WriteLine($"Priorytet {IndexOf(_prio, p)} (Ciągłość początkowa): {m.DlugoscCiaguPoczatkowego} dni");
                        break;

                    case SolverPriority.LacznaLiczbaObsadzonychDni:
                        WriteLine($"Priorytet {IndexOf(_prio, p)} (Obsada): {m.LiczbaDniObsadzonych} dni");
                        break;

                    case SolverPriority.SprawiedliwoscObciazenia:
                        // teraz poprawne pole: WskaznikSprawiedliwosci
                        WriteLine($"Priorytet {IndexOf(_prio, p)} (Wskaźnik Sprawiedliwości - σ obciążeń): {m.WskaznikSprawiedliwosci:F6}  (im mniej, tym lepiej)");
                        break;

                    case SolverPriority.RownomiernoscRozlozenia:
                        // teraz poprawne pole: WskaznikRownomiernosci
                        WriteLine($"Priorytet {IndexOf(_prio, p)} (Wskaźnik Równomierności - rozrzut w miesiącu): {m.WskaznikRownomiernosci:F6}  (im mniej, tym lepiej)");
                        break;

                    case SolverPriority.ZgodnoscWaznosciDeklaracji:
                        double zgodnosc = ObliczZgodnoscWaznosciDeklaracji(m);
                        WriteLine($"Priorytet {IndexOf(_prio, p)} (Zgodność z ważnością deklaracji): {zgodnosc:F6}  (im wyższa, tym lepiej)");
                        break;
                }
            }
        }

        // Uwaga: IReadOnlyList<T> nie ma wbudowanego IndexOf – własna implementacja + 1-bazowe raportowanie
        private static int IndexOf(IReadOnlyList<SolverPriority> list, SolverPriority p)
        {
            int idx = IndexOfRO(list, p);
            return 1 + Math.Max(0, idx);
        }

        private static int IndexOfRO<T>(IReadOnlyList<T> list, T value)
        {
            var cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < list.Count; i++)
                if (cmp.Equals(list[i], value))
                    return i;
            return -1;
        }

        private static double ObliczZgodnoscWaznosciDeklaracji(RozwiazanyGrafik wynik)
        {
            // Definicja: średnia ważona preferencji / (max 3) znormalizowana do [0..1], liczona względem wszystkich dni w miesiącu.
            // Punktacja: BCH=3, CHC=2, MOG=1, WAR=0; REZ/URL/DYZ/--- są pomijane w liczniku.
            if (_in is null) return 0.0;

            double sum = 0.0;
            foreach (var d in _in.DniWMiesiacu)
            {
                if (!wynik.Przypisania.TryGetValue(d, out var lek) || lek is null) continue;
                if (!_in.Dostepnosc.TryGetValue(d, out var map) || !map.TryGetValue(lek.Symbol, out var t)) continue;

                int w = Deklaracje.WagaPreferencji(t);
                if (w > 0) sum += w; // tylko preferencje liczymy
            }

            double denom = 3.0 * Math.Max(1, _in.DniWMiesiacu.Count);
            return sum / denom;
        }

        // --- pomoc: opisy enumów ---
        private static string GetEnumDescription(Enum value)
        {
            var fi = value.GetType().GetField(value.ToString());
            if (fi != null)
            {
                var attr = fi.GetCustomAttributes(typeof(DescriptionAttribute), false).FirstOrDefault() as DescriptionAttribute;
                if (attr != null && !string.IsNullOrWhiteSpace(attr.Description)) return attr.Description!;
            }
            // fallback
            return value.ToString();
        }
    }
}
