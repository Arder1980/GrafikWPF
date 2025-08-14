using ClosedXML.Excel;
using Microsoft.Win32;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GrafikWPF
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private bool _isCurrentViewLockedByDefault = false;
        private record PreparedData(DataTable DataTable, Dictionary<string, int> Limity, List<Lekarz> LekarzeAktywni, RozwiazanyGrafik? ZapisanyGrafik);

        private const int MAKS_KOLUMN_LEKARZY = 20;
        private const int GLOBAL_TIMEOUT_SECONDS = 600;
        private readonly List<DataGridTemplateColumn> _doctorColumns = new();
        private Button? _settingsButton;
        private DispatcherTimer _countdownTimer;
        private CancellationTokenSource? _cts;
        private bool _isManualCancellation = false;

        // ====== POSTĘP: zegar, watchdog i „soft-lift” ======
        private readonly Stopwatch _progressClock = new Stopwatch();
        private long _lastProgressUiTick = 0;
        private double _lastProgressPercent = 0;
        private DispatcherTimer? _progressWatchdog;
        private long _lastProgressEventTick = 0;
        private long _lastWatchdogTick = 0;
        private const double PROGRESS_STALE_SECONDS = 1.0;   // po tylu sekundach ciszy włącz dosuw
        private const double SOFT_LIFT_RATE_PER_SEC = 1.2;   // tempo dosuwu (pp/s)

        private Dictionary<string, int> _limityDyzurow = new();
        private readonly Dictionary<string, TypDostepnosci> _mapaNazwDostepnosci;
        private readonly Dictionary<TypDostepnosci, string> _mapaDostepnosciDoNazw;
        public List<string> OpcjeDostepnosci { get; }

        private DataTable _grafikDataTable = new();
        private bool _isInitializing = true;
        private bool _grafikZostalWygenerowany = false;
        private TextBlock? _maksDyzurTextBlock;

        private Button? _generateButton;
        private string? _aktualnyKluczMiesiaca;

        private bool _isChangingMonth = false;

        #region INotifyPropertyChanged Properties

        public string NazwaOddzialuInfo
        {
            get { return DataManager.AppData.NazwaOddzialu; }
        }
        public string NazwaSzpitalaInfo
        {
            get { return DataManager.AppData.NazwaSzpitala; }
        }
        public string DynamicWindowTitle
        {
            get { return $":: Grafikomat dyżurowy :: {NazwaOddzialuInfo}, {NazwaSzpitalaInfo}"; }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); }
        }

        private bool _isGenerating;
        public bool IsGenerating
        {
            get => _isGenerating;
            set { _isGenerating = value; OnPropertyChanged(); }
        }

        private string _busyMessage = "Przetwarzanie...";
        public string BusyMessage
        {
            get => _busyMessage;
            set { _busyMessage = value; OnPropertyChanged(); }
        }

        private double _generationProgress;
        public double GenerationProgress
        {
            get => _generationProgress;
            set { _generationProgress = value; OnPropertyChanged(); }
        }

        private string? _countdownMessage;
        public string? CountdownMessage
        {
            get => _countdownMessage;
            set { _countdownMessage = value; OnPropertyChanged(); }
        }

        private bool _isDeterministicSolverRunning;
        public bool IsDeterministicSolverRunning
        {
            get => _isDeterministicSolverRunning;
            set { _isDeterministicSolverRunning = value; OnPropertyChanged(); }
        }

        private string _deterministicSolverName = "";
        public string DeterministicSolverName
        {
            get => _deterministicSolverName;
            set { _deterministicSolverName = value; OnPropertyChanged(); }
        }

        private string? _selectedEngineInfo;
        public string? SelectedEngineInfo
        {
            get => _selectedEngineInfo;
            set { _selectedEngineInfo = value; OnPropertyChanged(); }
        }

        private string? _priorityOrderInfo;
        public string? PriorityOrderInfo
        {
            get => _priorityOrderInfo;
            set { _priorityOrderInfo = value; OnPropertyChanged(); }
        }

        private bool _isProgressIndeterminate;
        public bool IsProgressIndeterminate
        {
            get => _isProgressIndeterminate;
            set { _isProgressIndeterminate = value; OnPropertyChanged(); }
        }

        private string? _etaMessage;
        public string? EtaMessage
        {
            get => _etaMessage;
            set { _etaMessage = value; OnPropertyChanged(); }
        }

        private bool _showCountdown;
        public bool ShowCountdown
        {
            get => _showCountdown;
            set { _showCountdown = value; OnPropertyChanged(); }
        }

        private bool _showEta;
        public bool ShowEta
        {
            get => _showEta;
            set { _showEta = value; OnPropertyChanged(); }
        }
        #endregion

        public MainWindow()
        {
            InitializeComponent();
            QuestPDF.Settings.License = LicenseType.Community;

            _countdownTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            _mapaNazwDostepnosci = new Dictionary<string, TypDostepnosci>
            {
                { "---", TypDostepnosci.Niedostepny }, { "Mogę", TypDostepnosci.Moge },
                { "Chcę", TypDostepnosci.Chce }, { "Bardzo chcę", TypDostepnosci.BardzoChce },
                { "Rezerwacja", TypDostepnosci.Rezerwacja },
                { "Urlop", TypDostepnosci.Urlop }, { "Dyżur (inny)", TypDostepnosci.DyzurInny },
                { "Mogę warunkowo", TypDostepnosci.MogeWarunkowo }
            };
            _mapaDostepnosciDoNazw = _mapaNazwDostepnosci.ToDictionary(kp => kp.Value, kp => kp.Key);

            OpcjeDostepnosci = new List<string>
            {
                "---", "Mogę", "Chcę", "Bardzo chcę", "Rezerwacja",
                "Mogę warunkowo", "Dyżur (inny)", "Urlop"
            };

            InicjalizujStaleKolumnySiatki();

            GrafikGrid.LoadingRow += DataGrid_LoadingRow_Styling;
            this.Loaded += Window_Loaded;
            this.Closing += Window_Closing;
            this.DataContext = this;

            _progressClock.Start();
        }

        private void RefreshDynamicTitles()
        {
            OnPropertyChanged(nameof(NazwaOddzialuInfo));
            OnPropertyChanged(nameof(NazwaSzpitalaInfo));
            OnPropertyChanged(nameof(DynamicWindowTitle));
        }

        private void UpdateSolverInfo()
        {
            var descriptions = DataManager.AppData.KolejnoscPriorytetowSolvera
                .Select(p => PrioritiesWindow.GetEnumDescription(p));

            var algorytmName = DataManager.AppData.WybranyAlgorytm.ToString() + "Solver";

            SelectedEngineInfo = $"Wybrany silnik: {algorytmName}";
            PriorityOrderInfo = $"Kolejność priorytetów: {string.Join(" > ", descriptions)}";
        }

        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            await ZapiszBiezacyMiesiac();
            DataManager.SaveData();
        }

        private void Window_Loaded(object? sender, RoutedEventArgs e)
        {
            _isInitializing = true;
            InicjalizujWyborDaty();
            RefreshDynamicTitles();
            _isInitializing = false;
            _ = Dispatcher.BeginInvoke(new Action(async () => await ReloadViewAsync()), DispatcherPriority.ContextIdle);
        }

        private async void DateSelection_Changed(object? sender, SelectionChangedEventArgs? e)
        {
            if (_isInitializing || _isChangingMonth || e?.OriginalSource is not ComboBox)
            {
                return;
            }

            _isChangingMonth = true;

            try
            {
                await ZapiszBiezacyMiesiac();
                await ReloadViewAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Wystąpił nieoczekiwany błąd podczas zmiany miesiąca. Aplikacja może być w niestabilnym stanie.\n\nSzczegóły techniczne: {ex.Message}",
                                "Błąd krytyczny",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
            finally
            {
                _isChangingMonth = false;
            }
        }

        private async Task ReloadViewAsync()
        {
            BusyMessage = "Odświeżanie widoku...";
            IsGenerating = false;
            IsProgressIndeterminate = true;
            IsBusy = true;

            try
            {
                if (YearComboBox.SelectedItem == null || MonthComboBox.SelectedItem == null) return;

                int rok = (int)YearComboBox.SelectedItem;
                int miesiac = MonthComboBox.SelectedIndex + 1;

                await Dispatcher.InvokeAsync(() =>
                {
                    var dzis = DateTime.Today;
                    var wybranaData = new DateTime(rok, miesiac, 1);

                    _isCurrentViewLockedByDefault = (wybranaData.Year < dzis.Year) || (wybranaData.Year == dzis.Year && wybranaData.Month <= dzis.Month);

                    bool shouldCheckboxBeVisible = (wybranaData.Year == dzis.Year && wybranaData.Month == dzis.Month);
                    UnlockEditCheckBox.IsChecked = false;
                    UnlockEditCheckBox.Visibility = shouldCheckboxBeVisible ? Visibility.Visible : Visibility.Collapsed;

                    UpdateSolverInfo();
                });

                var preparedData = await WygenerujDaneWtle(rok, miesiac);

                await Dispatcher.InvokeAsync(() =>
                {
                    _limityDyzurow = preparedData.Limity;
                    _grafikDataTable = preparedData.DataTable;
                    _grafikZostalWygenerowany = preparedData.ZapisanyGrafik != null;

                    GrafikGrid.ItemsSource = null;
                });

                await Dispatcher.InvokeAsync(async () =>
                {
                    await WypelnijNaglowkiDanymiAsync(preparedData.LekarzeAktywni);
                    AktualizujWidokSiatki(preparedData.LekarzeAktywni);
                });

                await Dispatcher.InvokeAsync(() => {
                    GrafikGrid.ItemsSource = _grafikDataTable.DefaultView;
                });

                if (_grafikZostalWygenerowany)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        WyswietlWynikWGrid(preparedData.ZapisanyGrafik!);
                    });
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateLayoutAndText();
                    SetReadOnlyState();
                }, DispatcherPriority.ContextIdle);

            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ZapiszBiezacyMiesiac()
        {
            if (string.IsNullOrEmpty(_aktualnyKluczMiesiaca)) return;

            if (!DataManager.AppData.DaneGrafikow.ContainsKey(_aktualnyKluczMiesiaca))
            {
                DataManager.AppData.DaneGrafikow[_aktualnyKluczMiesiaca] = new DaneMiesiaca();
            }
            var daneMiesiaca = DataManager.AppData.DaneGrafikow[_aktualnyKluczMiesiaca];
            daneMiesiaca.LimityDyzurow = new Dictionary<string, int>(_limityDyzurow);

            if (daneMiesiaca.SymboleLekarzyAktywnychWMiesiacu == null || !daneMiesiaca.SymboleLekarzyAktywnychWMiesiacu.Any())
            {
                daneMiesiaca.SymboleLekarzyAktywnychWMiesiacu = DataManager.AppData.WszyscyLekarze
                    .Where(l => l.IsAktywny)
                    .Select(l => l.Symbol)
                    .ToList();
            }

            var dostepnosc = new Dictionary<DateTime, Dictionary<string, TypDostepnosci>>();
            await Task.Run(() =>
            {
                if (_grafikDataTable == null || _grafikDataTable.Rows.Count == 0) return;

                var symboleAktywnych = daneMiesiaca.SymboleLekarzyAktywnychWMiesiacu ?? new List<string>();
                var lekarzeAktywniDoZapisu = DataManager.AppData.WszyscyLekarze
                                                .Where(l => symboleAktywnych.Contains(l.Symbol))
                                                .OrderBy(l => l.Nazwisko).ThenBy(l => l.Imie)
                                                .ToList();

                foreach (DataRow row in _grafikDataTable.Rows)
                {
                    var data = (DateTime)row["FullDate"];
                    var wpisyDnia = new Dictionary<string, TypDostepnosci>();

                    for (int i = 0; i < lekarzeAktywniDoZapisu.Count; i++)
                    {
                        var lekarz = lekarzeAktywniDoZapisu[i];
                        string nazwaKolumny = $"Lekarz_{i}";
                        if (_grafikDataTable.Columns.Contains(nazwaKolumny))
                        {
                            _mapaNazwDostepnosci.TryGetValue(row[nazwaKolumny].ToString() ?? "---", out var typ);
                            wpisyDnia[lekarz.Symbol] = typ;
                        }
                    }
                    dostepnosc[data] = wpisyDnia;
                }
            });
            daneMiesiaca.Dostepnosc = dostepnosc;
        }

        private async Task<PreparedData> WygenerujDaneWtle(int rok, int miesiac)
        {
            return await Task.Run(() =>
            {
                _aktualnyKluczMiesiaca = $"{rok:D4}-{miesiac:D2}";

                List<Lekarz> lekarzeAktywni;
                DataTable dt;
                Dictionary<string, int> limity;
                RozwiazanyGrafik? zapisanyGrafik = null;

                var dzis = DateTime.Today;
                var wybranaData = new DateTime(rok, miesiac, 1);
                bool czyMiesiacArchiwalny = (wybranaData.Year < dzis.Year) || (wybranaData.Year == dzis.Year && wybranaData.Month < dzis.Month);

                bool daneMiesiacaIstnieja = DataManager.AppData.DaneGrafikow.TryGetValue(_aktualnyKluczMiesiaca, out var daneMiesiaca);

                if (czyMiesiacArchiwalny && daneMiesiacaIstnieja && daneMiesiaca.SymboleLekarzyAktywnychWMiesiacu != null && daneMiesiaca.SymboleLekarzyAktywnychWMiesiacu.Any())
                {
                    var symboleHistoryczne = daneMiesiaca.SymboleLekarzyAktywnychWMiesiacu;
                    lekarzeAktywni = DataManager.AppData.WszyscyLekarze
                        .Where(l => symboleHistoryczne.Contains(l.Symbol))
                        .OrderBy(l => l.Nazwisko).ThenBy(l => l.Imie)
                        .ToList();

                    limity = new Dictionary<string, int>(daneMiesiaca.LimityDyzurow);
                    dt = PrzygotujSiatkeDanychZBiezacychDanych(rok, miesiac, lekarzeAktywni, daneMiesiaca);
                    zapisanyGrafik = daneMiesiaca.ZapisanyGrafik;
                }
                else
                {
                    lekarzeAktywni = DataManager.AppData.WszyscyLekarze
                        .Where(l => l.IsAktywny)
                        .OrderBy(l => l.Nazwisko).ThenBy(l => l.Imie)
                        .ToList();

                    if (daneMiesiaca != null)
                    {
                        daneMiesiaca.SymboleLekarzyAktywnychWMiesiacu = lekarzeAktywni.Select(l => l.Symbol).ToList();
                    }

                    limity = daneMiesiaca != null ? new Dictionary<string, int>(daneMiesiaca.LimityDyzurow) : lekarzeAktywni.ToDictionary(l => l.Symbol, l => 0);
                    dt = daneMiesiaca != null ? PrzygotujSiatkeDanychZBiezacychDanych(rok, miesiac, lekarzeAktywni, daneMiesiaca) : PrzygotujSiatkeDanychNowyMiesiac(rok, miesiac, lekarzeAktywni);
                    zapisanyGrafik = daneMiesiaca?.ZapisanyGrafik;
                }

                return new PreparedData(dt, limity, lekarzeAktywni, zapisanyGrafik);
            });
        }

        private DataTable StworzPustaSiatkeDanych()
        {
            var dt = new DataTable();
            dt.Columns.Add("FullDate", typeof(DateTime));
            dt.Columns.Add("Data", typeof(string));
            for (int i = 0; i < MAKS_KOLUMN_LEKARZY; i++)
            {
                dt.Columns.Add($"Lekarz_{i}", typeof(string));
            }
            dt.Columns.Add("Data_Powtorzona", typeof(string));
            dt.Columns.Add("Wynik", typeof(string));
            dt.Columns.Add("WynikLekarz", typeof(Lekarz));
            return dt;
        }

        private DataTable PrzygotujSiatkeDanychNowyMiesiac(int rok, int miesiac, List<Lekarz> lekarzeAktywni)
        {
            var dt = StworzPustaSiatkeDanych();

            int dniWMiesiacu = DateTime.DaysInMonth(rok, miesiac);
            for (int dzien = 1; dzien <= dniWMiesiacu; dzien++)
            {
                var data = new DateTime(rok, miesiac, dzien);
                var row = dt.NewRow();
                row["FullDate"] = data;
                row["Data"] = data.ToString("dd.MM (dddd)", new CultureInfo("pl-PL"));
                for (int i = 0; i < MAKS_KOLUMN_LEKARZY; i++)
                {
                    row[$"Lekarz_{i}"] = "---";
                }
                row["Data_Powtorzona"] = row["Data"];
                row["WynikLekarz"] = DBNull.Value;
                dt.Rows.Add(row);
            }
            return dt;
        }

        private DataTable PrzygotujSiatkeDanychZBiezacychDanych(int rok, int miesiac, List<Lekarz> lekarzeAktywni, DaneMiesiaca daneMiesiaca)
        {
            var dt = StworzPustaSiatkeDanych();

            int dniWMiesiacu = DateTime.DaysInMonth(rok, miesiac);
            for (int dzien = 1; dzien <= dniWMiesiacu; dzien++)
            {
                var data = new DateTime(rok, miesiac, dzien);
                var row = dt.NewRow();
                row["FullDate"] = data;
                row["Data"] = data.ToString("dd.MM (dddd)", new CultureInfo("pl-PL"));

                if (daneMiesiaca.Dostepnosc != null && daneMiesiaca.Dostepnosc.TryGetValue(data, out var dostepnosciDnia))
                {
                    for (int i = 0; i < lekarzeAktywni.Count; i++)
                    {
                        var lekarz = lekarzeAktywni[i];
                        if (dostepnosciDnia.TryGetValue(lekarz.Symbol, out var typ))
                        {
                            row[$"Lekarz_{i}"] = _mapaDostepnosciDoNazw.GetValueOrDefault(typ, "---");
                        }
                    }
                }

                row["Data_Powtorzona"] = row["Data"];
                row["WynikLekarz"] = DBNull.Value;
                dt.Rows.Add(row);
            }
            return dt;
        }

        private void InicjalizujStaleKolumnySiatki()
        {
            var staticStyle = (Style)this.TryFindResource("StaticColumnStyle");
            var centerTextStyle = (Style)this.TryFindResource("CenterVTextBlockStyle");
            var singleClickStyle = (Style)this.TryFindResource("SingleClickEditingCellStyle");

            GrafikGrid.Columns.Add(new DataGridTextColumn
            {
                Header = null,
                Binding = new Binding("[Data]"),
                IsReadOnly = true,
                Width = new DataGridLength(1.5, DataGridLengthUnitType.Star),
                MinWidth = 130,
                CellStyle = staticStyle,
                ElementStyle = centerTextStyle
            });

            for (int i = 0; i < MAKS_KOLUMN_LEKARZY; i++)
            {
                var templateColumn = new DataGridTemplateColumn
                {
                    Header = "Dyżurny",
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                    CellStyle = singleClickStyle,
                    Visibility = Visibility.Collapsed
                };

                var textFactory = new FrameworkElementFactory(typeof(TextBlock));
                textFactory.SetValue(TextBlock.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
                textFactory.SetValue(TextBlock.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
                textFactory.SetBinding(TextBlock.TextProperty, new Binding($"[Lekarz_{i}]"));
                templateColumn.CellTemplate = new DataTemplate { VisualTree = textFactory };

                var comboFactory = new FrameworkElementFactory(typeof(ComboBox));
                comboFactory.SetValue(ComboBox.HorizontalContentAlignmentProperty, System.Windows.HorizontalAlignment.Center);
                comboFactory.SetValue(ComboBox.VerticalContentAlignmentProperty, System.Windows.VerticalAlignment.Center);
                comboFactory.SetValue(ComboBox.IsDropDownOpenProperty, true);
                comboFactory.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("DataContext.OpcjeDostepnosci")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1)
                });
                comboFactory.SetBinding(ComboBox.SelectedItemProperty, new Binding($"[Lekarz_{i}]")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });
                templateColumn.CellEditingTemplate = new DataTemplate { VisualTree = comboFactory };

                _doctorColumns.Add(templateColumn);
                GrafikGrid.Columns.Add(templateColumn);
            }

            GrafikGrid.Columns.Add(new DataGridTextColumn()
            {
                Header = null,
                Binding = new Binding("[Data_Powtorzona]"),
                IsReadOnly = true,
                Width = new DataGridLength(1.5, DataGridLengthUnitType.Star),
                MinWidth = 130,
                CellStyle = staticStyle,
                ElementStyle = centerTextStyle
            });

            GrafikGrid.Columns.Add(new DataGridTextColumn()
            {
                Header = null,
                Binding = new Binding("[Wynik]"),
                IsReadOnly = true,
                Width = new DataGridLength(1.5, DataGridLengthUnitType.Star),
                MinWidth = 130,
                CellStyle = staticStyle,
                ElementStyle = centerTextStyle
            });
        }

        private void AktualizujWidokSiatki(List<Lekarz> lekarzeAktywni)
        {
            for (int i = 0; i < MAKS_KOLUMN_LEKARZY; i++)
            {
                if (i < _doctorColumns.Count)
                {
                    var column = _doctorColumns[i];
                    if (i < lekarzeAktywni.Count)
                    {
                        column.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        column.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        private void UpdateLayoutAndText()
        {
            UpdateRowHeights();
            UpdateMaksDyzurText();
            UpdateSettingsButtonText();
            UpdateResultColumnDisplay();
        }

        private void UpdateSettingsButtonText()
        {
            if (_settingsButton == null || GrafikGrid.Columns.Count == 0) return;

            double columnWidth = GrafikGrid.Columns[0].ActualWidth;
            if (columnWidth < 120)
            {
                _settingsButton.Content = "Zarz. dyżurnymi";
            }
            else
            {
                _settingsButton.Content = "Zarządzanie dyżurnymi";
            }
        }

        private void UpdateRowHeights()
        {
            if (!this.IsLoaded || GrafikGrid.Items.Count <= 0) return;
            double headerRowHeight = 35;
            DateSelectorRow.Height = new GridLength(headerRowHeight);

            if (UnifiedHeaderGrid.RowDefinitions.Count > 0)
            {
                UnifiedHeaderGrid.UpdateLayout();
            }

            double headersActualHeight = UnifiedHeaderGrid.ActualHeight;
            double footerHeight = FooterPanel.ActualHeight + FooterPanel.Margin.Top + FooterPanel.Margin.Bottom;

            double containerHeight = TableContainerGrid.ActualHeight;
            double gridRowZeroHeight = containerHeight - footerHeight;

            if (gridRowZeroHeight > headersActualHeight)
            {
                double dataGridAvailableHeight = gridRowZeroHeight - headersActualHeight - 2;
                if (dataGridAvailableHeight > 1)
                {
                    GrafikGrid.RowHeight = dataGridAvailableHeight / GrafikGrid.Items.Count;
                }
            }
        }

        private void UpdateMaksDyzurText()
        {
            if (_maksDyzurTextBlock != null && GrafikGrid.Columns.Count > 0)
            {
                double columnWidth = GrafikGrid.Columns[0].ActualWidth;
                if (columnWidth > 160) _maksDyzurTextBlock.Text = "Maksymalna ilość dyżurów";
                else if (columnWidth > 120) _maksDyzurTextBlock.Text = "Maks. ilość dyżurów";
                else _maksDyzurTextBlock.Text = "Maks. dyżurów";
            }
        }

        private void UpdateResultColumnDisplay()
        {
            if (!GrafikGrid.IsLoaded || _grafikDataTable.Rows.Count == 0 || GrafikGrid.Columns.Count < 1) return;
            var wynikColumn = GrafikGrid.Columns.Last();
            double columnWidth = wynikColumn.ActualWidth - 16;
            bool useSymbol = false;
            if (_grafikZostalWygenerowany)
            {
                foreach (DataRow row in _grafikDataTable.Rows)
                {
                    if (row["WynikLekarz"] is Lekarz lekarz)
                    {
                        var typeface = new Typeface(
                            this.FontFamily ?? SystemFonts.MessageFontFamily,
                            this.FontStyle,
                            this.FontWeight,
                            this.FontStretch);

                        var formattedText = new FormattedText(
                            lekarz.PelneImie, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                            typeface, GrafikGrid.FontSize, Brushes.Black, new NumberSubstitution(), 1);

                        if (formattedText.Width > columnWidth)
                        {
                            useSymbol = true;
                            break;
                        }
                    }
                }
            }
            foreach (DataRow row in _grafikDataTable.Rows)
            {
                if (row["WynikLekarz"] is Lekarz lekarz)
                {
                    row["Wynik"] = useSymbol ? lekarz.Symbol : lekarz.PelneImie;
                }
                else
                {
                    row["Wynik"] = _grafikZostalWygenerowany ? "--- BRAK OBSADY ---" : "";
                }
            }
        }

        private void Legenda_Click(object? sender, RoutedEventArgs e)
        {
            var legendaWindow = new LegendaWindow { Owner = this };
            legendaWindow.ShowDialog();
        }

        private void Logic_Click(object sender, RoutedEventArgs e)
        {
            var logicWindow = new LogikaSolveraWindow { Owner = this };
            logicWindow.ShowDialog();
        }

        private void Info_Click(object sender, RoutedEventArgs e)
        {
            var infoWindow = new InfoWindow { Owner = this };
            infoWindow.ShowDialog();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async void SettingsButton_Click(object? sender, RoutedEventArgs e)
        {
            await ZapiszBiezacyMiesiac();

            var settingsWindow = new UstawieniaLekarzyWindow(DataManager.AppData.WszyscyLekarze) { Owner = this };
            bool? result = settingsWindow.ShowDialog();

            if (result == true)
            {
                var updatedList = settingsWindow.ZaktualizowaniLekarze;
                DataManager.AppData.WszyscyLekarze.Clear();
                DataManager.AppData.WszyscyLekarze.AddRange(updatedList);
                await ReloadViewAsync();
            }
        }

        private void GeneralSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new UstawieniaOgolneWindow(DataManager.AppData) { Owner = this };
            var result = settingsWindow.ShowDialog();
            if (result == true)
            {
                DataManager.AppData.NazwaOddzialu = settingsWindow.NazwaOddzialu;
                DataManager.AppData.NazwaSzpitala = settingsWindow.NazwaSzpitala;
                RefreshDynamicTitles();
            }
        }

        private void PrioritiesSettings_Click(object sender, RoutedEventArgs e)
        {
            var prioritiesWindow = new PrioritiesWindow(DataManager.AppData.KolejnoscPriorytetowSolvera)
            {
                Owner = this
            };
            bool? result = prioritiesWindow.ShowDialog();
            if (result == true)
            {
                DataManager.AppData.KolejnoscPriorytetowSolvera = prioritiesWindow.NewOrder;
                UpdateSolverInfo();
            }
        }

        private void AlgorithmSettings_Click(object sender, RoutedEventArgs e)
        {
            var currentSelection = DataManager.AppData.WybranyAlgorytm;
            var dialog = new WyborAlgorytmuWindow(currentSelection) { Owner = this };

            var result = dialog.ShowDialog();

            if (result == true)
            {
                if (dialog.SelectedAlgorithm != currentSelection)
                {
                    DataManager.AppData.WybranyAlgorytm = dialog.SelectedAlgorithm;
                    UpdateSolverInfo();
                }
            }
        }

        private void InicjalizujWyborDaty()
        {
            for (int rok = DateTime.Now.Year - 5; rok <= DateTime.Now.Year + 5; rok++) YearComboBox.Items.Add(rok);
            for (int i = 1; i <= 12; i++) MonthComboBox.Items.Add(new DateTime(2000, i, 1).ToString("MMMM", new CultureInfo("pl-PL")));

            YearComboBox.SelectionChanged += DateSelection_Changed;
            MonthComboBox.SelectionChanged += DateSelection_Changed;

            var dataStartowa = DateTime.Now.AddMonths(1);
            YearComboBox.SelectedItem = dataStartowa.Year;
            MonthComboBox.SelectedIndex = dataStartowa.Month - 1;
        }

        private async Task WypelnijNaglowkiDanymiAsync(List<Lekarz> lekarzeAktywni)
        {
            UnifiedHeaderGrid.ColumnDefinitions.Clear();
            UnifiedHeaderGrid.RowDefinitions.Clear();
            UnifiedHeaderGrid.Children.Clear();

            UnifiedHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            UnifiedHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            UnifiedHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            UnifiedHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
            UnifiedHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35) });

            for (int i = 0; i < GrafikGrid.Columns.Count; i++)
            {
                var binding = new Binding($"Columns[{i}].ActualWidth") { Source = GrafikGrid };
                var colDef = new ColumnDefinition();
                BindingOperations.SetBinding(colDef, ColumnDefinition.WidthProperty, binding);
                UnifiedHeaderGrid.ColumnDefinitions.Add(colDef);
            }

            await Dispatcher.Yield(DispatcherPriority.Background);

            _settingsButton = new Button { Content = "Zarządzanie dyżurnymi", Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(2) };
            _settingsButton.Click += SettingsButton_Click;
            Grid.SetRow(_settingsButton, 0);
            Grid.SetColumn(_settingsButton, 0);
            UnifiedHeaderGrid.Children.Add(_settingsButton);

            for (int i = 0; i < lekarzeAktywni.Count; i++)
            {
                var symbolBlock = new TextBlock { Text = lekarzeAktywni[i].Symbol, FontWeight = FontWeights.Bold, FontSize = 14, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                Grid.SetRow(symbolBlock, 0);
                Grid.SetColumn(symbolBlock, i + 1);
                UnifiedHeaderGrid.Children.Add(symbolBlock);
            }

            await Dispatcher.Yield(DispatcherPriority.Background);

            var borderBrush = Brushes.DarkGray;
            var separator = new Border { BorderBrush = borderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(separator, 1);
            Grid.SetColumnSpan(separator, UnifiedHeaderGrid.ColumnDefinitions.Count);
            UnifiedHeaderGrid.Children.Add(separator);

            _maksDyzurTextBlock = new TextBlock { Text = "Maks. dyżurów", FontWeight = FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            Grid.SetRow(_maksDyzurTextBlock, 2);
            Grid.SetColumn(_maksDyzurTextBlock, 0);
            UnifiedHeaderGrid.Children.Add(_maksDyzurTextBlock);

            for (int i = 0; i < lekarzeAktywni.Count; i++)
            {
                var textBox = new TextBox
                {
                    Text = _limityDyzurow.GetValueOrDefault(lekarzeAktywni[i].Symbol, 0).ToString(),
                    Tag = lekarzeAktywni[i],
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                    Width = 45,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Margin = new Thickness(5, 4, 5, 4),
                    Padding = new Thickness(0, 1, 0, 1)
                };
                textBox.LostFocus += LimitTextBox_LostFocus;
                textBox.PreviewTextInput += LimitTextBox_PreviewTextInput;
                Grid.SetRow(textBox, 2);
                Grid.SetColumn(textBox, i + 1);
                UnifiedHeaderGrid.Children.Add(textBox);
            }

            await Dispatcher.Yield(DispatcherPriority.Background);

            int resultColIndex = 1 + MAKS_KOLUMN_LEKARZY + 1;
            _generateButton = new Button { Content = "Generuj Grafik", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(2) };
            _generateButton.Click += new RoutedEventHandler(GenerateButton_Click);
            Grid.SetRow(_generateButton, 2);
            Grid.SetColumn(_generateButton, resultColIndex);
            UnifiedHeaderGrid.Children.Add(_generateButton);

            var mainBarBackground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(79, 79, 79));
            var borderThickness = new Thickness(0, 1, 1, 0);
            var newPadding = new Thickness(0, 8, 0, 8);

            var rowBackground = new Border { Background = mainBarBackground, BorderBrush = borderBrush, BorderThickness = new Thickness(1, 1, 1, 0) };
            Grid.SetRow(rowBackground, 4);
            Grid.SetColumnSpan(rowBackground, UnifiedHeaderGrid.ColumnDefinitions.Count);
            UnifiedHeaderGrid.Children.Add(rowBackground);

            var mainDataHeader = new TextBlock { Text = "Data", FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = System.Windows.VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Padding = newPadding };
            var mainDataHeaderBorder = new Border { Child = mainDataHeader, BorderBrush = borderBrush, BorderThickness = borderThickness };
            Grid.SetRow(mainDataHeaderBorder, 4);
            Grid.SetColumn(mainDataHeaderBorder, 0);
            UnifiedHeaderGrid.Children.Add(mainDataHeaderBorder);

            if (lekarzeAktywni.Any())
            {
                var mainDeklaracjeHeader = new TextBlock { Text = "Deklaracje dostępności dyżurnych", FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = System.Windows.VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Padding = newPadding };
                var mainDeklaracjeHeaderBorder = new Border { Child = mainDeklaracjeHeader, BorderBrush = borderBrush, BorderThickness = borderThickness };
                Grid.SetRow(mainDeklaracjeHeaderBorder, 4);
                Grid.SetColumn(mainDeklaracjeHeaderBorder, 1);
                Grid.SetColumnSpan(mainDeklaracjeHeaderBorder, lekarzeAktywni.Count);
                UnifiedHeaderGrid.Children.Add(mainDeklaracjeHeaderBorder);
            }

            int secondDateColIndex = 1 + MAKS_KOLUMN_LEKARZY;
            var secondDataHeader = new TextBlock { Text = "Data", FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = System.Windows.VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Padding = newPadding };
            var secondDataHeaderBorder = new Border { Child = secondDataHeader, BorderBrush = borderBrush, BorderThickness = borderThickness };
            Grid.SetRow(secondDataHeaderBorder, 4);
            Grid.SetColumn(secondDataHeaderBorder, secondDateColIndex);
            UnifiedHeaderGrid.Children.Add(secondDataHeaderBorder);

            var mainWynikHeader = new TextBlock { Text = "Wynik Grafiku", FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = System.Windows.VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Padding = newPadding };
            var mainWynikHeaderBorder = new Border { Child = mainWynikHeader, BorderBrush = borderBrush, BorderThickness = borderThickness };
            Grid.SetRow(mainWynikHeaderBorder, 4);
            Grid.SetColumn(mainWynikHeaderBorder, resultColIndex);
            UnifiedHeaderGrid.Children.Add(mainWynikHeaderBorder);
        }

        private void LimitTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.Tag is Lekarz lekarz)
            {
                if (int.TryParse(textBox.Text, out int nowyLimit))
                {
                    if (YearComboBox.SelectedItem is int rok && MonthComboBox.SelectedIndex != -1)
                    {
                        int miesiac = MonthComboBox.SelectedIndex + 1;
                        int dniWMiesiacu = DateTime.DaysInMonth(rok, miesiac);
                        if (nowyLimit > dniWMiesiacu)
                        {
                            MessageBox.Show("Limit dyżurów nie może przekraczać liczby dni w miesiącu.", "Nierealny limit", MessageBoxButton.OK, MessageBoxImage.Information);
                            textBox.Text = _limityDyzurow.GetValueOrDefault(lekarz.Symbol, 0).ToString();
                        }
                        else
                        {
                            _limityDyzurow[lekarz.Symbol] = nowyLimit;
                        }
                    }
                }
                else
                {
                    MessageBox.Show("Proszę wpisać poprawną liczbę.", "Błąd");
                    textBox.Text = _limityDyzurow.GetValueOrDefault(lekarz.Symbol, 0).ToString();
                }
            }
        }

        private void DataGridCell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridCell cell && !cell.IsEditing && !cell.IsReadOnly)
            {
                if (!cell.IsFocused) cell.Focus();
                if (FindVisualParent<DataGrid>(cell) is DataGrid dataGrid) dataGrid.BeginEdit(e);
            }
        }

        private async void GenerateButton_Click(object? sender, RoutedEventArgs e)
        {
            var stopwatch = new Stopwatch();
            _isManualCancellation = false;
            await ZapiszBiezacyMiesiac();
            if (string.IsNullOrEmpty(_aktualnyKluczMiesiaca) || !DataManager.AppData.DaneGrafikow.ContainsKey(_aktualnyKluczMiesiaca))
            {
                MessageBox.Show("Brak danych wejściowych dla bieżącego miesiąca. Nie można wygenerować grafiku.", "Brak Danych", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_limityDyzurow.Values.Any(limit => limit > 0))
            {
                MessageBox.Show("Żaden z aktywnych lekarzy nie ma określonego limitu dyżurów (wszystkie limity wynoszą 0). Aby wygenerować grafik, przynajmniej jeden lekarz musi mieć limit większy od zera.", "Brak limitów", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BusyMessage = "Generowanie grafiku...";
            GenerationProgress = 0;
            IsProgressIndeterminate = false;

            // reset stanu postępu
            _lastProgressPercent = 0;
            _lastProgressUiTick = 0;
            _lastProgressEventTick = _progressClock.ElapsedTicks;
            _lastWatchdogTick = _lastProgressEventTick;

            var selectedSolver = DataManager.AppData.WybranyAlgorytm;
            IsDeterministicSolverRunning = selectedSolver == SolverType.Backtracking || selectedSolver == SolverType.AStar;
            DeterministicSolverName = selectedSolver.ToString() + "Solver";

            bool isDeterministic = selectedSolver == SolverType.Backtracking || selectedSolver == SolverType.AStar;
            ShowCountdown = isDeterministic;
            ShowEta = !isDeterministic;

            IsGenerating = true;
            IsBusy = true;

            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(GLOBAL_TIMEOUT_SECONDS));
            int countdown = GLOBAL_TIMEOUT_SECONDS;
            EventHandler? tickHandler = null;
            tickHandler = (s, args) =>
            {
                countdown--;
                if (countdown >= 0)
                {
                    TimeSpan time = TimeSpan.FromSeconds(countdown);
                    CountdownMessage = $"Automatyczne przerwanie za: {time:mm\\:ss}";
                }
            };

            _countdownTimer.Tick += tickHandler;
            TimeSpan initialTime = TimeSpan.FromSeconds(countdown);
            CountdownMessage = $"Automatyczne przerwanie za: {initialTime:mm\\:ss}";
            _countdownTimer.Start();

            // watchdog co 250 ms – miękki dosuw w zastoju
            _progressWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _progressWatchdog.Tick += ProgressWatchdog_Tick;
            _progressWatchdog.Start();

            // postęp: nie resetuj zegara przy tym samym %
            var progress = new Progress<double>(p =>
            {
                double pctRaw = Math.Max(0, Math.Min(100, p * 100.0));
                if (pctRaw < _lastProgressPercent)
                    pctRaw = _lastProgressPercent;

                bool realAdvance = pctRaw > _lastProgressPercent + 0.01; // 0.01 pp histerezy

                if (realAdvance)
                {
                    _lastProgressEventTick = _progressClock.ElapsedTicks;
                    _lastProgressPercent = pctRaw;
                    GenerationProgress = pctRaw;
                }
                else
                {
                    var nowTicks = _progressClock.ElapsedTicks;
                    var minIntervalTicks = Stopwatch.Frequency / 5; // ≤5 Hz
                    if (nowTicks - _lastProgressUiTick >= minIntervalTicks)
                    {
                        _lastProgressUiTick = nowTicks;
                        GenerationProgress = _lastProgressPercent;
                    }
                }

                if (ShowEta && p > 0.01)
                {
                    var elapsed = stopwatch.Elapsed;
                    if (elapsed.TotalMilliseconds > 0)
                    {
                        var totalEstimated = TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds / p);
                        var remaining = totalEstimated - elapsed;
                        if (remaining.TotalSeconds > 0)
                        {
                            EtaMessage = $"Szacowany czas do końca: {remaining:mm\\:ss}";
                        }
                    }
                }
            });

            RozwiazanyGrafik? wynik = null;
            try
            {
                wynik = await Task.Run(() =>
                {
                    stopwatch.Start();
                    var lekarzeAktywni = DataManager.AppData.WszyscyLekarze.Where(l => l.IsAktywny).ToList();
                    var dostepnosc = DataManager.AppData.DaneGrafikow[_aktualnyKluczMiesiaca].Dostepnosc;
                    var daneDoSilnika = new GrafikWejsciowy { Lekarze = lekarzeAktywni, Dostepnosc = dostepnosc, LimityDyzurow = _limityDyzurow };
                    var solver = new ReservationSolverWrapper(selectedSolver, daneDoSilnika, DataManager.AppData.KolejnoscPriorytetowSolvera, progress, _cts.Token);

                    return solver.ZnajdzOptymalneRozwiazanie();
                }, _cts.Token);

                if (wynik != null)
                {
                    DataManager.AppData.DaneGrafikow[_aktualnyKluczMiesiaca].ZapisanyGrafik = wynik;
                    WyswietlWynikWGrid(wynik);
                }
            }
            catch (OperationCanceledException)
            {
                if (!_isManualCancellation)
                {
                    HandleTimeout();
                }
                else
                {
                    ClearResultColumns();
                }
            }
            catch (AggregateException ae)
            {
                if (ae.Flatten().InnerExceptions.OfType<OperationCanceledException>().Any())
                {
                    if (!_isManualCancellation) HandleTimeout();
                    else ClearResultColumns();
                }
                else
                {
                    var innerEx = ae.Flatten().InnerException;
                    MessageBox.Show($"Wystąpił błąd krytyczny: {innerEx?.Message}", "Błąd krytyczny", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Wystąpił błąd: {ex.Message}", "Błąd krytyczny", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                stopwatch.Stop();

                if (_progressWatchdog != null)
                {
                    _progressWatchdog.Stop();
                    _progressWatchdog.Tick -= ProgressWatchdog_Tick;
                    _progressWatchdog = null;
                }

                _countdownTimer.Stop();
                if (tickHandler != null) _countdownTimer.Tick -= tickHandler;
                CountdownMessage = "";
                EtaMessage = "";
                ShowCountdown = false;
                ShowEta = false;
                _isManualCancellation = false;
                IsGenerating = false;
                IsBusy = false;
                IsDeterministicSolverRunning = false;
                IsProgressIndeterminate = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        // Watchdog: jeśli długo brak realnego postępu – delikatnie „dosuwaj” pasek (do 98%)
        private void ProgressWatchdog_Tick(object? sender, EventArgs e)
        {
            var now = _progressClock.ElapsedTicks;
            var sinceEvent = (now - _lastProgressEventTick) / (double)Stopwatch.Frequency;
            var sinceTick = (now - _lastWatchdogTick) / (double)Stopwatch.Frequency;
            _lastWatchdogTick = now;

            if (GenerationProgress < 99.9 && sinceEvent > PROGRESS_STALE_SECONDS)
            {
                var cap = 98.0;
                if (_lastProgressPercent < cap)
                {
                    var inc = SOFT_LIFT_RATE_PER_SEC * sinceTick;
                    var next = Math.Min(cap, _lastProgressPercent + inc);
                    if (next > _lastProgressPercent)
                    {
                        _lastProgressPercent = next;
                        GenerationProgress = next;
                    }
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _isManualCancellation = true;
            _cts?.Cancel();
        }

        private void HandleTimeout() { ClearResultColumns(); var dialog = new TimeoutWindow { Owner = this }; dialog.ShowDialog(); if (dialog.Result == TimeoutWindow.TimeoutResult.ChangeAlgorithm) AlgorithmSettings_Click(this, new RoutedEventArgs()); }
        private void ClearResultColumns() { if (_grafikDataTable == null || _grafikDataTable.Rows.Count == 0) return; foreach (DataRow row in _grafikDataTable.Rows) { row["WynikLekarz"] = DBNull.Value; row["Wynik"] = ""; } _grafikZostalWygenerowany = false; UpdateResultColumnDisplay(); }
        private void WyswietlWynikWGrid(RozwiazanyGrafik wynik) { _grafikZostalWygenerowany = true; foreach (DataRow row in _grafikDataTable.Rows) { DateTime dataWiersza = (DateTime)row["FullDate"]; row["WynikLekarz"] = wynik.Przypisania.TryGetValue(dataWiersza, out Lekarz? l) ? l ?? (object)DBNull.Value : (object)DBNull.Value; } UpdateResultColumnDisplay(); }
        private void DataGrid_LoadingRow_Styling(object? sender, DataGridRowEventArgs e) { if (e.Row.Item is DataRowView rowView && rowView.Row.Table.Columns.Contains("FullDate")) { DateTime date = (DateTime)rowView["FullDate"]; if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday || CzyToSwieto(date)) e.Row.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 160, 160)); else e.Row.ClearValue(Control.BackgroundProperty); } }
        private bool CzyToSwieto(DateTime data) { int rok = data.Year; DateTime wielkanoc = ObliczWielkanoc(rok); var swieta = new HashSet<DateTime> { new DateTime(rok, 1, 1), new DateTime(rok, 1, 6), wielkanoc, wielkanoc.AddDays(1), new DateTime(rok, 5, 1), new DateTime(rok, 5, 3), wielkanoc.AddDays(49), wielkanoc.AddDays(60), new DateTime(rok, 8, 15), new DateTime(rok, 11, 1), new DateTime(rok, 11, 11), new DateTime(rok, 12, 25), new DateTime(rok, 12, 26) }; return swieta.Contains(data.Date); }
        private DateTime ObliczWielkanoc(int rok) { int a = rok % 19, b = rok / 100, c = rok % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451; int miesiac = (h + l - 7 * m + 114) / 31, dzien = ((h + l - 7 * m + 114) % 31) + 1; return new DateTime(rok, miesiac, dzien); }
        public static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject { DependencyObject parentObject = VisualTreeHelper.GetParent(child); if (parentObject == null) return null; T? parent = parentObject as T; return parent ?? FindVisualParent<T>(parentObject); }
        private void LimitTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e) { if (!char.IsDigit(e.Text, e.Text.Length - 1)) e.Handled = true; }
        private async void KopiujDoSchowka_Click(object sender, RoutedEventArgs e) { if (_grafikDataTable.Rows.Cast<DataRow>().All(r => r["WynikLekarz"] is DBNull)) { MessageBox.Show("Najpierw wygeneruj grafik.", "Brak Danych", MessageBoxButton.OK, MessageBoxImage.Information); return; } var sb = new StringBuilder(); foreach (DataRow row in _grafikDataTable.Rows) { if (row["WynikLekarz"] is Lekarz lekarz) { string initial = !string.IsNullOrEmpty(lekarz.Imie) ? $"{lekarz.Imie[0]}." : ""; sb.AppendLine($"{lekarz.Nazwisko} {initial}".Trim()); } else { sb.AppendLine("--- BRAK OBSADY ---"); } } bool success = await SetClipboardTextWithRetryAsync(sb.ToString()); if (success) { MessageBox.Show("Lista dyżurnych skopiowana.", "Kopiowanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information); } else { MessageBox.Show("Nie można uzyskać dostępu do schowka.", "Błąd Schowka", MessageBoxButton.OK, MessageBoxImage.Error); } }
        private async Task<bool> SetClipboardTextWithRetryAsync(string text) { const int retries = 10; const int delay = 100; for (int i = 0; i < retries; i++) { try { Clipboard.Clear(); Clipboard.SetText(text); return true; } catch (COMException) { await Task.Delay(delay); } catch (Exception) { return false; } } return false; }
        private void ExportButton_Click(object sender, RoutedEventArgs e) { if (sender is Button button && button.ContextMenu != null) { button.ContextMenu.PlacementTarget = button; button.ContextMenu.IsOpen = true; } }
        private void ZapiszJakoPdf_Click(object sender, RoutedEventArgs e) { if (_grafikDataTable.Rows.Cast<DataRow>().All(r => r["WynikLekarz"] is DBNull)) { MessageBox.Show("Najpierw wygeneruj grafik.", "Brak Danych", MessageBoxButton.OK, MessageBoxImage.Information); return; } var dialog = new SaveFileDialog { Filter = "Plik PDF (*.pdf)|*.pdf", FileName = $"Grafik_{YearComboBox.SelectedItem}_{MonthComboBox.SelectedItem}.pdf" }; if (dialog.ShowDialog() != true) return; try { Document.Create(container => { container.Page(page => { page.Size(PageSizes.A4.Portrait()); page.Margin(20); page.Header().AlignCenter().Column(column => { column.Item().Text("Grafikomat dyżurowy").SemiBold().FontSize(12); column.Item().Text(NazwaOddzialuInfo).Bold().FontSize(16); column.Item().Text(NazwaSzpitalaInfo).FontSize(10); column.Item().PaddingTop(10).Text($"Grafik dyżurów na {MonthComboBox.SelectedItem} {YearComboBox.SelectedItem}").Bold().FontSize(14); }); page.Content().Table(table => { table.ColumnsDefinition(columns => { columns.RelativeColumn(2); columns.RelativeColumn(3); }); table.Header(header => { header.Cell().Background("#2F4F4F").Padding(3).Text("Data").FontColor(QuestPDF.Helpers.Colors.White); header.Cell().Background("#2F4F4F").Padding(3).Text("Dyżurny").FontColor(QuestPDF.Helpers.Colors.White); }); foreach (DataRow dataRow in _grafikDataTable.Rows) { string dyzurny = dataRow["WynikLekarz"] is Lekarz lekarz ? lekarz.PelneImie : "--- BRAK OBSADY ---"; table.Cell().Border(1).Padding(3).Text(dataRow["Data"].ToString()); table.Cell().Border(1).Padding(3).Text(dyzurny); } }); page.Footer().AlignCenter().Text(text => text.Span($"Wygenerowano: {DateTime.Now:yyyy-MM-dd HH:mm}")); }); }).GeneratePdf(dialog.FileName); if (MessageBox.Show($"Pomyślnie zapisano grafik w:\n{dialog.FileName}\n\nCzy chcesz otworzyć plik?", "Eksport Zakończony", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) OpenFile(dialog.FileName); } catch (Exception ex) { MessageBox.Show($"Błąd tworzenia PDF: {ex.Message}", "Błąd Eksportu", MessageBoxButton.OK, MessageBoxImage.Error); } }
        private void ZapiszJakoXlsx_Click(object sender, RoutedEventArgs e) { if (_grafikDataTable.Rows.Cast<DataRow>().All(r => r["WynikLekarz"] is DBNull)) { MessageBox.Show("Najpierw wygeneruj grafik.", "Brak Danych", MessageBoxButton.OK, MessageBoxImage.Information); return; } var dialog = new SaveFileDialog { Filter = "Plik Excel (*.xlsx)|*.xlsx", FileName = $"Grafik_{YearComboBox.SelectedItem}_{MonthComboBox.SelectedItem}.xlsx" }; if (dialog.ShowDialog() != true) return; try { using var workbook = new XLWorkbook(); var worksheet = workbook.Worksheets.Add("Grafik"); worksheet.Cell("A1").Value = "Grafikomat dyżurowy"; worksheet.Cell("A2").Value = NazwaOddzialuInfo; worksheet.Cell("A3").Value = NazwaSzpitalaInfo; worksheet.Cell("A4").Value = $"Grafik dyżurów na {MonthComboBox.SelectedItem} {YearComboBox.SelectedItem}"; worksheet.Range("A1:B1").Merge().Style.Font.Bold = true; worksheet.Range("A2:B2").Merge().Style.Font.Bold = true; worksheet.Range("A3:B3").Merge(); worksheet.Range("A4:B4").Merge().Style.Font.Bold = true; worksheet.Range("A1:A4").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; int tableHeaderRow = 6; worksheet.Cell(tableHeaderRow, 1).Value = "Data"; worksheet.Cell(tableHeaderRow, 2).Value = "Dyżurny"; var headerRow = worksheet.Row(tableHeaderRow); headerRow.Style.Font.Bold = true; headerRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#4F4F4F"); headerRow.Style.Font.FontColor = XLColor.White; int rowIdx = tableHeaderRow + 1; foreach (DataRow dataRow in _grafikDataTable.Rows) { worksheet.Cell(rowIdx, 1).Value = dataRow["Data"].ToString(); string dyzurny = dataRow["WynikLekarz"] is Lekarz lekarz ? lekarz.PelneImie : "--- BRAK OBSADY ---"; worksheet.Cell(rowIdx, 2).Value = dyzurny; rowIdx++; } worksheet.Columns().AdjustToContents(); workbook.SaveAs(dialog.FileName); if (MessageBox.Show($"Pomyślnie zapisano grafik w:\n{dialog.FileName}\n\nCzy chcesz otworzyć plik?", "Eksport Zakończony", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) OpenFile(dialog.FileName); } catch (Exception ex) { MessageBox.Show($"Błąd tworzenia Excel: {ex.Message}", "Błąd Eksportu", MessageBoxButton.OK, MessageBoxImage.Error); } }
        private void OpenFile(string filePath) { try { new Process { StartInfo = new ProcessStartInfo(filePath) { UseShellExecute = true } }.Start(); } catch (Exception ex) { MessageBox.Show($"Nie można otworzyć pliku: {ex.Message}", "Błąd otwierania", MessageBoxButton.OK, MessageBoxImage.Error); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        private void PrevYear_Click(object sender, RoutedEventArgs e) { if (YearComboBox.SelectedIndex > 0) YearComboBox.SelectedIndex--; }
        private void NextYear_Click(object sender, RoutedEventArgs e) { if (YearComboBox.SelectedIndex < YearComboBox.Items.Count - 1) YearComboBox.SelectedIndex++; }
        private void PrevMonth_Click(object sender, RoutedEventArgs e) { if (MonthComboBox.SelectedIndex > 0) MonthComboBox.SelectedIndex--; else if (YearComboBox.SelectedIndex > 0) { YearComboBox.SelectedIndex--; MonthComboBox.SelectedIndex = 11; } }
        private void NextMonth_Click(object sender, RoutedEventArgs e) { if (MonthComboBox.SelectedIndex < MonthComboBox.Items.Count - 1) MonthComboBox.SelectedIndex++; else if (YearComboBox.SelectedIndex < YearComboBox.Items.Count - 1) { YearComboBox.SelectedIndex++; MonthComboBox.SelectedIndex = 0; } }
        private async void MainWindow_StateChanged(object sender, EventArgs e) { if (this.WindowState == WindowState.Minimized || _isBusy) return; await RefreshUILayoutAsync(); }
        private async Task RefreshUILayoutAsync() { if (!_isBusy) { BusyMessage = "Odświeżanie widoku..."; IsGenerating = false; IsBusy = true; await Dispatcher.BeginInvoke(new Action(() => { UpdateLayoutAndText(); IsBusy = false; }), DispatcherPriority.ContextIdle); } }
        private void SetReadOnlyState() { bool isTemporarilyUnlocked = UnlockEditCheckBox.IsChecked == true; bool isEffectivelyLocked = _isCurrentViewLockedByDefault && !isTemporarilyUnlocked; GrafikGrid.IsReadOnly = isEffectivelyLocked; if (_generateButton != null) _generateButton.IsEnabled = !isEffectivelyLocked; foreach (var child in UnifiedHeaderGrid.Children) if (child is TextBox tb && tb.Tag is Lekarz) tb.IsEnabled = !isEffectivelyLocked; }
        private void UnlockEditCheckBox_Changed(object sender, RoutedEventArgs e) { if ((sender as CheckBox)?.IsChecked == true && MessageBox.Show("Uwaga! Edytujesz dane historyczne, które mogą mieć wpływ na przyszłe grafiki. Czy na pewno chcesz kontynuować?", "Potwierdzenie", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No) { UnlockEditCheckBox.IsChecked = false; return; } SetReadOnlyState(); }
    }
}
