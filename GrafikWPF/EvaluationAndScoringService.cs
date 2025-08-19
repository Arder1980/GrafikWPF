using System;
using System.Collections.Generic;
using System.Linq;

namespace GrafikWPF
{
    public static class EvaluationAndScoringService
    {
        // Wagi do porównywania rozwiązań (większe = ważniejsze w finalnym score)
        private static readonly Dictionary<SolverPriority, double> PriorityWeights = new()
        {
            { SolverPriority.CiagloscPoczatkowa,           1_000_000_000_000 },
            { SolverPriority.LacznaLiczbaObsadzonychDni,       1_000_000_000 },
            { SolverPriority.SprawiedliwoscObciazenia,            1_000_000 },
            { SolverPriority.RownomiernoscRozlozenia,                 1_000 }
        };

        // Dodatkowe gratyfikatory (poza priorytetami)
        private const double REZERWACJA_WEIGHT = 100_000_000_000_000;
        private const double BARDZO_CHCE_WEIGHT = 100;
        private const double CHCE_WEIGHT = 10;
        private const double MOGE_WEIGHT = 1;

        /// <summary>
        /// Agregatowa ocena rozwiązania (większa = lepiej) zgodna z kolejnością priorytetów.
        /// Normalizacje:
        ///  - Sprawiedliwość i Równomierność są „im mniej, tym lepiej”, więc używamy 1/(1+X).
        /// </summary>
        public static double CalculateScore(RozwiazanyGrafik grafik, List<SolverPriority> priorytety, GrafikWejsciowy daneWejsciowe)
        {
            if (grafik == null) return double.MinValue;

            double totalDays = daneWejsciowe.DniWMiesiacu.Count;
            if (totalDays == 0) return 0;

            double normContinuity = grafik.DlugoscCiaguPoczatkowego / totalDays;
            double normCoverage = grafik.LiczbaDniObsadzonych / totalDays;

            // Uwaga: dla wskaźników „im mniej, tym lepiej” normalizujemy tak, by 1 = idealnie.
            double normFairness = 1.0 / (1.0 + grafik.WskaznikSprawiedliwosci);   // sprawiedliwość ∝ limitom
            double normSpacing = 1.0 / (1.0 + grafik.WskaznikRownomiernosci);     // równomierność w czasie

            double score = 0;

            // Rezerwacje traktujemy jako twardy bonus
            score += grafik.ZrealizowaneRezerwacje * REZERWACJA_WEIGHT;

            var normalizedValues = new Dictionary<SolverPriority, double>
            {
                { SolverPriority.CiagloscPoczatkowa,         normContinuity },
                { SolverPriority.LacznaLiczbaObsadzonychDni, normCoverage   },
                { SolverPriority.SprawiedliwoscObciazenia,   normFairness   },
                { SolverPriority.RownomiernoscRozlozenia,    normSpacing    }
            };

            foreach (var priority in priorytety)
                if (PriorityWeights.TryGetValue(priority, out double weight))
                    score += normalizedValues[priority] * weight;

            // Miękkie preferencje — drobne punkty „za chęci”
            score += grafik.ZrealizowaneBardzoChce * BARDZO_CHCE_WEIGHT;
            score += grafik.ZrealizowaneChce * CHCE_WEIGHT;
            score += grafik.ZrealizowaneMoge * MOGE_WEIGHT;

            return score;
        }

        /// <summary>
        /// Wektor lex-min dla porównywania rozwiązań (większe lepsze wartości zamieniamy na monotonną postać).
        /// </summary>
        public static long[] ToIntVector(RozwiazanyGrafik m, List<SolverPriority> priorytety)
        {
            var v = new long[priorytety.Count + 4]; // 4 dodatkowe metryki (Rezerwacje, BC, Ch, M)
            int i = 0;

            var metricsMap = new Dictionary<SolverPriority, long>
            {
                { SolverPriority.LacznaLiczbaObsadzonychDni, m.LiczbaDniObsadzonych },
                { SolverPriority.CiagloscPoczatkowa,         m.DlugoscCiaguPoczatkowego },

                // Dla „im mniej, tym lepiej” bierzemy ujemne wartości (żeby „mniej” było „lepsze” leksykograficznie)
                { SolverPriority.SprawiedliwoscObciazenia,  -(long)Math.Round(1_000_000.0 * m.WskaznikSprawiedliwosci) },
                { SolverPriority.RownomiernoscRozlozenia,   -(long)Math.Round(1_000_000.0 * m.WskaznikRownomiernosci)  },
            };

            foreach (var p in priorytety)
                v[i++] = metricsMap.GetValueOrDefault(p, 0);

            v[i++] = m.ZrealizowaneRezerwacje;
            v[i++] = m.ZrealizowaneBardzoChce;
            v[i++] = m.ZrealizowaneChce;
            v[i++] = m.ZrealizowaneMoge;
            return v;
        }

        /// <summary>
        /// Przelicza metryki jakości na podstawie przypisań i limitów.
        /// </summary>
        public static RozwiazanyGrafik CalculateMetrics(
            IReadOnlyDictionary<DateTime, Lekarz?> przypisania,
            IReadOnlyDictionary<string, int> oblozenie,
            GrafikWejsciowy daneWejsciowe)
        {
            var dni = daneWejsciowe.DniWMiesiacu;
            var p = new Dictionary<DateTime, Lekarz?>(przypisania);

            // Najdłuższy prefiks ciągłej obsady
            int cp = 0;
            foreach (var key in dni.OrderBy(d => d))
            {
                if (p.TryGetValue(key, out var l) && l != null) cp++;
                else break;
            }

            // Zliczenia preferencji
            int zrealizowaneRezerwacje = 0;
            int zrealizowaneChce = 0;
            int zrealizowaneBardzoChce = 0;
            int zrealizowaneMoge = 0;
            foreach (var wpis in p.Where(x => x.Value != null))
            {
                var typ = daneWejsciowe.Dostepnosc[wpis.Key][wpis.Value!.Symbol];
                if (typ == TypDostepnosci.Rezerwacja) zrealizowaneRezerwacje++;
                else if (typ == TypDostepnosci.Chce) zrealizowaneChce++;
                else if (typ == TypDostepnosci.BardzoChce) zrealizowaneBardzoChce++;
                else if (typ == TypDostepnosci.Moge) zrealizowaneMoge++;
            }

            // 1) SPRAWIEDLIWOŚĆ: odchylenie std. procentowych obciążeń względem limitów
            //    (im mniejsze, tym „sprawiedliwiej”)
            var obciazeniaProcentowe = new List<double>();
            foreach (var lekarz in daneWejsciowe.Lekarze.Where(l => l.IsAktywny))
            {
                int limit = daneWejsciowe.LimityDyzurow.GetValueOrDefault(lekarz.Symbol, 0);
                if (limit > 0)
                {
                    double przydzielone = oblozenie.GetValueOrDefault(lekarz.Symbol, 0);
                    obciazeniaProcentowe.Add(przydzielone * 100.0 / limit);
                }
            }

            double wskaznikSprawiedliwosci = 0.0;
            if (obciazeniaProcentowe.Count > 1)
            {
                double avg = obciazeniaProcentowe.Average();
                double sumSq = obciazeniaProcentowe.Sum(val => (val - avg) * (val - avg));
                wskaznikSprawiedliwosci = Math.Sqrt(sumSq / obciazeniaProcentowe.Count);
            }

            // 2) RÓWNOMIERNOŚĆ: „rozstrzelenie” w czasie — średnie odchylenie std. odstępów między dyżurami
            double wskaznikRownomiernosci = 0.0;
            var odchyleniaOdstepow = new List<double>();

            foreach (var lekarz in daneWejsciowe.Lekarze.Where(l => l.IsAktywny))
            {
                var dniLekarza = p.Where(kvp => kvp.Value?.Symbol == lekarz.Symbol)
                                  .Select(kvp => kvp.Key)
                                  .OrderBy(d => d)
                                  .ToList();

                if (dniLekarza.Count > 2)
                {
                    var odst = new List<double>();
                    for (int i = 0; i < dniLekarza.Count - 1; i++)
                        odst.Add((dniLekarza[i + 1] - dniLekarza[i]).TotalDays);

                    if (odst.Count > 0)
                    {
                        double avgGap = odst.Average();
                        double sumSqG = odst.Sum(val => (val - avgGap) * (val - avgGap));
                        odchyleniaOdstepow.Add(Math.Sqrt(sumSqG / odst.Count));
                    }
                }
            }
            if (odchyleniaOdstepow.Any())
                wskaznikRownomiernosci = odchyleniaOdstepow.Average();

            return new RozwiazanyGrafik
            {
                Przypisania = p,
                DlugoscCiaguPoczatkowego = cp,
                ZrealizowaneRezerwacje = zrealizowaneRezerwacje,
                ZrealizowaneChce = zrealizowaneChce,
                ZrealizowaneBardzoChce = zrealizowaneBardzoChce,
                ZrealizowaneMoge = zrealizowaneMoge,
                FinalneOblozenieLekarzy = new Dictionary<string, int>(oblozenie),

                // KLUCZOWE: spójne przypisanie do nowych nazw
                WskaznikSprawiedliwosci = wskaznikSprawiedliwosci,
                WskaznikRownomiernosci = wskaznikRownomiernosci
            };
        }
    }
}
