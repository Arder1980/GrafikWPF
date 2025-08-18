using System.Collections.Concurrent;

namespace GrafikWPF
{
    public class GeneticSolver : IGrafikSolver
    {
        private class Chromosome
        {
            public Dictionary<DateTime, Lekarz?> Genes { get; set; }
            public double Fitness { get; set; }

            public Chromosome(Dictionary<DateTime, Lekarz?> genes)
            {
                Genes = genes;
                Fitness = 0.0;
            }

            public Chromosome Clone()
            {
                return new Chromosome(new Dictionary<DateTime, Lekarz?>(Genes));
            }
        }

        private readonly int _populationSize;
        private readonly int _generations;
        private const double CrossoverRate = 0.85;
        private const double MutationRate = 0.05;
        private const int TournamentSize = 5;

        private readonly GrafikWejsciowy _daneWejsciowe;
        private readonly List<SolverPriority> _kolejnoscPriorytetow;
        private readonly IProgress<double>? _progressReporter;
        private readonly CancellationToken _cancellationToken;
        private readonly SolverUtility _utility;

        private List<Chromosome> _population = new();
        private readonly Random _random = new();

        public GeneticSolver(GrafikWejsciowy daneWejsciowe, List<SolverPriority> kolejnoscPriorytetow, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            _daneWejsciowe = daneWejsciowe;
            _kolejnoscPriorytetow = kolejnoscPriorytetow;
            _progressReporter = progress;
            _cancellationToken = cancellationToken;
            _utility = new SolverUtility(daneWejsciowe);

            int problemSize = _daneWejsciowe.Lekarze.Count * _daneWejsciowe.DniWMiesiacu.Count;
            _populationSize = Math.Max(50, problemSize / 5);
            _generations = Math.Max(150, problemSize * 2);
        }

        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            RunLogger.Start("GS", _daneWejsciowe, _kolejnoscPriorytetow);

            StworzPopulacjePoczatkowa();
            ObliczDopasowanie();

            for (int i = 0; i < _generations; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                var nowaPopulacja = new ConcurrentBag<Chromosome>();

                var najlepszy = _population.OrderByDescending(c => c.Fitness).First();
                nowaPopulacja.Add(najlepszy.Clone());

                Parallel.For(1, _populationSize, _ =>
                {
                    var rodzic1 = Selekcja();
                    var rodzic2 = Selekcja();
                    var dziecko = Krzyzowanie(rodzic1, rodzic2);
                    Mutacja(dziecko);
                    nowaPopulacja.Add(dziecko);
                });

                _population = nowaPopulacja.ToList();
                ObliczDopasowanie();
                _progressReporter?.Report((double)(i + 1) / _generations);
            }

            var finalnyNajlepszy = _population.OrderByDescending(c => c.Fitness).First();
            var __map = finalnyNajlepszy.Genes;
            var __ob = _utility.ObliczOblozenie(__map);
            var __result = EvaluationAndScoringService.CalculateMetrics(__map, __ob, _daneWejsciowe);
            RunLogger.Stop(__result);
            return __result;
        }

        private void StworzPopulacjePoczatkowa()
        {
            _population = new List<Chromosome>();
            for (int i = 0; i < _populationSize; i++)
            {
                _population.Add(new Chromosome(_utility.StworzLosoweRozwiazanie()));
            }
        }

        private void ObliczDopasowanie()
        {
            Parallel.ForEach(_population, chromosom =>
            {
                var metryki = EvaluationAndScoringService.CalculateMetrics(chromosom.Genes, _utility.ObliczOblozenie(chromosom.Genes), _daneWejsciowe);
                chromosom.Fitness = EvaluationAndScoringService.CalculateScore(metryki, _kolejnoscPriorytetow, _daneWejsciowe);
            });
        }

        private Chromosome Selekcja()
        {
            var turniej = new List<Chromosome>();
            for (int i = 0; i < TournamentSize; i++)
            {
                turniej.Add(_population[_random.Next(_populationSize)]);
            }
            return turniej.OrderByDescending(c => c.Fitness).First();
        }

        private Chromosome Krzyzowanie(Chromosome rodzic1, Chromosome rodzic2)
        {
            if (_random.NextDouble() > CrossoverRate)
            {
                return rodzic1.Clone();
            }

            var punktKrzyzowania = _random.Next(_daneWejsciowe.DniWMiesiacu.Count);
            var dni = _daneWejsciowe.DniWMiesiacu;
            var dzieckoGenes = new Dictionary<DateTime, Lekarz?>();

            for (int i = 0; i < dni.Count; i++)
            {
                dzieckoGenes[dni[i]] = i < punktKrzyzowania ? rodzic1.Genes[dni[i]] : rodzic2.Genes[dni[i]];
            }

            ConstraintValidationService.RepairSchedule(dzieckoGenes, _daneWejsciowe);
            return new Chromosome(dzieckoGenes);
        }

        private void Mutacja(Chromosome chromosom)
        {
            foreach (var dzien in _daneWejsciowe.DniWMiesiacu)
            {
                if (_random.NextDouble() < MutationRate)
                {
                    var oblozenie = _utility.ObliczOblozenie(chromosom.Genes);
                    var wykorzystaneW = chromosom.Genes
                        .Where(g => g.Value != null && _daneWejsciowe.Dostepnosc[g.Key][g.Value.Symbol] == TypDostepnosci.MogeWarunkowo)
                        .Select(g => g.Value!.Symbol)
                        .ToHashSet();

                    var kandydaci = ConstraintValidationService.GetValidCandidatesForDay(dzien, _daneWejsciowe, chromosom.Genes, oblozenie, wykorzystaneW);
                    chromosom.Genes[dzien] = kandydaci.Any() ? kandydaci[_random.Next(kandydaci.Count)] : null;
                }
            }
            ConstraintValidationService.RepairSchedule(chromosom.Genes, _daneWejsciowe);
        }
    }
}