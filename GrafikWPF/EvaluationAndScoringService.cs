using System;
using System.Collections.Generic;
using System.Linq;

namespace GrafikWPF
{
    public static class EvaluationAndScoringService
    {
        private static readonly Dictionary<SolverPriority, double> PriorityWeights = new()
        {
            { SolverPriority.CiagloscPoczatkowa,       1_000_000_000_000 },
            { SolverPriority.LacznaLiczbaObsadzonychDni,  1_000_000_000 },
            { SolverPriority.SprawiedliwoscObciazenia,     1_000_000 },
            { SolverPriority.RownomiernoscRozlozenia,       1_000 }
        };

        private const double REZERWACJA_WEIGHT = 100_000_000_000_000;
        private const double BARDZO_CHCE_WEIGHT = 100;
        private const double CHCE_WEIGHT = 10;
        private const double MOGE_WEIGHT = 1;

        public static double CalculateScore(RozwiazanyGrafik grafik, List<SolverPriority> priorytety, GrafikWejsciowy daneWejsciowe)
        {
            if (grafik == null) return double.MinValue;

            double totalDays = daneWejsciowe.DniWMiesiacu.Count;
            if (totalDays == 0) return 0;

            double normContinuity = grafik.DlugoscCiaguPoczatkowego / totalDays;
            double normCoverage = grafik.LiczbaDniObsadzonych / totalDays;
            double normFairness = 1.0 / (1.0 + grafik.WskaznikRownomiernosci);
            double normSpacing = 1.0 / (1.0 + grafik.WskaznikRozlozeniaDyzurow);

            double score = 0;

            score += grafik.ZrealizowaneRezerwacje * REZERWACJA_WEIGHT;

            var normalizedValues = new Dictionary<SolverPriority, double>
            {
                { SolverPriority.CiagloscPoczatkowa, normContinuity },
                { SolverPriority.LacznaLiczbaObsadzonychDni, normCoverage },
                { SolverPriority.SprawiedliwoscObciazenia, normFairness },
                { SolverPriority.RownomiernoscRozlozenia, normSpacing }
            };

            foreach (var priority in priorytety)
            {
                if (PriorityWeights.TryGetValue(priority, out double weight))
                {
                    score += normalizedValues[priority] * weight;
                }
            }

            score += grafik.ZrealizowaneBardzoChce * BARDZO_CHCE_WEIGHT;
            score += grafik.ZrealizowaneChce * CHCE_WEIGHT;
            score += grafik.ZrealizowaneMoge * MOGE_WEIGHT;

            return score;
        }

        public static long[] ToIntVector(RozwiazanyGrafik m, List<SolverPriority> priorytety)
        {
            var v = new long[priorytety.Count + 4]; // 4 dodatkowe metryki (Rezerwacje, BC, Ch, M)
            int i = 0;

            var metricsMap = new Dictionary<SolverPriority, long>
            {
                { SolverPriority.LacznaLiczbaObsadzonychDni, m.LiczbaDniObsadzonych },
                { SolverPriority.CiagloscPoczatkowa, m.DlugoscCiaguPoczatkowego },
                { SolverPriority.SprawiedliwoscObciazenia, -(long)Math.Round(1_000_000.0 * m.WskaznikRownomiernosci) },
                { SolverPriority.RownomiernoscRozlozenia, -(long)Math.Round(1_000_000.0 * m.WskaznikRozlozeniaDyzurow) }
            };

            foreach (var p in priorytety)
            {
                v[i++] = metricsMap.GetValueOrDefault(p, 0);
            }

            v[i++] = m.ZrealizowaneRezerwacje;
            v[i++] = m.ZrealizowaneBardzoChce;
            v[i++] = m.ZrealizowaneChce;
            v[i++] = m.ZrealizowaneMoge;
            return v;
        }

        public static RozwiazanyGrafik CalculateMetrics(IReadOnlyDictionary<DateTime, Lekarz?> przypisania, IReadOnlyDictionary<string, int> oblozenie, GrafikWejsciowy daneWejsciowe)
        {
            var dni = daneWejsciowe.DniWMiesiacu;
            var p = new Dictionary<DateTime, Lekarz?>(przypisania);

            int cp = 0;
            foreach (var key in dni.OrderBy(d => d)) { if (p.TryGetValue(key, out var l) && l != null) cp++; else break; }

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

            var obciazeniaProcentowe = new List<double>();
            foreach (var lekarz in daneWejsciowe.Lekarze.Where(l => l.IsAktywny))
            {
                int maksymalnaLiczbaDyzurow = daneWejsciowe.LimityDyzurow.GetValueOrDefault(lekarz.Symbol, 0);
                if (maksymalnaLiczbaDyzurow > 0)
                {
                    double ldyzurow = oblozenie.GetValueOrDefault(lekarz.Symbol, 0);
                    obciazeniaProcentowe.Add(ldyzurow * 100.0 / maksymalnaLiczbaDyzurow);
                }
            }

            double wskaznikRownomiernosci = 0.0;
            if (obciazeniaProcentowe.Count > 1)
            {
                double srednia = obciazeniaProcentowe.Average();
                double sumaKwadratowRoznic = obciazeniaProcentowe.Sum(val => (val - srednia) * (val - srednia));
                wskaznikRownomiernosci = Math.Sqrt(sumaKwadratowRoznic / obciazeniaProcentowe.Count);
            }

            double wskaznikRozlozeniaDyzurow = 0.0;
            var wszystkieOdchylenia = new List<double>();
            foreach (var lekarz in daneWejsciowe.Lekarze.Where(l => l.IsAktywny))
            {
                var dyzuryLekarza = p.Where(kvp => kvp.Value?.Symbol == lekarz.Symbol)
                                     .Select(kvp => kvp.Key)
                                     .OrderBy(d => d)
                                     .ToList();

                if (dyzuryLekarza.Count > 2)
                {
                    var odstepy = new List<double>();
                    for (int i = 0; i < dyzuryLekarza.Count - 1; i++)
                    {
                        odstepy.Add((dyzuryLekarza[i + 1] - dyzuryLekarza[i]).TotalDays);
                    }

                    if (odstepy.Any())
                    {
                        double sredniOdstep = odstepy.Average();
                        double sumaKwadratowRoznicOdstepow = odstepy.Sum(val => (val - sredniOdstep) * (val - sredniOdstep));
                        wszystkieOdchylenia.Add(Math.Sqrt(sumaKwadratowRoznicOdstepow / odstepy.Count));
                    }
                }
            }
            if (wszystkieOdchylenia.Any())
            {
                wskaznikRozlozeniaDyzurow = wszystkieOdchylenia.Average();
            }

            return new RozwiazanyGrafik
            {
                Przypisania = p,
                DlugoscCiaguPoczatkowego = cp,
                ZrealizowaneRezerwacje = zrealizowaneRezerwacje,
                ZrealizowaneChce = zrealizowaneChce,
                ZrealizowaneBardzoChce = zrealizowaneBardzoChce,
                ZrealizowaneMoge = zrealizowaneMoge,
                FinalneOblozenieLekarzy = new Dictionary<string, int>(oblozenie),
                WskaznikRownomiernosci = wskaznikRownomiernosci,
                WskaznikRozlozeniaDyzurow = wskaznikRozlozeniaDyzurow
            };
        }
    }
}