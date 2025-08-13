using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace GrafikWPF
{
    public partial class WyborAlgorytmuWindow : Window, INotifyPropertyChanged
    {
        public class AlgorithmChoice : INotifyPropertyChanged
        {
            public SolverType Type { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public object DescriptionHeader { get; set; } = new TextBlock();

            private bool _isSelected;
            public bool IsSelected
            {
                get => _isSelected;
                set { _isSelected = value; OnPropertyChanged(); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            protected void OnPropertyChanged([CallerMemberName] string? name = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        public List<AlgorithmChoice> AlgorithmOptions { get; set; }
        public List<AlgorithmInfo> ComparisonData { get; set; }
        public SolverType SelectedAlgorithm => AlgorithmOptions.First(o => o.IsSelected).Type;

        private AlgorithmChoice? _activeDescription;
        public AlgorithmChoice? ActiveDescription
        {
            get => _activeDescription;
            set { _activeDescription = value; OnPropertyChanged(nameof(ActiveDescription)); }
        }

        public WyborAlgorytmuWindow(SolverType currentSolver)
        {
            InitializeComponent();
            this.DataContext = this;

            AlgorithmOptions = new List<AlgorithmChoice>
            {
                new AlgorithmChoice
                {
                    Type = SolverType.Backtracking,
                    Name = "BacktrackingSolver",
                    DescriptionHeader = CreateHeader("BacktrackingSolver", "algorytm z nawrotami"),
                    Description = "Działa jak skrupulatny detektyw w labiryncie. Systematycznie, krok po kroku, podąża jedną ścieżką, przypisując dyżury dzień po dniu. Gdy trafia w ślepy zaułek (sytuację, w której nie da się obsadzić dyżuru zgodnie z regułami), powraca (backtrack) do ostatniego skrzyżowania i próbuje innej drogi. Dzięki wbudowanym mechanizmom 'przycinania' (pruning), potrafi z góry odrzucać całe korytarze, które na pewno nie doprowadzą do lepszego rozwiązania niż już znalezione. Gwarantuje znalezienie idealnego rozwiązania, ale musi 'fizycznie' przejść każdą obiecującą ścieżkę."
                },
                new AlgorithmChoice
                {
                    Type = SolverType.AStar,
                    Name = "AStarSolver",
                    DescriptionHeader = CreateHeader("AStarSolver", "wielokryterialny algorytm A*"),
                    Description = "Można go porównać do doświadczonego nawigatora z mapą i kompasem, który szuka najkrótszej drogi do celu. W każdym momencie analizuje nie tylko już przebytą drogę, ale również inteligentnie szacuje odległość, jaka jeszcze pozostała do końca. Zamiast ślepo badać wszystkie ścieżki jak Backtracking, A* koncentruje swoje wysiłki na tych, które wydają się najbardziej obiecujące. Dzięki temu potrafi znaleźć optymalną trasę znacznie szybciej, omijając wiele niepotrzebnych dróg."
                },
                new AlgorithmChoice
                {
                    Type = SolverType.Genetic,
                    Name = "GeneticSolver",
                    DescriptionHeader = CreateHeader("GeneticSolver", "algorytm genetyczny"),
                    Description = "Inspirowany teorią ewolucji Karola Darwina. Algorytm tworzy początkową 'populację' losowych grafików, a następnie poddaje ją procesowi naturalnej selekcji przez wiele pokoleń. Najlepsze grafiki ('osobniki') są ze sobą 'krzyżowane', wymieniając swoje fragmenty i tworząc nowe 'potomstwo'. Dodatkowo, wprowadzane są losowe 'mutacje', które zapewniają różnorodność. Z pokolenia na pokolenie słabsze rozwiązania są eliminowane, a populacja jako całość staje się coraz 'silniejsza', dążąc do wyłonienia niemal idealnego grafiku."
                },
                new AlgorithmChoice
                {
                    Type = SolverType.SimulatedAnnealing,
                    Name = "SimulatedAnnealingSolver",
                    DescriptionHeader = CreateHeader("SimulatedAnnealingSolver", "algorytm symulowanego wyżarzania"),
                    Description = "Naśladuje proces powolnego studzenia (wyżarzania) metalu, aby uzyskać jego idealną, krystaliczną strukturę. Algorytm zaczyna od wysokiej 'temperatury', na której chętnie akceptuje nawet gorsze modyfikacje grafiku, co pozwala mu na swobodne 'przeskakiwanie' między różnymi typami rozwiązań i unikanie utknięcia w pierwszym znalezionym optimum. W miarę jak 'temperatura' powoli opada, algorytm staje się coraz bardziej 'wybredny' i akceptuje już tylko zmiany, które faktycznie poprawiają wynik. Ten kontrolowany 'chaos' na początku i 'precyzja' na końcu pozwala skutecznie znaleźć bardzo dobre rozwiązania."
                },
                new AlgorithmChoice
                {
                    Type = SolverType.TabuSearch,
                    Name = "TabuSearchSolver",
                    DescriptionHeader = CreateHeader("TabuSearchSolver", "algorytm przeszukiwania z zakazami"),
                    Description = "Wyobraź sobie eksploratora, który notuje w dzienniku ostatnio odwiedzone miejsca, by do nich od razu nie wracać. Ten algorytm w każdym kroku rozgląda się za najlepszą możliwą modyfikacją grafiku w swoim 'sąsiedztwie'. Co ważne, dokona tej zmiany nawet, jeśli chwilowo pogorszy to ogólny wynik. Aby uniknąć zapętlenia się i ciągłego wracania do tych samych rozwiązań, prowadzi 'listę tabu' – krótkoterminową pamięć ruchów, które są tymczasowo 'zakazane'. Pozwala to na wydostanie się z lokalnych optimów i zbadanie szerszego obszaru potencjalnych grafików."
                },
                new AlgorithmChoice
                {
                    Type = SolverType.AntColony,
                    Name = "AntColonySolver",
                    DescriptionHeader = CreateHeader("AntColonySolver", "algorytam kolonii mrówek"),
                    Description = "Działa w oparciu o obserwację kolonii mrówek poszukującej najkrótszej drogi do pożywienia. Wiele wirtualnych 'mrówek' jednocześnie i niezależnie od siebie buduje kompletne grafiki. Każda mrówka, która stworzy dobrej jakości grafik, zostawia za sobą cyfrowy 'ślad feromonowy' na fragmentach, z których korzystała (np. 'przypisanie lekarza X do dnia Y jest dobrym pomysłem'). Kolejne 'pokolenia' mrówek są przyciągane do silniejszych śladów feromonowych, co naturalnie wzmacnia najlepsze elementy i prowadzi całą 'kolonię' do bardzo szybkiego znalezienia optymalnego rozwiązania."
                }
            };

            foreach (var option in AlgorithmOptions)
            {
                option.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(AlgorithmChoice.IsSelected) && option.IsSelected)
                    {
                        ActiveDescription = option;
                    }
                };
            }

            var selectedOption = AlgorithmOptions.FirstOrDefault(o => o.Type == currentSolver) ?? AlgorithmOptions.First();
            selectedOption.IsSelected = true;

            ComparisonData = new List<AlgorithmInfo>
            {
                new AlgorithmInfo("BacktrackingSolver", "Przeszukiwanie zupełne", "Deterministyczny", "Nie", "Gwarancja znalezienia wyniku najlepszego z możliwych.", "Od b. krótkiego do astronomicznie długiego", "Niskie"),
                new AlgorithmInfo("AStarSolver", "Przeszukiwanie heurystyczne", "Deterministyczny", "Nie", "Wynik bardzo wysokiej jakości (bez gwarancji optimum).", "Krótki / Średni", "Wysokie"),
                new AlgorithmInfo("GeneticSolver", "Metaheurystyka ewolucyjna", "Stochastyczny", "Tak", "Wynik bardzo wysokiej jakości, bliski najlepszemu.", "Krótki", "Średnie / Wysokie"),
                new AlgorithmInfo("SimulatedAnnealingSolver", "Metaheurystyka", "Stochastyczny", "Nie", "Wynik zazwyczaj bardzo dobry, lecz bez gwarancji bliskości do wyniku najlepszego z możliwych.", "Średni", "Niskie"),
                new AlgorithmInfo("TabuSearchSolver", "Metaheurystyka", "Stochastyczny", "Nie", "Wynik bardzo wysokiej jakości, bliski najlepszemu.", "Krótki", "Średnie"),
                new AlgorithmInfo("AntColonySolver", "Metaheurystyka (inteligencja rozproszona)", "Stochastyczny", "Tak", "Wynik bardzo wysokiej jakości, bliski najlepszemu.", "Krótki", "Wysokie")
            };
        }

        private TextBlock CreateHeader(string name, string description)
        {
            var tb = new TextBlock();
            tb.Inlines.Add(new Bold(new Run(name)));
            tb.Inlines.Add(new Run($" ({description})"));
            return tb;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }

        private void AlgorithmLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AlgorithmChoice choice)
            {
                choice.IsSelected = true;
            }
        }

        private void BenchmarkButton_Click(object sender, RoutedEventArgs e)
        {
            var benchmarkWindow = new BenchmarkWindow
            {
                Owner = this
            };
            benchmarkWindow.ShowDialog();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}