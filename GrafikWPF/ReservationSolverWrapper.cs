// ----- FILE: GrafikWPF/ReservationSolverWrapper.cs -----
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GrafikWPF
{
    /// <summary>
    /// Wrapper: wycina dni z rezerwacjami, uruchamia solver na zredukowanym problemie,
    /// scala wyniki i naprawia konflikty „dzień po dniu” bez BC — najpierw szukając zamiennika.
    /// </summary>
    public class ReservationSolverWrapper : IGrafikSolver
    {
        private readonly GrafikWejsciowy _oryginalneDane;
        private readonly IGrafikSolver _wewnetrznySolver;
        private readonly CancellationToken _ct;

        // Rezerwacje: dzień -> lekarz (stałe; nie modyfikujemy tych przydziałów)
        private Dictionary<DateTime, Lekarz> _rezerwacje;

        // Oryginalne limity (potrzebne przy podmianach)
        private Dictionary<string, int> _limityOryginalne;

        public ReservationSolverWrapper(
            SolverType typSolvera,
            GrafikWejsciowy daneWejsciowe,
            List<SolverPriority> kolejnoscPriorytetow,
            IProgress<double>? progress,
            CancellationToken token)
        {
            _oryginalneDane = daneWejsciowe;
            _ct = token;

            var (rez, reduced) = PrzygotujProblemDlaSolvera(daneWejsciowe);
            _rezerwacje = rez;
            _limityOryginalne = _oryginalneDane.Lekarze
                .ToDictionary(l => l.Symbol, l => _oryginalneDane.LimityDyzurow.GetValueOrDefault(l.Symbol, 0));

            _wewnetrznySolver = SolverFactory.CreateSolver(typSolvera, reduced, kolejnoscPriorytetow, progress, token);
        }

        private (Dictionary<DateTime, Lekarz> rezerwacje, GrafikWejsciowy zredukowaneDane)
            PrzygotujProblemDlaSolvera(GrafikWejsciowy dane)
        {
            var rezerwacje = new Dictionary<DateTime, Lekarz>();
            var zredukowaneDni = new Dictionary<DateTime, Dictionary<string, TypDostepnosci>>();
            var zredukowaneLimity = new Dictionary<string, int>(dane.LimityDyzurow);

            // Zbierz rezerwacje i zredukuj limity
            foreach (var dzien in dane.DniWMiesiacu)
            {
                foreach (var (sym, typ) in dane.Dostepnosc[dzien])
                {
                    if (typ == TypDostepnosci.Rezerwacja)
                    {
                        var lek = dane.Lekarze.First(l => l.Symbol == sym);
                        rezerwacje[dzien] = lek;
                        if (zredukowaneLimity.ContainsKey(sym))
                            zredukowaneLimity[sym] = Math.Max(0, zredukowaneLimity[sym] - 1);
                    }
                }
            }

            // Walidacja: ten sam lekarz nie może mieć dwóch rezerwacji dzień-po-dniu
            foreach (var g in rezerwacje.GroupBy(k => k.Value.Symbol))
            {
                var list = g.OrderBy(k => k.Key).Select(k => k.Key).ToList();
                for (int i = 0; i + 1 < list.Count; i++)
                    if ((list[i + 1] - list[i]).TotalDays == 1)
                        throw new InvalidOperationException(
                            $"Błąd w danych wejściowych: Lekarz {g.Key} ma dwie rezerwacje dzień po dniu ({list[i]:dd.MM} i {list[i + 1]:dd.MM}).");
            }

            // Zredukowane dni (bez rezerwacji)
            foreach (var dzien in dane.DniWMiesiacu)
                if (!rezerwacje.ContainsKey(dzien))
                    zredukowaneDni[dzien] = dane.Dostepnosc[dzien];

            var reduced = new GrafikWejsciowy
            {
                Lekarze = dane.Lekarze,
                Dostepnosc = zredukowaneDni,
                LimityDyzurow = zredukowaneLimity
            };
            return (rezerwacje, reduced);
        }

        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            // 1) Rozwiąż z wyciętymi rezerwacjami
            var partial = _wewnetrznySolver.ZnajdzOptymalneRozwiazanie();

            // 2) Scal rezerwacje + wynik solv.
            var plan = new Dictionary<DateTime, Lekarz?>(
                _rezerwacje.ToDictionary(k => k.Key, k => (Lekarz?)k.Value));
            foreach (var kv in partial.Przypisania) plan[kv.Key] = kv.Value;

            // 3) Obciążenia (na pełnym planie po scaleniu)
            var oblozenie = _oryginalneDane.Lekarze.ToDictionary(l => l.Symbol, _ => 0);
            foreach (var l in plan.Values) if (l != null) oblozenie[l.Symbol]++;

            // 4) Napraw konflikty d+1 – NAJPIERW: spróbuj znaleźć zamiennika (fallback: wyzeruj)
            NaprawDzienPoDniu_Z_Zamiennikiem(plan, oblozenie);

            // 5) Metryki całości
            return EvaluationAndScoringService.CalculateMetrics(plan, oblozenie, _oryginalneDane);
        }

        // ========================= NAPRAWA KONFLIKTÓW =========================

        private void NaprawDzienPoDniu_Z_Zamiennikiem(
            Dictionary<DateTime, Lekarz?> plan,
            Dictionary<string, int> oblozenie)
        {
            var dni = _oryginalneDane.DniWMiesiacu.OrderBy(d => d).ToList();

            for (int i = 0; i + 1 < dni.Count; i++)
            {
                _ct.ThrowIfCancellationRequested();

                var d1 = dni[i];
                var d2 = dni[i + 1];
                var a = plan.GetValueOrDefault(d1);
                var b = plan.GetValueOrDefault(d2);
                if (a == null || b == null) continue;
                if (a.Symbol != b.Symbol) continue;

                // Dopuszczamy, jeśli którykolwiek to BC
                bool bc1 = Av(d1, a.Symbol) == TypDostepnosci.BardzoChce;
                bool bc2 = Av(d2, b.Symbol) == TypDostepnosci.BardzoChce;
                if (bc1 || bc2) continue;

                // Nie ruszamy rezerwacji – jeśli jedna strona to RZ, zmieniamy drugą
                bool r1 = Av(d1, a.Symbol) == TypDostepnosci.Rezerwacja;
                bool r2 = Av(d2, b.Symbol) == TypDostepnosci.Rezerwacja;

                DateTime dayToFix;
                string symToRemove;

                if (r1 && r2)
                {
                    // Nie powinno się zdarzyć (walidowane wcześniej), zachowawczo pomiń
                    SolverDiagnostics.Log($"[WrapperRepair] UWAGA: dwie rezerwacje dzień-po-dniu dla {a.Symbol} ({Fmt(d1)} & {Fmt(d2)}). Pomijam.");
                    continue;
                }
                else if (r1 && !r2) { dayToFix = d2; symToRemove = b.Symbol; }
                else if (!r1 && r2) { dayToFix = d1; symToRemove = a.Symbol; }
                else
                {
                    // Obie nie są rezerwacjami – zmieniamy „słabszą” deklarację; remis → d1
                    var s1 = Score(Av(d1, a.Symbol));
                    var s2 = Score(Av(d2, b.Symbol));
                    dayToFix = (s1 < s2) ? d1 : (s2 < s1 ? d2 : d1);
                    symToRemove = a.Symbol; // to i tak ten sam symbol
                }

                // SPRÓBUJ ZNALEŹĆ ZAMIENNIKA
                var replacement = ZnajdzZastepce(plan, oblozenie, dayToFix, symToRemove);

                if (replacement != null)
                {
                    var old = plan[dayToFix]!;
                    plan[dayToFix] = replacement;

                    oblozenie[old.Symbol]--;
                    oblozenie[replacement.Symbol]++;

                    SolverDiagnostics.Log($"[WrapperRepair] Zamiana: {Fmt(dayToFix)} {old.Symbol} ➜ {replacement.Symbol} (naprawa d+1 bez BC).");
                }
                else
                {
                    // Brak kandydatów — fallback do wyzerowania
                    SolverDiagnostics.Log($"[WrapperRepair] Brak zamiennika dla {Fmt(dayToFix)} ({symToRemove}) – zeruję ten dzień.");
                    var old = plan[dayToFix]!;
                    oblozenie[old.Symbol]--;
                    plan[dayToFix] = null;
                }
            }
        }

        private Lekarz? ZnajdzZastepce(
            Dictionary<DateTime, Lekarz?> plan,
            Dictionary<string, int> oblozenie,
            DateTime dzien,
            string symbolDoWyczyszczenia)
        {
            var prev = dzien.AddDays(-1);
            var next = dzien.AddDays(1);

            // Kandydaci: wszyscy oprócz aktualnie wpisanego
            var kandydaci = _oryginalneDane.Lekarze
                .Where(l => l.Symbol != symbolDoWyczyszczenia)
                .ToList();

            Lekarz? best = null;
            double bestScore = double.NegativeInfinity;

            foreach (var k in kandydaci)
            {
                _ct.ThrowIfCancellationRequested();

                var sym = k.Symbol;
                var avHere = Av(dzien, sym);

                // 1) Dostępność dopuszczająca przydział
                if (avHere is TypDostepnosci.Niedostepny or TypDostepnosci.Urlop or TypDostepnosci.DyzurInny)
                    continue;

                // 2) Limit
                int limit = _limityOryginalne.GetValueOrDefault(sym, 0);
                if (oblozenie.GetValueOrDefault(sym, 0) >= limit)
                    continue;

                // 3) Jednorazowe MW
                if (avHere == TypDostepnosci.MogeWarunkowo && MaJuzMW(plan, sym))
                    continue;

                bool isBC = avHere == TypDostepnosci.BardzoChce;

                // 4) Zakaz „d+1 bez BC” (względem sąsiadów już wpisanych w planie)
                if (!isBC)
                {
                    if (plan.TryGetValue(prev, out var p) && p != null && p.Symbol == sym) continue;
                    if (plan.TryGetValue(next, out var n) && n != null && n.Symbol == sym) continue;
                }

                // 5) „Inny dyżur ±1” dla nie-BC
                if (!isBC)
                {
                    if (Av(prev, sym) == TypDostepnosci.DyzurInny) continue;
                    if (Av(next, sym) == TypDostepnosci.DyzurInny) continue;
                }

                // 6) Scoring kandydata – preferuj BC>CH>MG>MW, większy zapas limitu i większą odległość od własnych dyżurów
                double score =
                    Score(avHere) * 1000.0
                    + (limit - oblozenie.GetValueOrDefault(sym, 0)) * 10.0
                    + NearestAssignedDistance(plan, dzien, sym);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = k;
                }
            }

            return best;
        }

        // ========================= POMOCNICZE =========================

        private TypDostepnosci Av(DateTime day, string sym)
        {
            if (!_oryginalneDane.Dostepnosc.TryGetValue(day, out var map)) return TypDostepnosci.Niedostepny;
            return map.TryGetValue(sym, out var t) ? t : TypDostepnosci.Niedostepny;
        }

        private static int Score(TypDostepnosci t) => t switch
        {
            TypDostepnosci.BardzoChce => 3,
            TypDostepnosci.Chce => 2,
            TypDostepnosci.Moge => 1,
            TypDostepnosci.MogeWarunkowo => 0,   // najniżej
            _ => -100
        };

        private bool MaJuzMW(Dictionary<DateTime, Lekarz?> plan, string symbol)
        {
            foreach (var (d, lek) in plan)
            {
                if (lek?.Symbol != symbol) continue;
                if (Av(d, symbol) == TypDostepnosci.MogeWarunkowo) return true;
            }
            return false;
        }

        private static string Fmt(DateTime d) => d.ToString("yyyy-MM-dd");

        private int NearestAssignedDistance(Dictionary<DateTime, Lekarz?> plan, DateTime day, string symbol)
        {
            var days = _oryginalneDane.DniWMiesiacu.OrderBy(d => d).ToList();
            int idx = days.IndexOf(day);
            int best = int.MaxValue;

            for (int i = idx - 1; i >= 0; i--)
                if (plan.TryGetValue(days[i], out var l) && l?.Symbol == symbol) { best = Math.Min(best, idx - i); break; }
            for (int i = idx + 1; i < days.Count; i++)
                if (plan.TryGetValue(days[i], out var l) && l?.Symbol == symbol) { best = Math.Min(best, i - idx); break; }

            return best == int.MaxValue ? 50 : best; // im większy, tym lepiej
        }
    }
}
