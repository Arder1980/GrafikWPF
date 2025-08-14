#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace GrafikWPF
{
    /// <summary>
    /// Bardzo prosty i bezpieczny logger do pliku na potrzeby diagnozy solverów.
    /// Domyślna lokalizacja: Dokumenty\Grafikomat_logs\log_YYYYMMDD_HHMMSS.txt
    /// Użycie (w kodzie solvera lub UI):
    ///     SolverDiagnostics.Enabled = true;
    ///     SolverDiagnostics.Start();            // lub Start("C:\\...\\plik.txt")
    ///     SolverDiagnostics.Log("Tekst...");
    ///     SolverDiagnostics.LogBlock("Tytuł", listaLinii);
    ///     SolverDiagnostics.Stop();
    /// </summary>
    public static class SolverDiagnostics
    {
        private static readonly object _gate = new();
        private static StreamWriter? _writer;
        private static bool _started;

        /// <summary>Czy logowanie jest włączone logicznie (globalny przełącznik).</summary>
        public static bool Enabled { get; set; } = false;

        /// <summary>Pełna ścieżka do bieżącego pliku z logiem (jeśli działa).</summary>
        public static string? CurrentLogPath { get; private set; }

        /// <summary>Czy logger faktycznie zapisuje (Enabled && Start wykonane bez błędu).</summary>
        public static bool IsActive
        {
            get { lock (_gate) return Enabled && _started && _writer != null; }
        }

        /// <summary>Rozpoczęcie sesji logowania. Gdy path==null, tworzy plik w Dokumenty\Grafikomat_logs\log_YYYYMMDD_HHMMSS.txt</summary>
        public static void Start(string? path = null)
        {
            if (!Enabled) return;

            lock (_gate)
            {
                try
                {
                    if (_writer != null) return; // już działa

                    if (string.IsNullOrWhiteSpace(path))
                    {
                        string dir = GetDefaultLogDirectory();
                        Directory.CreateDirectory(dir);
                        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        path = Path.Combine(dir, $"log_{stamp}.txt");
                    }
                    else
                    {
                        string? dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    }

                    // Ustawienia pliku: dopisywanie = false (zawsze nowy plik), UTF-8, AutoFlush.
                    _writer = new StreamWriter(new FileStream(path!, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        AutoFlush = true,
                        NewLine = Environment.NewLine
                    };
                    _started = true;
                    CurrentLogPath = path;

                    // Nagłówek pliku
                    _writer.WriteLine($"==== SolverDiagnostics START {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ====");
                    _writer.WriteLine($"Machine: {Environment.MachineName}, User: {Environment.UserName}");
                    _writer.WriteLine($"Process: {Process.GetCurrentProcess().ProcessName} (PID {Environment.ProcessId})");
                    _writer.WriteLine(new string('=', 72));
                }
                catch
                {
                    // W razie problemu nie wywracamy programu – po prostu nie logujemy.
                    _writer = null;
                    _started = false;
                    CurrentLogPath = null;
                }
            }
        }

        /// <summary>Zakończenie sesji logowania i zamknięcie pliku.</summary>
        public static void Stop()
        {
            lock (_gate)
            {
                try
                {
                    if (_writer != null)
                    {
                        _writer.WriteLine(new string('=', 72));
                        _writer.WriteLine($"==== SolverDiagnostics STOP {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ====");
                        _writer.Flush();
                        _writer.Dispose();
                    }
                }
                catch { /* ignorujemy */ }
                finally
                {
                    _writer = null;
                    _started = false;
                }
            }
        }

        /// <summary>Prosta linia logu z bieżącym czasem. Ignoruje wywołanie, gdy logger nieaktywny.</summary>
        public static void Log(string message)
        {
            if (!Enabled) return;
            lock (_gate)
            {
                if (_writer == null) return;
                try
                {
                    string ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    _writer.WriteLine($"[{ts}] {message}");
                }
                catch { /* ignorujemy – log nie może psuć działania aplikacji */ }
            }
        }

        /// <summary>Blok tekstu z nagłówkiem i listą linii (ładniejsze zrzuty zestawień).</summary>
        public static void LogBlock(string title, IEnumerable<string> lines)
        {
            if (!Enabled) return;
            lock (_gate)
            {
                if (_writer == null) return;
                try
                {
                    string ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    _writer.WriteLine($"[{ts}] --- {title} ---");
                    foreach (var line in lines)
                        _writer.WriteLine(line);
                    _writer.WriteLine($"[{ts}] --- /{title} ---");
                }
                catch { }
            }
        }

        /// <summary>Para klucz-wartość (czytelne parametry, ustawienia, decyzje).</summary>
        public static void LogKeyValue(string key, string value)
        {
            Log($"{key}: {value}");
        }

        /// <summary>Logowanie wyjątku (z krótkim kontekstem).</summary>
        public static void LogException(Exception ex, string? context = null)
        {
            if (!Enabled) return;
            lock (_gate)
            {
                if (_writer == null) return;
                try
                {
                    string ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    _writer.WriteLine($"[{ts}] !! EXCEPTION {(context ?? "")}".Trim());
                    _writer.WriteLine(ex.GetType().FullName);
                    _writer.WriteLine(ex.Message);
                    _writer.WriteLine(ex.StackTrace);
                    if (ex.InnerException != null)
                    {
                        _writer.WriteLine("-- InnerException --");
                        _writer.WriteLine(ex.InnerException.GetType().FullName);
                        _writer.WriteLine(ex.InnerException.Message);
                        _writer.WriteLine(ex.InnerException.StackTrace);
                    }
                    _writer.WriteLine(new string('-', 56));
                }
                catch { }
            }
        }

        /// <summary>Pomocnicza metoda: domyślny katalog logów w Dokumentach.</summary>
        public static string GetDefaultLogDirectory()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(docs, "Grafikomat_logs");
        }

        /// <summary>
        /// Krótkie, wygodne formatowanie dla zestawień (np. „Dzień 2025-09-14: [Ala-Chcę, Ola-Mogę]”).
        /// </summary>
        public static string JoinInline(IEnumerable<string> items, string sep = ", ")
        {
            return string.Join(sep, items ?? Array.Empty<string>());
        }
    }
}
