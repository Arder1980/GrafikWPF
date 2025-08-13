using System.Collections.Concurrent;

namespace GrafikWPF
{
    public class AntColonySolver : IGrafikSolver
    {
        private readonly int _numAnts;
        private readonly int _maxGenerations;
        private const double EvaporationRate = 0.5;
        private const double Alpha = 1.0;
        private const double Beta = 5.0;
        private const double Q0 = 0.7;

        private readonly GrafikWejsciowy _daneWejsciowe;
        private readonly List<SolverPriority> _kolejnoscPriorytetow;
        private readonly IProgress<double>? _progressReporter;
        private readonly CancellationToken _cancellationToken;
        private readonly SolverUtility _utility;
        private readonly Random _random = new();
        private Dictionary<DateTime, Dictionary<string, double>> _pheromoneMatrix = new();

        public AntColonySolver(GrafikWejsciowy daneWejsciowe, List<SolverPriority> kolejnoscPriorytetow, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            _daneWejsciowe = daneWejsciowe;
            _kolejnoscPriorytetow = kolejnoscPriorytetow;
            _progressReporter = progress;
            _cancellationToken = cancellationToken;
            _utility = new SolverUtility(daneWejsciowe);

            _numAnts = Math.Max(50, _daneWejsciowe.Lekarze.Count * 3);
            _maxGenerations = Math.Max(200, _daneWejsciowe.DniWMiesiacu.Count * 15);
        }

        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            InitializePheromones();
            var bestSolution = new Dictionary<DateTime, Lekarz?>();
            double bestFitness = double.MinValue;

            for (int i = 0; i < _maxGenerations; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                var solutions = new ConcurrentBag<Dictionary<DateTime, Lekarz?>>();

                Parallel.For(0, _numAnts, ant =>
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var solution = BuildSolutionForAnt();
                    solutions.Add(solution);
                });

                var bestInGeneration = solutions.AsParallel().OrderByDescending(s =>
                {
                    var m = EvaluationAndScoringService.CalculateMetrics(s, _utility.ObliczOblozenie(s), _daneWejsciowe);
                    return EvaluationAndScoringService.CalculateScore(m, _kolejnoscPriorytetow, _daneWejsciowe);
                }).First();

                var bestMetrics = EvaluationAndScoringService.CalculateMetrics(bestInGeneration, _utility.ObliczOblozenie(bestInGeneration), _daneWejsciowe);
                var bestFitnessInGeneration = EvaluationAndScoringService.CalculateScore(bestMetrics, _kolejnoscPriorytetow, _daneWejsciowe);

                if (bestFitnessInGeneration > bestFitness)
                {
                    bestFitness = bestFitnessInGeneration;
                    bestSolution = bestInGeneration;
                }

                EvaporatePheromones();
                UpdatePheromones(bestSolution, bestFitness);

                _progressReporter?.Report((double)(i + 1) / _maxGenerations);
            }

            return EvaluationAndScoringService.CalculateMetrics(bestSolution, _utility.ObliczOblozenie(bestSolution), _daneWejsciowe);
        }

        private void InitializePheromones()
        {
            _pheromoneMatrix = new Dictionary<DateTime, Dictionary<string, double>>();
            foreach (var dzien in _daneWejsciowe.DniWMiesiacu)
            {
                _pheromoneMatrix[dzien] = new Dictionary<string, double>();
                foreach (var lekarz in _daneWejsciowe.Lekarze)
                {
                    _pheromoneMatrix[dzien][lekarz.Symbol] = 1.0;
                }
            }
        }

        private Dictionary<DateTime, Lekarz?> BuildSolutionForAnt()
        {
            var newSolution = new Dictionary<DateTime, Lekarz?>();
            var oblozenie = _daneWejsciowe.Lekarze.ToDictionary(l => l.Symbol, l => 0);
            var wykorzystaneW = new HashSet<string>();

            foreach (var dzien in _daneWejsciowe.DniWMiesiacu)
            {
                var kandydaci = ConstraintValidationService.GetValidCandidatesForDay(dzien, _daneWejsciowe, newSolution, oblozenie, wykorzystaneW);
                if (!kandydaci.Any())
                {
                    newSolution[dzien] = null;
                    continue;
                }

                var attractiveness = kandydaci.ToDictionary(
                    kandydat => kandydat,
                    kandydat => Math.Pow(_pheromoneMatrix[dzien][kandydat.Symbol], Alpha) * Math.Pow(GetHeuristicValue(dzien, kandydat), Beta)
                );

                Lekarz? wybranyLekarz;
                if (_random.NextDouble() < Q0)
                {
                    wybranyLekarz = attractiveness.OrderByDescending(kvp => kvp.Value).First().Key;
                }
                else
                {
                    double totalAttractiveness = attractiveness.Values.Sum();
                    double randomValue = _random.NextDouble() * totalAttractiveness;
                    wybranyLekarz = null;

                    foreach (var choice in attractiveness)
                    {
                        randomValue -= choice.Value;
                        if (randomValue <= 0)
                        {
                            wybranyLekarz = choice.Key;
                            break;
                        }
                    }
                    wybranyLekarz ??= kandydaci.Last();
                }

                newSolution[dzien] = wybranyLekarz;
                oblozenie[wybranyLekarz.Symbol]++;
                if (_daneWejsciowe.Dostepnosc[dzien][wybranyLekarz.Symbol] == TypDostepnosci.MogeWarunkowo)
                {
                    wykorzystaneW.Add(wybranyLekarz.Symbol);
                }
            }
            return newSolution;
        }

        private double GetHeuristicValue(DateTime dzien, Lekarz lekarz)
        {
            return _daneWejsciowe.Dostepnosc[dzien][lekarz.Symbol] switch
            {
                TypDostepnosci.BardzoChce => 100.0,
                TypDostepnosci.Chce => 20.0,
                TypDostepnosci.Moge => 1.0,
                TypDostepnosci.MogeWarunkowo => 0.5,
                _ => 0.1
            };
        }

        private void EvaporatePheromones()
        {
            foreach (var dzien in _pheromoneMatrix.Keys)
            {
                foreach (var lekarzSymbol in _pheromoneMatrix[dzien].Keys.ToList())
                {
                    _pheromoneMatrix[dzien][lekarzSymbol] *= (1.0 - EvaporationRate);
                }
            }
        }

        private void UpdatePheromones(Dictionary<DateTime, Lekarz?> solution, double fitness)
        {
            if (fitness <= 0) return;
            // Skala fitnessu się zmieniła, więc depozyt feromonu wymaga dostosowania.
            // Używamy potęgi, aby wzmocnić różnice między dobrymi a bardzo dobrymi wynikami.
            double pheromoneDeposit = Math.Pow(fitness / 1_000_000_000_000, 2);

            foreach (var entry in solution)
            {
                if (entry.Value != null)
                {
                    _pheromoneMatrix[entry.Key][entry.Value.Symbol] += pheromoneDeposit;
                }
            }
        }
    }
}