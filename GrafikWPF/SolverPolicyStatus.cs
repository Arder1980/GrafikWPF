#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace GrafikWPF
{
    /// <summary>
    /// ETAP 0 (diagnostyka): wspólny nagłówek statusu polityk solvera oraz miękkie asercje.
    /// Ten plik NIE zmienia logiki – tylko loguje i ostrzega, jeśli konfiguracja wygląda podejrzanie.
    /// </summary>
    internal static class SolverPolicyStatus
    {
        /// <summary>
        /// Wypisuje do loga jeden, czytelny nagłówek bieżących priorytetów i polityk.
        /// </summary>
        /// <param name="solverName">Nazwa silnika (np. "Backtracking").</param>
        /// <param name="priorities">Kolejność priorytetów z UI (dynamiczna!).</param>
        /// <param name="chProtectEnabled">Czy aktywne jest jakiekolwiek „rezerwowanie” CH/BC (powinno być OFF w tej fazie).</param>
        /// <param name="bcBreaksAdjacent">Czy BC może łamać zakazy sąsiedztwa (dzień-po-dniu i „Inny dyżur ±1”).</param>
        /// <param name="mwMax">Maksymalna liczba MW (Mogę warunkowo) na lekarza – oczekujemy 1.</param>
        /// <param name="rhK">Zakres rolling horizon K: (min,max) – np. (2,7).</param>
        /// <param name="lrBack">Zakres cofki LocalRepair: (min,max) – np. (5,14).</param>
        /// <param name="lrFwd">Zakres „w przód” LocalRepair: (min,max) – np. (4,6).</param>
        public static void LogStartupHeader(
            string solverName,
            IReadOnlyList<SolverPriority> priorities,
            bool chProtectEnabled,
            bool bcBreaksAdjacent,
            int mwMax,
            (int min, int max) rhK,
            (int min, int max) lrBack,
            (int min, int max) lrFwd)
        {
            // 1) Priorytety w kolejności z UI
            var priOrder = priorities is null || priorities.Count == 0
                ? "(brak)"
                : string.Join(" > ", priorities.Select(GetPriorityLabel));

            // 2) Zbiorczy nagłówek polityk (jedna linia, łatwa do odnalezienia w logu)
            SolverDiagnostics.Log($"[Policy] Solver={solverName}; Priorities=[{priOrder}]; " +
                                  $"CHProtect={(chProtectEnabled ? "ON" : "OFF")}; " +
                                  $"BC_breaks_adjacent={(bcBreaksAdjacent ? "TRUE" : "FALSE")}; " +
                                  $"MW_max={mwMax}; RH_K={rhK.min}..{rhK.max}; LocalRepair back={lrBack.min}..{lrBack.max} fwd={lrFwd.min}..{lrFwd.max}");

            // 3) Miękkie asercje – logują ostrzeżenia, nic nie wyłączają
            SoftAssert(priorities != null, "[ASSERT] priorities == null – brak kolejności priorytetów z UI.");
            SoftAssert(mwMax == 1, "[ASSERT] MW_max != 1 – oczekiwano limitu 1 na lekarza.");
            SoftAssert(!chProtectEnabled, "[ASSERT] CHProtect powinno być OFF (bez twardego rezerwowania CH/BC).");
            SoftAssert(bcBreaksAdjacent, "[ASSERT] BC_breaks_adjacent == FALSE – BC powinno móc łamać sąsiedztwo ±1.");
            SoftAssert(rhK.min >= 1 && rhK.max >= rhK.min,
                       "[ASSERT] Rolling Horizon K ma niepoprawny zakres.");
            SoftAssert(lrBack.min >= 0 && lrBack.max >= lrBack.min,
                       "[ASSERT] LocalRepair back ma niepoprawny zakres.");
            SoftAssert(lrFwd.min >= 0 && lrFwd.max >= lrFwd.min,
                       "[ASSERT] LocalRepair fwd ma niepoprawny zakres.");
        }

        /// <summary>
        /// Dodatkowa „miękka asercja” wykonywana po zakończeniu generowania:
        /// można tu zliczyć np. przekroczenia MW (jeśli solver to loguje/udostępnia dane).
        /// Na ETAP 0: opcjonalne (bezpieczne NO-OP, jeśli nie mamy danych).
        /// </summary>
        public static void LogPostRunSummary(
            string solverName,
            int totalDays,
            int filledDays,
            int prefixLen,
            int emptyDays)
        {
            SolverDiagnostics.Log($"[Policy] PostRun: Solver={solverName}; " +
                                  $"Filled={filledDays}/{totalDays}; Prefix={prefixLen}; Empty={emptyDays}");
        }

        private static void SoftAssert(bool condition, string messageIfFail)
        {
            if (!condition)
                SolverDiagnostics.Log($"WARN {messageIfFail}");
        }

        private static string GetPriorityLabel(SolverPriority p) => p switch
        {
            SolverPriority.CiagloscPoczatkowa => "Ciągłość od początku",
            SolverPriority.LacznaLiczbaObsadzonychDni => "Maks. obsada",
            SolverPriority.SprawiedliwoscObciazenia => "Sprawiedliwość (∝ limitom)",
            SolverPriority.RownomiernoscRozlozenia => "Równomierność",
            _ => p.ToString()
        };
    }
}
