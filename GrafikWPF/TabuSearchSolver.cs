namespace GrafikWPF
{
    public class TabuSearchSolver : IGrafikSolver
    {
        private readonly int _tabuListSize;
        private readonly int _maxIterations;
        private readonly GrafikWejsciowy _daneWejsciowe;
        private readonly List<SolverPriority> _kolejnoscPriorytetow;
        private readonly IProgress<double>? _progressReporter;
        private readonly CancellationToken _cancellationToken;
        private readonly SolverUtility _utility;

        public TabuSearchSolver(GrafikWejsciowy daneWejsciowe, List<SolverPriority> kolejnoscPriorytetow, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            _daneWejsciowe = daneWejsciowe;
            _kolejnoscPriorytetow = kolejnoscPriorytetow;
            _progressReporter = progress;
            _cancellationToken = cancellationToken;
            _utility = new SolverUtility(daneWejsciowe);

            int problemSize = _daneWejsciowe.Lekarze.Count * _daneWejsciowe.DniWMiesiacu.Count;
            _tabuListSize = Math.Max(20, problemSize / 10);
            _maxIterations = Math.Max(300, problemSize * 2);
        }

        private double CalculateAdaptiveScore(RozwiazanyGrafik grafik, int iteration)
        {
            double progress = (double)iteration / _maxIterations;
            int prioritiesToConsider = 1 + (int)(progress * (_kolejnoscPriorytetow.Count - 1));
            var adaptivePriorities = _kolejnoscPriorytetow.Take(prioritiesToConsider).ToList();
            return EvaluationAndScoringService.CalculateScore(grafik, adaptivePriorities, _daneWejsciowe);
        }

        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            // ZMIANA: Zaczynamy od rozwiązania "chciwego", a nie w pełni losowego.
            var obecneRozwiazanie = _utility.StworzChciweRozwiazaniePoczatkowe();
            var najlepszeRozwiazanie = new Dictionary<DateTime, Lekarz?>(obecneRozwiazanie);

            var metrics = EvaluationAndScoringService.CalculateMetrics(najlepszeRozwiazanie, _utility.ObliczOblozenie(najlepszeRozwiazanie), _daneWejsciowe);
            double najlepszyFitness = EvaluationAndScoringService.CalculateScore(metrics, _kolejnoscPriorytetow, _daneWejsciowe);

            var tabuLista = new Queue<Dictionary<DateTime, Lekarz?>>();
            int iteracjeBezPoprawy = 0;
            int progDywersyfikacji = _maxIterations / 4;

            for (int i = 0; i < _maxIterations; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                var sasiedzi = GenerujSasiadow(obecneRozwiazanie);
                Dictionary<DateTime, Lekarz?>? najlepszySasiad = null;
                double najlepszyFitnessSasiada = double.MinValue;

                foreach (var sasiad in sasiedzi)
                {
                    if (!CzyJestWTabu(sasiad, tabuLista))
                    {
                        var sasiadMetrics = EvaluationAndScoringService.CalculateMetrics(sasiad, _utility.ObliczOblozenie(sasiad), _daneWejsciowe);
                        double sasiadFitness = CalculateAdaptiveScore(sasiadMetrics, i);

                        if (sasiadFitness > najlepszyFitnessSasiada)
                        {
                            najlepszyFitnessSasiada = sasiadFitness;
                            najlepszySasiad = sasiad;
                        }
                    }
                }

                if (najlepszySasiad != null)
                {
                    obecneRozwiazanie = najlepszySasiad;
                    if (tabuLista.Count >= _tabuListSize) tabuLista.Dequeue();
                    tabuLista.Enqueue(obecneRozwiazanie);

                    var sasiadMetricsFull = EvaluationAndScoringService.CalculateMetrics(obecneRozwiazanie, _utility.ObliczOblozenie(obecneRozwiazanie), _daneWejsciowe);
                    double sasiadFitnessFull = EvaluationAndScoringService.CalculateScore(sasiadMetricsFull, _kolejnoscPriorytetow, _daneWejsciowe);

                    if (sasiadFitnessFull > najlepszyFitness)
                    {
                        najlepszyFitness = sasiadFitnessFull;
                        najlepszeRozwiazanie = new Dictionary<DateTime, Lekarz?>(obecneRozwiazanie);
                        iteracjeBezPoprawy = 0;
                    }
                    else
                    {
                        iteracjeBezPoprawy++;
                    }
                }

                if (iteracjeBezPoprawy > progDywersyfikacji)
                {
                    obecneRozwiazanie = Dywersyfikuj(obecneRozwiazanie);
                    iteracjeBezPoprawy = 0;
                }

                _progressReporter?.Report((double)(i + 1) / _maxIterations);
            }

            return EvaluationAndScoringService.CalculateMetrics(najlepszeRozwiazanie, _utility.ObliczOblozenie(najlepszeRozwiazanie), _daneWejsciowe);
        }

        private List<Dictionary<DateTime, Lekarz?>> GenerujSasiadow(Dictionary<DateTime, Lekarz?> obecny)
        {
            var sasiedzi = new List<Dictionary<DateTime, Lekarz?>>();
            int liczbaSasiadow = Math.Max(10, _daneWejsciowe.Lekarze.Count / 2);
            for (int i = 0; i < liczbaSasiadow; i++) sasiedzi.Add(_utility.GenerujSasiada(obecny));
            return sasiedzi;
        }

        private bool CzyJestWTabu(Dictionary<DateTime, Lekarz?> rozwiazanie, Queue<Dictionary<DateTime, Lekarz?>> tabuLista)
        {
            foreach (var tabu in tabuLista)
            {
                if (rozwiazanie.Count == tabu.Count && !rozwiazanie.Except(tabu).Any()) return true;
            }
            return false;
        }

        private Dictionary<DateTime, Lekarz?> Dywersyfikuj(Dictionary<DateTime, Lekarz?> obecny)
        {
            var zdywersyfikowany = new Dictionary<DateTime, Lekarz?>(obecny);
            int liczbaZamian = _daneWejsciowe.DniWMiesiacu.Count / 5;
            for (int i = 0; i < liczbaZamian; i++) zdywersyfikowany = _utility.GenerujSasiada(zdywersyfikowany);
            return zdywersyfikowany;
        }
    }
}