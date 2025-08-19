using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace GrafikWPF
{
    public partial class BenchmarkWindow : Window, INotifyPropertyChanged
    {
        public record PriorityProfile(string Name, List<SolverPriority> Priorities);

        private readonly DispatcherTimer _countdownTimer;
        public ObservableCollection<BenchmarkResult> Results { get; set; }
        public ObservableCollection<PriorityProfile> PriorityProfiles { get; set; }

        private PriorityProfile _selectedPriorityProfile;
        public PriorityProfile SelectedPriorityProfile
        {
            get => _selectedPriorityProfile;
            set { _selectedPriorityProfile = value; OnPropertyChanged(); }
        }

        private readonly Dictionary<string, RozwiazanyGrafik> _optimalStandards = new();
        private readonly string _debugLogPath = Path.Combine(AppContext.BaseDirectory, "benchmark_debug_log.txt");

        public BenchmarkWindow()
        {
            InitializeComponent();
            DataContext = this;

            Results = new ObservableCollection<BenchmarkResult>();
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

            PriorityProfiles = new ObservableCollection<PriorityProfile>
            {
                new("Ciągłość > Obsadzenie > Sprawiedliwość > Rozłożenie", new List<SolverPriority> { SolverPriority.CiagloscPoczatkowa, SolverPriority.LacznaLiczbaObsadzonychDni, SolverPriority.SprawiedliwoscObciazenia, SolverPriority.RownomiernoscRozlozenia }),
                new("Obsadzenie > Sprawiedliwość > Rozłożenie > Ciągłość", new List<SolverPriority> { SolverPriority.LacznaLiczbaObsadzonychDni, SolverPriority.SprawiedliwoscObciazenia, SolverPriority.RownomiernoscRozlozenia, SolverPriority.CiagloscPoczatkowa }),
                new("Sprawiedliwość > Rozłożenie > Ciągłość > Obsadzenie", new List<SolverPriority> { SolverPriority.SprawiedliwoscObciazenia, SolverPriority.RownomiernoscRozlozenia, SolverPriority.CiagloscPoczatkowa, SolverPriority.LacznaLiczbaObsadzonychDni }),
                new("Rozłożenie > Ciągłość > Obsadzenie > Sprawiedliwość", new List<SolverPriority> { SolverPriority.RownomiernoscRozlozenia, SolverPriority.CiagloscPoczatkowa, SolverPriority.LacznaLiczbaObsadzonychDni, SolverPriority.SprawiedliwoscObciazenia })
            };
            _selectedPriorityProfile = PriorityProfiles.First();

            LoadOptimalStandards();
            InitializeBenchmarkTasks();
        }

        private void LoadOptimalStandards()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames().Where(n => n.Contains("BenchmarkStandards"));

            foreach (var name in resourceNames)
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null) continue;
                using var reader = new StreamReader(stream);
                string json = reader.ReadToEnd();
                try
                {
                    var standard = JsonSerializer.Deserialize<RozwiazanyGrafik>(json);
                    if (standard != null)
                    {
                        var key = Path.GetFileNameWithoutExtension(name.Split('.').Reverse().Skip(1).Reverse().Last());
                        _optimalStandards[key] = standard;
                    }
                }
                catch (JsonException ex)
                {
                    MessageBox.Show($"Błąd parsowania pliku wzorca: {name}\n\n{ex.Message}", "Błąd krytyczny", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void InitializeBenchmarkTasks()
        {
            Results.Clear();
            var testCases = new[]
            {
                new { Name = "Mały (7 dyżurnych)", DoctorCount = 7 },
                new { Name = "Średni (12 dyżurnych)", DoctorCount = 12 },
                new { Name = "Duży (20 dyżurnych)", DoctorCount = 20 }
            };

            var solvers = Enum.GetValues(typeof(SolverType)).Cast<SolverType>().ToList();

            foreach (var solverType in solvers)
            {
                foreach (var testCase in testCases)
                {
                    Results.Add(new BenchmarkResult
                    {
                        EngineName = solverType.ToString() + "Solver",
                        TestCaseName = testCase.Name,
                        SolverType = solverType,
                        DoctorCount = testCase.DoctorCount
                    });
                }
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            ExportButton.IsEnabled = false;
            PriorityProfileComboBox.IsEnabled = false;

            if (File.Exists(_debugLogPath)) File.Delete(_debugLogPath);

            var selectedPriorities = SelectedPriorityProfile.Priorities;

            var priorityKeyBuilder = new StringBuilder();
            foreach (var priority in selectedPriorities)
            {
                string keyPart = priority switch
                {
                    SolverPriority.CiagloscPoczatkowa => "continuity",
                    SolverPriority.LacznaLiczbaObsadzonychDni => "coverage",
                    SolverPriority.SprawiedliwoscObciazenia => "fairness",
                    SolverPriority.RownomiernoscRozlozenia => "spacing",
                    _ => ""
                };
                if (priorityKeyBuilder.Length > 0) priorityKeyBuilder.Append('_');
                priorityKeyBuilder.Append(keyPart);
            }
            string profileKey = priorityKeyBuilder.ToString();

            foreach (var result in Results)
            {
                Dispatcher.BeginInvoke(() => (BenchmarkItemsControl.ItemContainerGenerator.ContainerFromItem(result) as FrameworkElement)?.BringIntoView(), DispatcherPriority.Background);

                result.Status = "W toku...";
                result.ExecutionTime = "-";
                result.Progress = 0;
                result.TimeRemaining = "";
                result.QualityScore = "-";

                var testData = BenchmarkDataFactory.CreateTestCase(result.DoctorCount);

                string testCaseKey = result.DoctorCount == 7 ? "small" : (result.DoctorCount == 12 ? "medium" : "large");

                string standardKey = $"optimal_{testCaseKey}_{profileKey}";
                _optimalStandards.TryGetValue(standardKey, out var optimalStandard);

                double optimalScore = 0;

                if (optimalStandard != null)
                {
                    // Ujednolicona ocena wzorca
                    optimalScore = EvaluationAndScoringService.CalculateScore(optimalStandard, selectedPriorities, testData);
                }

                var stopwatch = new Stopwatch();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                int countdown = 60;

                EventHandler? tickHandler = (s, args) =>
                {
                    countdown--;
                    if (countdown >= 0) result.TimeRemaining = $"Do anulowania: {countdown}s";
                };
                _countdownTimer.Tick += tickHandler;
                result.TimeRemaining = $"Do anulowania: {countdown}s";
                _countdownTimer.Start();

                RozwiazanyGrafik? solverResult = null;

                try
                {
                    solverResult = await Task.Run(() =>
                    {
                        stopwatch.Start();
                        var progress = new Progress<double>(p => result.Progress = p * 100);
                        var solver = SolverFactory.CreateSolver(result.SolverType, testData, selectedPriorities, progress, cts.Token);
                        var solution = solver.ZnajdzOptymalneRozwiazanie();
                        stopwatch.Stop();
                        return solution;
                    }, cts.Token);

                    result.Status = "Ukończono";
                    result.ExecutionTime = stopwatch.ElapsedMilliseconds.ToString();
                    result.Progress = 100;

                    if (solverResult != null && solverResult.Przypisania.Any() && optimalStandard != null && optimalScore > 0)
                    {
                        // Spójna ocena wyniku solvera
                        double solverScore = EvaluationAndScoringService.CalculateScore(solverResult, selectedPriorities, testData);
                        double quality = (solverScore / optimalScore) * 100.0;
                        result.QualityScore = $"{quality:F2}%";
                        LogDebugInfo(result, optimalStandard, solverResult, optimalScore, solverScore, testData);
                    }
                    else if (optimalStandard == null)
                    {
                        result.QualityScore = "Brak wzorca";
                    }
                    else
                    {
                        result.QualityScore = "Brak danych";
                    }
                }
                catch (OperationCanceledException)
                {
                    HandleTimeout(result, stopwatch, solverResult, testData, optimalStandard, selectedPriorities);
                }
                catch (AggregateException ae)
                {
                    if (ae.Flatten().InnerExceptions.OfType<OperationCanceledException>().Any())
                    {
                        HandleTimeout(result, stopwatch, solverResult, testData, optimalStandard, selectedPriorities);
                    }
                    else
                    {
                        HandleGenericError(result, stopwatch, ae);
                    }
                }
                catch (Exception ex)
                {
                    HandleGenericError(result, stopwatch, ex);
                }
                finally
                {
                    _countdownTimer.Stop();
                    _countdownTimer.Tick -= tickHandler;
                    result.TimeRemaining = "";
                }
            }

            StartButton.IsEnabled = true;
            ExportButton.IsEnabled = true;
            PriorityProfileComboBox.IsEnabled = true;
        }

        private void LogDebugInfo(BenchmarkResult result, RozwiazanyGrafik? standard, RozwiazanyGrafik solverResult, double optimalScore, double solverScore, GrafikWejsciowy testData)
        {
            if (standard == null) return;
            var utility = new SolverUtility(testData);

            // Liczymy metryki jawnie na potrzeby logu, żeby nazwy i wartości były spójne
            var standardMetrics = EvaluationAndScoringService.CalculateMetrics(standard.Przypisania, utility.ObliczOblozenie(standard.Przypisania), testData);
            var solverMetrics = EvaluationAndScoringService.CalculateMetrics(solverResult.Przypisania, utility.ObliczOblozenie(solverResult.Przypisania), testData);

            var sb = new StringBuilder();
            sb.AppendLine("===============================================================");
            sb.AppendLine($"DEBUG LOG: {result.EngineName} @ {result.TestCaseName}");
            sb.AppendLine("===============================================================");
            sb.AppendLine();
            sb.AppendLine("---------- WZORZEC (Z PLIKU .JSON) ----------");
            sb.AppendLine($"Optimal Score: {optimalScore:F4}");
            sb.AppendLine($"DlugoscCiaguPoczatkowego: {standardMetrics.DlugoscCiaguPoczatkowego}");
            sb.AppendLine($"LiczbaDniObsadzonych: {standardMetrics.LiczbaDniObsadzonych}");
            sb.AppendLine($"WskaznikSprawiedliwosci: {standardMetrics.WskaznikSprawiedliwosci:F10}");
            sb.AppendLine($"WskaznikRownomiernosci: {standardMetrics.WskaznikRownomiernosci:F10}");
            sb.AppendLine($"ZrealizowaneBardzoChce: {standardMetrics.ZrealizowaneBardzoChce}");
            sb.AppendLine($"ZrealizowaneChce: {standardMetrics.ZrealizowaneChce}");
            sb.AppendLine($"ZrealizowaneMoge: {standardMetrics.ZrealizowaneMoge}");
            sb.AppendLine();
            sb.AppendLine("---------- WYNIK SOLVERA (Z TESTU) ----------");
            sb.AppendLine($"Solver Score: {solverScore:F4}");
            sb.AppendLine($"DlugoscCiaguPoczatkowego: {solverMetrics.DlugoscCiaguPoczatkowego}");
            sb.AppendLine($"LiczbaDniObsadzonych: {solverMetrics.LiczbaDniObsadzonych}");
            sb.AppendLine($"WskaznikSprawiedliwosci: {solverMetrics.WskaznikSprawiedliwosci:F10}");
            sb.AppendLine($"WskaznikRownomiernosci: {solverMetrics.WskaznikRownomiernosci:F10}");
            sb.AppendLine($"ZrealizowaneBardzoChce: {solverMetrics.ZrealizowaneBardzoChce}");
            sb.AppendLine($"ZrealizowaneChce: {solverMetrics.ZrealizowaneChce}");
            sb.AppendLine($"ZrealizowaneMoge: {solverMetrics.ZrealizowaneMoge}");
            sb.AppendLine();

            File.AppendAllText(_debugLogPath, sb.ToString());
        }

        private void HandleTimeout(BenchmarkResult result, Stopwatch stopwatch, RozwiazanyGrafik? solverResult, GrafikWejsciowy testData, RozwiazanyGrafik? optimalStandard, List<SolverPriority> selectedPriorities)
        {
            if (stopwatch.IsRunning) stopwatch.Stop();
            result.Status = "Przekroczono limit czasu";
            result.ExecutionTime = "> 60000";

            if (solverResult != null && solverResult.Przypisania.Any() && optimalStandard != null)
            {
                double optimalScore = EvaluationAndScoringService.CalculateScore(optimalStandard, selectedPriorities, testData);
                double solverScore = EvaluationAndScoringService.CalculateScore(solverResult, selectedPriorities, testData);
                if (optimalScore > 0)
                {
                    double quality = (solverScore / optimalScore) * 100.0;
                    result.QualityScore = $"{quality:F2}% (nieoptymalny)";
                }
            }
            else
            {
                result.QualityScore = "Brak danych";
                result.QualityScoreDetails = "(test przerwany)";
            }
            result.Progress = 0;
        }

        private void HandleGenericError(BenchmarkResult result, Stopwatch stopwatch, Exception ex)
        {
            if (stopwatch.IsRunning) stopwatch.Stop();
            result.Status = $"Błąd: {ex.GetType().Name}";
            result.ExecutionTime = "-";
            result.QualityScore = "Brak danych";
            result.Progress = 0;
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Pliki CSV (*.csv)|*.csv|Pliki tekstowe (*.txt)|*.*",
                FileName = $"Benchmark_{DateTime.Now:yyyyMMdd_HHmm}.csv",
                Title = "Zapisz wyniki benchmarku"
            };

            if (dialog.ShowDialog() == true)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Wyniki Benchmarku - Profil: {SelectedPriorityProfile.Name}");
                sb.AppendLine($"Data: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("Silnik;Scenariusz;Czas (ms);Zgodność z wzorcem (%);Status");

                foreach (var r in Results)
                {
                    sb.AppendLine($"\"{r.EngineName}\";\"{r.TestCaseName}\";\"{r.ExecutionTime}\";\"{r.QualityScore}\";\"{r.Status}\"");
                }

                try
                {
                    File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                    MessageBox.Show($"Wyniki zapisano do pliku:\n{dialog.FileName}", "Eksport zakończony", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Wystąpił błąd podczas zapisu pliku:\n{ex.Message}", "Błąd eksportu", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
