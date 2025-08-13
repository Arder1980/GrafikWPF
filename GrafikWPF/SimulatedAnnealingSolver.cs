namespace GrafikWPF
{
    public class SimulatedAnnealingSolver : IGrafikSolver
    {
        private const double InitialTemperature = 1000.0;
        private const double CoolingRate = 0.995;
        private readonly int _iterationsPerTemperature;

        private readonly GrafikWejsciowy _daneWejsciowe;
        private readonly List<SolverPriority> _kolejnoscPriorytetow;
        private readonly IProgress<double>? _progressReporter;
        private readonly CancellationToken _cancellationToken;
        private readonly Random _random = new();
        private readonly SolverUtility _utility;

        public SimulatedAnnealingSolver(GrafikWejsciowy daneWejsciowe, List<SolverPriority> kolejnoscPriorytetow, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            _daneWejsciowe = daneWejsciowe;
            _kolejnoscPriorytetow = kolejnoscPriorytetow;
            _progressReporter = progress;
            _cancellationToken = cancellationToken;
            _utility = new SolverUtility(daneWejsciowe);
            _iterationsPerTemperature = Math.Max(100, _daneWejsciowe.Lekarze.Count * 10);
        }

        private double CalculateAdaptiveScore(RozwiazanyGrafik grafik, double temperature)
        {
            double progress = 1.0 - (Math.Log(temperature) / Math.Log(InitialTemperature));
            int prioritiesToConsider = 1 + (int)(progress * (_kolejnoscPriorytetow.Count - 1));
            var adaptivePriorities = _kolejnoscPriorytetow.Take(prioritiesToConsider).ToList();
            return EvaluationAndScoringService.CalculateScore(grafik, adaptivePriorities, _daneWejsciowe);
        }

        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            // ZMIANA: Zaczynamy od rozwiązania "chciwego", a nie w pełni losowego.
            var currentSolution = _utility.StworzChciweRozwiazaniePoczatkowe();
            var bestSolution = new Dictionary<DateTime, Lekarz?>(currentSolution);

            var currentMetrics = EvaluationAndScoringService.CalculateMetrics(currentSolution, _utility.ObliczOblozenie(currentSolution), _daneWejsciowe);
            double bestFitness = EvaluationAndScoringService.CalculateScore(currentMetrics, _kolejnoscPriorytetow, _daneWejsciowe);
            double currentFitness = CalculateAdaptiveScore(currentMetrics, InitialTemperature);

            double temperature = InitialTemperature;
            int totalIterations = (int)Math.Log(0.1 / InitialTemperature, CoolingRate) * _iterationsPerTemperature;
            int currentIteration = 0;

            while (temperature > 0.1)
            {
                for (int i = 0; i < _iterationsPerTemperature; i++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();

                    var newSolution = _utility.GenerujSasiada(currentSolution);
                    ConstraintValidationService.RepairSchedule(newSolution, _daneWejsciowe);

                    var newMetrics = EvaluationAndScoringService.CalculateMetrics(newSolution, _utility.ObliczOblozenie(newSolution), _daneWejsciowe);
                    double newFitness = CalculateAdaptiveScore(newMetrics, temperature);

                    if (newFitness > currentFitness || _random.NextDouble() < Math.Exp((newFitness - currentFitness) / temperature))
                    {
                        currentSolution = newSolution;
                        currentFitness = newFitness;

                        double fullNewFitness = EvaluationAndScoringService.CalculateScore(newMetrics, _kolejnoscPriorytetow, _daneWejsciowe);
                        if (fullNewFitness > bestFitness)
                        {
                            bestSolution = new Dictionary<DateTime, Lekarz?>(currentSolution);
                            bestFitness = fullNewFitness;
                        }
                    }
                    currentIteration++;
                }
                temperature *= CoolingRate;
                if (totalIterations > 0)
                {
                    _progressReporter?.Report((double)currentIteration / totalIterations);
                }
            }

            return EvaluationAndScoringService.CalculateMetrics(bestSolution, _utility.ObliczOblozenie(bestSolution), _daneWejsciowe);
        }
    }
}