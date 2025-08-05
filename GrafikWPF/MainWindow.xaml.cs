using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GrafikWPF
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private record PreparedData(DataTable DataTable, Dictionary<string, int> Limity, List<Lekarz> LekarzeAktywni, RozwiazanyGrafik? ZapisanyGrafik);

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

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); }
        }

        private double _generationProgress;
        public double GenerationProgress
        {
            get => _generationProgress;
            set { _generationProgress = value; OnPropertyChanged(); }
        }

        public string CurrentPriorityInfo { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            QuestPDF.Settings.License = LicenseType.Community;

            _mapaNazwDostepnosci = new Dictionary<string, TypDostepnosci>
            {
                { "---", TypDostepnosci.Niedostepny }, { "Mogę", TypDostepnosci.Moge },
                { "Chcę", TypDostepnosci.Chce }, { "Bardzo chcę", TypDostepnosci.BardzoChce },
                { "Urlop", TypDostepnosci.Urlop }, { "Dyżur (inny)", TypDostepnosci.DyzurInny },
                { "Mogę warunkowo", TypDostepnosci.MogeWarunkowo }
            };
            _mapaDostepnosciDoNazw = _mapaNazwDostepnosci.ToDictionary(kp => kp.Value, kp => kp.Key);
            CurrentPriorityInfo = string.Empty;

            OpcjeDostepnosci = new List<string>
            {
                "---", "Mogę", "Chcę", "Bardzo chcę", "Mogę warunkowo", "Dyżur (inny)", "Urlop"
            };

            GrafikGrid.LoadingRow += DataGrid_LoadingRow_Styling;
            this.Loaded += Window_Loaded;
            this.Closing += Window_Closing;
            this.DataContext = this;
        }

        private void UpdatePriorityInfo()
        {
            DataManager.AppData.InicjalizujPriorytety();
            var descriptions = DataManager.AppData.KolejnoscPriorytetowSolvera
                .Select(p => PrioritiesWindow.GetEnumDescription(p));

            CurrentPriorityInfo = $"Aktualna kolejność priorytetów: {string.Join(" > ", descriptions)}";
            OnPropertyChanged(nameof(CurrentPriorityInfo));
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
            _isInitializing = false;
            Dispatcher.BeginInvoke(new Action(async () => await ReloadViewAsync()), DispatcherPriority.ContextIdle);
        }

        private void DateSelection_Changed(object? sender, SelectionChangedEventArgs? e)
        {
            if (_isInitializing || IsBusy) return;
            if (e != null && e.OriginalSource is ComboBox)
            {
                Dispatcher.BeginInvoke(new Action(async () => await ReloadViewAsync()), DispatcherPriority.ContextIdle);
            }
        }

        private async Task ReloadViewAsync()
        {
            IsBusy = true;
            try
            {
                await ZapiszBiezacyMiesiac();

                if (YearComboBox.SelectedItem == null || MonthComboBox.SelectedItem == null) return;
                int rok = (int)YearComboBox.SelectedItem;
                int miesiac = MonthComboBox.SelectedIndex + 1;

                UpdatePriorityInfo();
                await AktualizujCaloscInterfejsuAsync(await WygenerujDaneWtle(rok, miesiac));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task AktualizujCaloscInterfejsuAsync(PreparedData preparedData)
        {
            MainContainer.Visibility = Visibility.Collapsed;

            _limityDyzurow = preparedData.Limity;
            _grafikDataTable = preparedData.DataTable;
            _grafikZostalWygenerowany = preparedData.ZapisanyGrafik != null;

            await AktualizujGridAsync(preparedData.LekarzeAktywni);

            WygenerujNaglowkiElementow(preparedData.LekarzeAktywni);
            WygenerujNaglowekGrupowy(preparedData.LekarzeAktywni);

            if (_grafikZostalWygenerowany)
            {
                WyswietlWynikWGrid(preparedData.ZapisanyGrafik!);
            }
            UpdateLayoutAndText();

            MainContainer.Visibility = Visibility.Visible;
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

            var dostepnosc = new Dictionary<DateTime, Dictionary<string, TypDostepnosci>>();
            await Task.Run(() =>
            {
                if (_grafikDataTable == null) return;
                foreach (DataRow row in _grafikDataTable.Rows)
                {
                    var data = (DateTime)row["FullDate"];
                    var wpisyDnia = new Dictionary<string, TypDostepnosci>();
                    foreach (var lekarz in DataManager.AppData.WszyscyLekarze)
                    {
                        if (_grafikDataTable.Columns.Contains(lekarz.Symbol))
                        {
                            _mapaNazwDostepnosci.TryGetValue(row[lekarz.Symbol].ToString() ?? "---", out var typ);
                            wpisyDnia[lekarz.Symbol] = typ;
                        }
                        else
                        {
                            wpisyDnia[lekarz.Symbol] = TypDostepnosci.Niedostepny;
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
                var lekarzeAktywni = DataManager.AppData.WszyscyLekarze.Where(l => l.IsAktywny).OrderBy(l => l.Nazwisko).ThenBy(l => l.Imie).ToList();
                DataTable dt;
                Dictionary<string, int> limity;
                RozwiazanyGrafik? zapisanyGrafik = null;

                if (DataManager.AppData.DaneGrafikow.TryGetValue(_aktualnyKluczMiesiaca, out var daneMiesiaca))
                {
                    limity = new Dictionary<string, int>(daneMiesiaca.LimityDyzurow);
                    dt = PrzygotujSiatkeDanychZBiezacychDanych(rok, miesiac, lekarzeAktywni, daneMiesiaca);
                    zapisanyGrafik = daneMiesiaca.ZapisanyGrafik;
                }
                else
                {
                    limity = lekarzeAktywni.ToDictionary(l => l.Symbol, l => 0);
                    dt = PrzygotujSiatkeDanychNowyMiesiac(rok, miesiac, lekarzeAktywni);
                }

                return new PreparedData(dt, limity, lekarzeAktywni, zapisanyGrafik);
            });
        }

        private DataTable PrzygotujSiatkeDanychNowyMiesiac(int rok, int miesiac, List<Lekarz> lekarzeAktywni)
        {
            var dt = new DataTable();
            dt.Columns.Add("FullDate", typeof(DateTime));
            dt.Columns.Add("Data", typeof(string));
            foreach (var symbol in lekarzeAktywni.Select(l => l.Symbol)) dt.Columns.Add(symbol, typeof(string));
            dt.Columns.Add("Data_Powtorzona", typeof(string));
            dt.Columns.Add("Wynik", typeof(string));
            dt.Columns.Add("WynikLekarz", typeof(Lekarz));

            int dniWMiesiacu = DateTime.DaysInMonth(rok, miesiac);
            for (int dzien = 1; dzien <= dniWMiesiacu; dzien++)
            {
                var data = new DateTime(rok, miesiac, dzien);
                var row = dt.NewRow();
                row["FullDate"] = data;
                row["Data"] = data.ToString("dd.MM (dddd)", new CultureInfo("pl-PL"));
                foreach (var symbol in lekarzeAktywni.Select(l => l.Symbol)) row[symbol] = "---";
                row["Data_Powtorzona"] = row["Data"];
                row["WynikLekarz"] = DBNull.Value;
                dt.Rows.Add(row);
            }
            return dt;
        }

        private DataTable PrzygotujSiatkeDanychZBiezacychDanych(int rok, int miesiac, List<Lekarz> lekarzeAktywni, DaneMiesiaca daneMiesiaca)
        {
            var dt = new DataTable();
            dt.Columns.Add("FullDate", typeof(DateTime));
            dt.Columns.Add("Data", typeof(string));
            foreach (var symbol in lekarzeAktywni.Select(l => l.Symbol)) dt.Columns.Add(symbol, typeof(string));
            dt.Columns.Add("Data_Powtorzona", typeof(string));
            dt.Columns.Add("Wynik", typeof(string));
            dt.Columns.Add("WynikLekarz", typeof(Lekarz));

            int dniWMiesiacu = DateTime.DaysInMonth(rok, miesiac);
            for (int dzien = 1; dzien <= dniWMiesiacu; dzien++)
            {
                var data = new DateTime(rok, miesiac, dzien);
                var row = dt.NewRow();
                row["FullDate"] = data;
                row["Data"] = data.ToString("dd.MM (dddd)", new CultureInfo("pl-PL"));

                if (daneMiesiaca.Dostepnosc != null && daneMiesiaca.Dostepnosc.TryGetValue(data, out var dostepnosciDnia))
                {
                    foreach (var lekarz in lekarzeAktywni)
                    {
                        if (dostepnosciDnia.TryGetValue(lekarz.Symbol, out var typ))
                        {
                            row[lekarz.Symbol] = _mapaDostepnosciDoNazw.GetValueOrDefault(typ, "---");
                        }
                        else
                        {
                            row[lekarz.Symbol] = "---";
                        }
                    }
                }
                else
                {
                    foreach (var symbol in lekarzeAktywni.Select(l => l.Symbol)) row[symbol] = "---";
                }

                row["Data_Powtorzona"] = row["Data"];
                row["WynikLekarz"] = DBNull.Value;
                dt.Rows.Add(row);
            }
            return dt;
        }

        private async Task AktualizujGridAsync(List<Lekarz> lekarzeAktywni)
        {
            GrafikGrid.ItemsSource = null;
            GrafikGrid.Columns.Clear();
            var singleClickStyle = (Style)this.TryFindResource("SingleClickEditingCellStyle");
            var staticStyle = (Style)this.TryFindResource("StaticColumnStyle");
            var centerTextStyle = (Style)this.TryFindResource("CenterVTextBlockStyle");

            var dataColumn1 = new DataGridTextColumn
            {
                Header = null,
                Binding = new Binding("[Data]"),
                IsReadOnly = true,
                Width = new DataGridLength(1.5, DataGridLengthUnitType.Star),
                CellStyle = staticStyle,
                ElementStyle = centerTextStyle
            };
            GrafikGrid.Columns.Add(dataColumn1);

            foreach (var lekarz in lekarzeAktywni)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    var templateColumn = new DataGridTemplateColumn { Header = null, Width = new DataGridLength(1, DataGridLengthUnitType.Star) };
                    string cellTemplateXaml = $@"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{{Binding [{lekarz.Symbol}]}}' HorizontalAlignment='Center' VerticalAlignment='Center'/></DataTemplate>";
                    string editingTemplateXaml = $@"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ComboBox ItemsSource='{{Binding DataContext.OpcjeDostepnosci, RelativeSource={{RelativeSource AncestorType=Window}}}}' SelectedItem='{{Binding [{lekarz.Symbol}], Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}}' HorizontalContentAlignment='Center' VerticalContentAlignment='Center' IsDropDownOpen='True'/></DataTemplate>";
                    templateColumn.CellTemplate = (DataTemplate)XamlReader.Parse(cellTemplateXaml);
                    templateColumn.CellEditingTemplate = (DataTemplate)XamlReader.Parse(editingTemplateXaml);
                    templateColumn.CellStyle = singleClickStyle;
                    GrafikGrid.Columns.Add(templateColumn);
                }, DispatcherPriority.Background);
            }

            var dataColumn2 = new DataGridTextColumn()
            {
                Header = null,
                Binding = new Binding("[Data_Powtorzona]"),
                IsReadOnly = true,
                Width = new DataGridLength(1.5, DataGridLengthUnitType.Star),
                CellStyle = staticStyle,
                ElementStyle = centerTextStyle
            };
            GrafikGrid.Columns.Add(dataColumn2);

            var wynikColumn = new DataGridTextColumn()
            {
                Header = null,
                Binding = new Binding("[Wynik]"),
                IsReadOnly = true,
                Width = new DataGridLength(1.5, DataGridLengthUnitType.Star),
                CellStyle = staticStyle,
                ElementStyle = centerTextStyle
            };
            GrafikGrid.Columns.Add(wynikColumn);

            GrafikGrid.ItemsSource = _grafikDataTable.DefaultView;
        }

        private void TableContainerGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutAndText();

        private void UpdateLayoutAndText()
        {
            UpdateRowHeights();
            UpdateMaksDyzurText();
            UpdateResultColumnDisplay();
            SyncButtonSizes();
        }

        private void SyncButtonSizes(object? sender = null, SizeChangedEventArgs? e = null)
        {
            if (_generateButton != null && ExportButton.IsLoaded && _generateButton.IsLoaded)
            {
                if (_generateButton.ActualWidth > 0)
                {
                    ExportButton.Width = _generateButton.ActualWidth;
                    ExportButton.Height = _generateButton.ActualHeight;
                }
            }
        }

        private void UpdateRowHeights()
        {
            if (!this.IsLoaded || GrafikGrid.Items.Count <= 0) return;
            double headerRowHeight = 35;
            DateSelectorRow.Height = new GridLength(headerRowHeight);
            if (ItemHeaderGrid.RowDefinitions.Count >= 3)
            {
                ItemHeaderGrid.RowDefinitions[0].Height = new GridLength(headerRowHeight);
                ItemHeaderGrid.RowDefinitions[2].Height = new GridLength(headerRowHeight);
            }
            MainHeaderGrid.Height = headerRowHeight;
            HeaderStackPanel.UpdateLayout();
            double headersActualHeight = HeaderStackPanel.ActualHeight;

            double footerHeight = FooterPanel.ActualHeight + FooterPanel.Margin.Top + FooterPanel.Margin.Bottom;

            double containerHeight = TableContainerGrid.ActualHeight;
            double dataGridAvailableHeight = containerHeight - headersActualHeight - footerHeight;
            dataGridAvailableHeight -= 2;
            if (dataGridAvailableHeight > 1)
            {
                GrafikGrid.RowHeight = dataGridAvailableHeight / GrafikGrid.Items.Count;
            }
        }

        private void UpdateMaksDyzurText()
        {
            if (_maksDyzurTextBlock == null || GrafikGrid.Columns.Count == 0) return;
            double columnWidth = GrafikGrid.Columns[0].ActualWidth;
            if (columnWidth > 160) _maksDyzurTextBlock.Text = "Maksymalna ilość dyżurów";
            else if (columnWidth > 120) _maksDyzurTextBlock.Text = "Maks. ilość dyżurów";
            else _maksDyzurTextBlock.Text = "Maks. dyżurów";
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
                        var formattedText = new FormattedText(
                            lekarz.PelneImie, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                            new Typeface(this.FontFamily, this.FontStyle, this.FontWeight, this.FontStretch),
                            GrafikGrid.FontSize, Brushes.Black, new NumberSubstitution(), 1);
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

        private void Info_Click(object? sender, RoutedEventArgs e)
        {
            var infoWindow = new InfoWindow { Owner = this };
            infoWindow.ShowDialog();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void SettingsButton_Click(object? sender, RoutedEventArgs e)
        {
            var settingsWindow = new UstawieniaLekarzyWindow(DataManager.AppData.WszyscyLekarze) { Owner = this };
            bool? result = settingsWindow.ShowDialog();
            if (result == true)
            {
                DataManager.AppData.WszyscyLekarze = settingsWindow.ZaktualizowaniLekarze;
                Dispatcher.BeginInvoke(new Action(async () => await ReloadViewAsync()), DispatcherPriority.ContextIdle);
            }
        }

        private void PrioritiesSettings_Click(object sender, RoutedEventArgs e)
        {
            DataManager.AppData.InicjalizujPriorytety();
            var prioritiesWindow = new PrioritiesWindow(DataManager.AppData.KolejnoscPriorytetowSolvera)
            {
                Owner = this
            };
            bool? result = prioritiesWindow.ShowDialog();
            if (result == true)
            {
                DataManager.AppData.KolejnoscPriorytetowSolvera = prioritiesWindow.NewOrder;
                UpdatePriorityInfo();
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

        private void WygenerujNaglowekGrupowy(List<Lekarz> lekarzeAktywni)
        {
            MainHeaderGrid.ColumnDefinitions.Clear();
            MainHeaderGrid.Children.Clear();
            MainHeaderGrid.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(79, 79, 79));
            var columns = GrafikGrid.Columns;
            if (!columns.Any()) return;
            for (int i = 0; i < columns.Count; i++)
            {
                var binding = new Binding($"Columns[{i}].ActualWidth") { Source = GrafikGrid };
                var colDef = new ColumnDefinition();
                BindingOperations.SetBinding(colDef, ColumnDefinition.WidthProperty, binding);
                MainHeaderGrid.ColumnDefinitions.Add(colDef);
            }
            var newPadding = new Thickness(0, 8, 0, 8);
            var mainDataHeader = new TextBlock { Text = "Data", FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = System.Windows.VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Padding = newPadding };
            Grid.SetColumn(mainDataHeader, 0);
            MainHeaderGrid.Children.Add(mainDataHeader);
            if (lekarzeAktywni.Any())
            {
                var mainDeklaracjeHeader = new TextBlock { Text = "Deklaracje dostępności", FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = System.Windows.VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Padding = newPadding };
                Grid.SetColumn(mainDeklaracjeHeader, 1);
                Grid.SetColumnSpan(mainDeklaracjeHeader, lekarzeAktywni.Count);
                MainHeaderGrid.Children.Add(mainDeklaracjeHeader);
            }
            int secondDateColumnIndex = 1 + lekarzeAktywni.Count;
            var secondDataHeader = new TextBlock { Text = "Data", FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = System.Windows.VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Padding = newPadding };
            Grid.SetColumn(secondDataHeader, secondDateColumnIndex);
            MainHeaderGrid.Children.Add(secondDataHeader);
            var mainWynikHeader = new TextBlock { Text = "Wynik Grafiku", FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = System.Windows.VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Padding = newPadding };
            Grid.SetColumn(mainWynikHeader, columns.Count - 1);
            MainHeaderGrid.Children.Add(mainWynikHeader);
        }

        private void WygenerujNaglowkiElementow(List<Lekarz> lekarzeAktywni)
        {
            ItemHeaderGrid.ColumnDefinitions.Clear();
            ItemHeaderGrid.Children.Clear();
            ItemHeaderGrid.RowDefinitions.Clear();

            if (_generateButton != null)
            {
                _generateButton.SizeChanged -= SyncButtonSizes;
            }

            var columns = GrafikGrid.Columns;
            if (!columns.Any()) return;
            ItemHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ItemHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ItemHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < columns.Count; i++)
            {
                var binding = new Binding($"Columns[{i}].ActualWidth") { Source = GrafikGrid };
                var colDef = new ColumnDefinition();
                BindingOperations.SetBinding(colDef, ColumnDefinition.WidthProperty, binding);
                ItemHeaderGrid.ColumnDefinitions.Add(colDef);
            }
            var settingsBtn = new Button { Content = "Edycja dyżurnych", Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(2) };
            settingsBtn.Click += SettingsButton_Click;
            Grid.SetRow(settingsBtn, 0);
            Grid.SetColumn(settingsBtn, 0);
            ItemHeaderGrid.Children.Add(settingsBtn);
            _maksDyzurTextBlock = new TextBlock { Text = "Maks. dyżurów", FontWeight = FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            Grid.SetRow(_maksDyzurTextBlock, 2);
            Grid.SetColumn(_maksDyzurTextBlock, 0);
            ItemHeaderGrid.Children.Add(_maksDyzurTextBlock);
            var separator = new Border { BorderBrush = Brushes.DarkGray, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(separator, 1);
            Grid.SetColumn(separator, 0);
            Grid.SetColumnSpan(separator, columns.Count);
            ItemHeaderGrid.Children.Add(separator);
            int columnIndex = 1;
            foreach (var lekarz in lekarzeAktywni)
            {
                var symbolBlock = new TextBlock { Text = lekarz.Symbol, FontWeight = FontWeights.Bold, FontSize = 14, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                Grid.SetRow(symbolBlock, 0);
                Grid.SetColumn(symbolBlock, columnIndex);
                ItemHeaderGrid.Children.Add(symbolBlock);
                var textBox = new TextBox
                {
                    Text = _limityDyzurow.GetValueOrDefault(lekarz.Symbol, 0).ToString(),
                    Tag = lekarz,
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    Margin = new Thickness(5, 2, 5, 2),
                    Padding = new Thickness(0, 1, 0, 1)
                };
                textBox.LostFocus += LimitTextBox_LostFocus;
                textBox.PreviewTextInput += LimitTextBox_PreviewTextInput;
                Grid.SetRow(textBox, 2);
                Grid.SetColumn(textBox, columnIndex);
                ItemHeaderGrid.Children.Add(textBox);
                columnIndex++;
            }

            var buttonFactory = new Button { Content = "Generuj Grafik", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(2) };
            buttonFactory.Click += new RoutedEventHandler(GenerateButton_Click);

            _generateButton = buttonFactory;
            _generateButton.SizeChanged += SyncButtonSizes;

            Grid.SetRow(buttonFactory, 2);
            Grid.SetColumn(buttonFactory, columns.Count - 1);
            ItemHeaderGrid.Children.Add(buttonFactory);
        }

        private void LimitTextBox_LostFocus(object? sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            var lekarz = textBox?.Tag as Lekarz;
            if (textBox != null && lekarz != null)
            {
                if (int.TryParse(textBox.Text, out int nowyLimit))
                {
                    if (YearComboBox.SelectedItem is int rok && MonthComboBox.SelectedIndex != -1)
                    {
                        int miesiac = MonthComboBox.SelectedIndex + 1;
                        int dniWMiesiacu = DateTime.DaysInMonth(rok, miesiac);
                        if (nowyLimit > dniWMiesiacu)
                        {
                            MessageBox.Show("Nie bądźmy zachłanni, dajmy popracować też innym ;-)", "Absurdalny limit", MessageBoxButton.OK, MessageBoxImage.Information);
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
            var cell = sender as DataGridCell;
            if (cell != null && !cell.IsEditing && !cell.IsReadOnly)
            {
                if (!cell.IsFocused) cell.Focus();
                var dataGrid = FindVisualParent<DataGrid>(cell);
                if (dataGrid != null) dataGrid.BeginEdit(e);
            }
        }

        private async void GenerateButton_Click(object? sender, RoutedEventArgs e)
        {
            await ZapiszBiezacyMiesiac();
            if (!_limityDyzurow.Values.Any(limit => limit > 0))
            {
                MessageBox.Show("Przynajmniej jeden z lekarzy musi mieć wprowadzoną maksymalną liczbę dyżurów większą od 0.", "Brak limitów dyżurów", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            IsBusy = true;
            GenerationProgress = 0;
            var lekarzeAktywni = DataManager.AppData.WszyscyLekarze.Where(l => l.IsAktywny).ToList();
            var dostepnosc = DataManager.AppData.DaneGrafikow[_aktualnyKluczMiesiaca!].Dostepnosc;
            var daneDoSilnika = new GrafikWejsciowy { Lekarze = lekarzeAktywni, Dostepnosc = dostepnosc, LimityDyzurow = _limityDyzurow };
            var progress = new Progress<double>(p => GenerationProgress = p * 100);

            DataManager.AppData.InicjalizujPriorytety();
            var solver = new GrafikSolver(daneDoSilnika, DataManager.AppData.KolejnoscPriorytetowSolvera, progress);

            RozwiazanyGrafik wynik = await Task.Run(() => solver.ZnajdzOptymalneRozwiazanie());
            DataManager.AppData.DaneGrafikow[_aktualnyKluczMiesiaca!].ZapisanyGrafik = wynik;
            WyswietlWynikWGrid(wynik);
            IsBusy = false;
        }

        private void WyswietlWynikWGrid(RozwiazanyGrafik wynik)
        {
            _grafikZostalWygenerowany = true;
            foreach (DataRow row in _grafikDataTable.Rows)
            {
                DateTime dataWiersza = (DateTime)row["FullDate"];
                if (wynik.Przypisania.TryGetValue(dataWiersza, out Lekarz? przypisanyLekarz))
                {
                    row["WynikLekarz"] = przypisanyLekarz ?? (object)DBNull.Value;
                }
                else
                {
                    row["WynikLekarz"] = DBNull.Value;
                }
            }
            UpdateResultColumnDisplay();
        }

        private void DataGrid_LoadingRow_Styling(object? sender, DataGridRowEventArgs e)
        {
            if (e.Row.Item is not DataRowView rowView || !rowView.Row.Table.Columns.Contains("FullDate")) return;
            DateTime date = (DateTime)rowView["FullDate"];
            if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday || CzyToSwieto(date))
            {
                e.Row.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 160, 160));
                e.Row.Tag = "Holiday";
            }
            else
            {
                e.Row.ClearValue(Control.BackgroundProperty);
                e.Row.Tag = "Workday";
            }
        }

        private bool CzyToSwieto(DateTime data)
        {
            int rok = data.Year;
            DateTime wielkanoc = ObliczWielkanoc(rok);
            var swieta = new HashSet<DateTime> { new DateTime(rok, 1, 1), new DateTime(rok, 1, 6), wielkanoc, wielkanoc.AddDays(1), new DateTime(rok, 5, 1), new DateTime(rok, 5, 3), wielkanoc.AddDays(49), wielkanoc.AddDays(60), new DateTime(rok, 8, 15), new DateTime(rok, 11, 1), new DateTime(rok, 11, 11), new DateTime(rok, 12, 25), new DateTime(rok, 12, 26) };
            return swieta.Contains(data.Date);
        }

        private DateTime ObliczWielkanoc(int rok)
        {
            int a = rok % 19, b = rok / 100, c = rok % 100, d = b / 4, e = b % 4;
            int f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
            int i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
            int m = (a + 11 * h + 22 * l) / 451, miesiac = (h + l - 7 * m + 114) / 31;
            int dzien = ((h + l - 7 * m + 114) % 31) + 1;
            return new DateTime(rok, miesiac, dzien);
        }

        public static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            return parentObject is T parent ? parent : FindVisualParent<T>(parentObject);
        }

        private void LimitTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!char.IsDigit(e.Text, e.Text.Length - 1)) e.Handled = true;
        }

        private async void KopiujDoSchowka_Click(object sender, RoutedEventArgs e)
        {
            if (_grafikDataTable.Rows.Cast<DataRow>().All(r => r["WynikLekarz"] is DBNull))
            {
                MessageBox.Show("Najpierw wygeneruj grafik, aby mieć co eksportować.", "Brak Danych", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Data\tDyżurny");
            foreach (DataRow row in _grafikDataTable.Rows)
            {
                string dyzurny = row["WynikLekarz"] is Lekarz lekarz ? lekarz.PelneImie : "--- BRAK OBSADY ---";
                sb.AppendLine($"{row["Data"]}\t{dyzurny}");
            }

            bool success = await SetClipboardTextWithRetryAsync(sb.ToString());
            if (success)
            {
                MessageBox.Show("Wyniki zostały skopiowane do schowka.", "Kopiowanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Nie można uzyskać dostępu do schowka. Jest on używany przez inny proces. Spróbuj ponownie.", "Błąd Schowka", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<bool> SetClipboardTextWithRetryAsync(string text)
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    var dataObject = new DataObject();
                    dataObject.SetText(text);
                    Clipboard.SetDataObject(dataObject, true);
                    return true;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    await Task.Delay(100);
                }
            }
            return false;
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.IsOpen = true;
            }
        }

        private void ZapiszJakoPdf_Click(object sender, RoutedEventArgs e)
        {
            if (_grafikDataTable.Rows.Cast<DataRow>().All(r => r["WynikLekarz"] is DBNull))
            {
                MessageBox.Show("Najpierw wygeneruj grafik, aby mieć co eksportować.", "Brak Danych", MessageBoxButton.OK, MessageBoxImage.Information); return;
            }
            var dialog = new SaveFileDialog { Filter = "Plik PDF (*.pdf)|*.pdf", FileName = $"Grafik_{YearComboBox.SelectedItem}_{MonthComboBox.SelectedItem}.pdf" };
            if (dialog.ShowDialog() != true) return;
            try
            {
                Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4.Portrait());
                        page.MarginVertical(20);
                        page.MarginHorizontal(30);

                        page.Header().AlignCenter().Text($"Grafik dyżurów na {MonthComboBox.SelectedItem} {YearComboBox.SelectedItem}").Bold().FontSize(16);
                        page.Content().Table(table =>
                        {
                            float fontSize = 10;
                            float cellPadding = 3;

                            if (_grafikDataTable.Rows.Count > 29)
                            {
                                fontSize = 9.5f;
                                cellPadding = 2;
                            }
                            if (_grafikDataTable.Rows.Count > 30)
                            {
                                fontSize = 9f;
                                cellPadding = 1.5f;
                            }

                            table.ColumnsDefinition(columns => { columns.RelativeColumn(2); columns.RelativeColumn(3); });
                            table.Header(header =>
                            {
                                header.Cell().Background("#2F4F4F").Padding(cellPadding).Text("Data").FontColor(QuestPDF.Helpers.Colors.White).FontSize(fontSize);
                                header.Cell().Background("#2F4F4F").Padding(cellPadding).Text("Dyżurny").FontColor(QuestPDF.Helpers.Colors.White).FontSize(fontSize);
                            });
                            foreach (DataRow dataRow in _grafikDataTable.Rows)
                            {
                                string dyzurny = dataRow["WynikLekarz"] is Lekarz lekarz ? lekarz.PelneImie : "--- BRAK OBSADY ---";
                                table.Cell().Border(1).Padding(cellPadding).Text(dataRow["Data"].ToString()).FontSize(fontSize);
                                table.Cell().Border(1).Padding(cellPadding).Text(dyzurny).FontSize(fontSize);
                            }
                        });
                        page.Footer().AlignCenter().Text(text => text.Span($"Wygenerowano: {DateTime.Now:yyyy-MM-dd HH:mm}"));
                    });
                }).GeneratePdf(dialog.FileName);

                string message = $"Pomyślnie zapisano grafik dyżurowy na {MonthComboBox.SelectedItem} {YearComboBox.SelectedItem} w:\n{dialog.FileName}\n\nCzy chcesz otworzyć utworzony plik?";
                if (MessageBox.Show(message, "Eksport Zakończony", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    OpenFile(dialog.FileName);
                }
            }
            catch (Exception ex) { MessageBox.Show($"Wystąpił błąd podczas tworzenia pliku PDF: {ex.Message}", "Błąd Eksportu", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ZapiszJakoXlsx_Click(object sender, RoutedEventArgs e)
        {
            if (_grafikDataTable.Rows.Cast<DataRow>().All(r => r["WynikLekarz"] is DBNull))
            {
                MessageBox.Show("Najpierw wygeneruj grafik, aby mieć co eksportować.", "Brak Danych", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog { Filter = "Plik Excel (*.xlsx)|*.xlsx", FileName = $"Grafik_{YearComboBox.SelectedItem}_{MonthComboBox.SelectedItem}.xlsx" };
            if (dialog.ShowDialog() != true) return;

            try
            {
                using var workbook = new XLWorkbook();
                var worksheet = workbook.Worksheets.Add("Grafik Dyżurów");

                worksheet.Cell("A1").Value = "Data";
                worksheet.Cell("B1").Value = "Dyżurny";

                var headerRow = worksheet.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#4F4F4F");
                headerRow.Style.Font.FontColor = XLColor.White;

                int rowIdx = 2;
                foreach (DataRow dataRow in _grafikDataTable.Rows)
                {
                    var dataCell = dataRow["Data"];
                    if (dataCell != null && dataCell != DBNull.Value)
                    {
                        worksheet.Cell(rowIdx, 1).Value = dataCell.ToString();
                    }

                    string dyzurny = dataRow["WynikLekarz"] is Lekarz lekarz ? lekarz.PelneImie : "--- BRAK OBSADY ---";
                    worksheet.Cell(rowIdx, 2).Value = dyzurny;

                    rowIdx++;
                }

                worksheet.Columns().AdjustToContents();
                workbook.SaveAs(dialog.FileName);

                string message = $"Pomyślnie zapisano grafik dyżurowy na {MonthComboBox.SelectedItem} {YearComboBox.SelectedItem} w:\n{dialog.FileName}\n\nCzy chcesz otworzyć utworzony plik?";
                if (MessageBox.Show(message, "Eksport Zakończony", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    OpenFile(dialog.FileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Wystąpił błąd podczas tworzenia pliku Excel: {ex.Message}", "Błąd Eksportu", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFile(string filePath)
        {
            try
            {
                new Process
                {
                    StartInfo = new ProcessStartInfo(filePath)
                    {
                        UseShellExecute = true
                    }
                }.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Nie można otworzyć pliku. Upewnij się, że masz zainstalowany program do obsługi tego typu plików.\n\nBłąd: {ex.Message}", "Błąd otwierania pliku", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void PrevYear_Click(object sender, RoutedEventArgs e)
        {
            if (YearComboBox.SelectedIndex > 0)
            {
                YearComboBox.SelectedIndex--;
            }
        }

        private void NextYear_Click(object sender, RoutedEventArgs e)
        {
            if (YearComboBox.SelectedIndex < YearComboBox.Items.Count - 1)
            {
                YearComboBox.SelectedIndex++;
            }
        }

        private void PrevMonth_Click(object sender, RoutedEventArgs e)
        {
            if (MonthComboBox.SelectedIndex > 0)
            {
                MonthComboBox.SelectedIndex--;
            }
            else
            {
                if (YearComboBox.SelectedIndex > 0)
                {
                    YearComboBox.SelectedIndex--;
                    MonthComboBox.SelectedIndex = 11;
                }
            }
        }

        private void NextMonth_Click(object sender, RoutedEventArgs e)
        {
            if (MonthComboBox.SelectedIndex < MonthComboBox.Items.Count - 1)
            {
                MonthComboBox.SelectedIndex++;
            }
            else
            {
                if (YearComboBox.SelectedIndex < YearComboBox.Items.Count - 1)
                {
                    YearComboBox.SelectedIndex++;
                    MonthComboBox.SelectedIndex = 0;
                }
            }
        }
    }
}