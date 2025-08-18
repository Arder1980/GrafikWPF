using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace GrafikWPF
{
    public partial class UstawieniaOgolneWindow : Window
    {
        public string NazwaOddzialu { get; set; } = "";
        public string NazwaSzpitala { get; set; } = "";

        public UstawieniaOgolneWindow()
        {
            InitializeComponent();

            // Spójne tło
            var brush = TryFindResource("AppWindowBackground") as Brush;
            if (brush != null) this.Background = brush;
            else if (Application.Current?.MainWindow != null)
                this.Background = Application.Current.MainWindow.Background;

            var app = DataManager.AppData;
            LoadFrom(app);

            // po załadowaniu kontrolek dopasuj szerokość do bieżącej ścieżki
            Loaded += (_, __) => AdjustPathFieldAndWindowWidth();
        }

        public UstawieniaOgolneWindow(DaneAplikacji dane) : this()
        {
            if (!ReferenceEquals(dane, DataManager.AppData))
            {
                LoadFrom(dane);
                AdjustPathFieldAndWindowWidth();
            }
        }

        private void LoadFrom(DaneAplikacji app)
        {
            NazwaOddzialu = app.NazwaOddzialu;
            NazwaSzpitala = app.NazwaSzpitala;
            DataContext = this;

            ChkLogEnabled.IsChecked = app.LogowanieWlaczone;
            CmbLogMode.SelectedIndex = app.TrybLogowania == LogMode.Debug ? 1 : 0;

            var domyslny = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            TxtLogDir.Text = string.IsNullOrWhiteSpace(app.KatalogLogow) ? domyslny : app.KatalogLogow;
        }

        // --- AUTO-DOPASOWANIE SZEROKOŚCI DO DŁUGOŚCI ŚCIEŻKI ---
        private void AdjustPathFieldAndWindowWidth()
        {
            // 1) zmierz szerokość tekstu ścieżki w pikselach (bez zawijania)
            double textW = MeasureTextWidth(TxtLogDir.Text, TxtLogDir);

            // 2) ustaw minimalną szerokość pola zgodnie z zawartością (z zapasem)
            double minText = Math.Max(420, textW + 24);
            TxtLogDir.MinWidth = minText;

            // 3) wylicz sugerowaną szerokość okna: kolumna etykiet + odstępy + textbox + przyciski + marginesy
            double labelCol = 170; // z XAML
            double betweenCols = 12;
            double windowMargins = 16 * 2; // lewy+prawy
            double innerGridSpacing = 10 + 8; // odstępy w gridzie z przyciskami
            double buttons = (BtnPick.ActualWidth > 0 ? BtnPick.ActualWidth : 100)
                           + (BtnOpen.ActualWidth > 0 ? BtnOpen.ActualWidth : 90);

            double desired = labelCol + betweenCols + minText + innerGridSpacing + buttons + windowMargins;

            // 4) nie wychodź poza 90% szerokości obszaru roboczego ekranu
            double max = SystemParameters.WorkArea.Width * 0.9;
            this.Width = Math.Min(Math.Max(this.Width, desired), max);
        }

        private static double MeasureTextWidth(string text, Control templateControl)
        {
            if (string.IsNullOrEmpty(text)) text = "C:\\";
            var tb = new TextBlock
            {
                Text = text,
                FontFamily = templateControl.FontFamily,
                FontSize = templateControl.FontSize,
                FontWeight = templateControl.FontWeight,
                FontStyle = templateControl.FontStyle,
                FontStretch = templateControl.FontStretch
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return tb.DesiredSize.Width;
        }

        private void PickLogDir_Click(object sender, RoutedEventArgs e)
        {
            var startDir = Directory.Exists(TxtLogDir.Text) ? TxtLogDir.Text : AppDomain.CurrentDomain.BaseDirectory;
            var dlg = new SaveFileDialog
            {
                Title = "Wybierz folder logów",
                FileName = "Wybierz_tę_lokalizację",
                InitialDirectory = startDir,
                OverwritePrompt = false,
                AddExtension = false,
                Filter = "Folder|*.*"
            };
            if (dlg.ShowDialog(this) == true)
            {
                var path = Path.GetDirectoryName(dlg.FileName) ?? "";
                if (!string.IsNullOrWhiteSpace(path))
                {
                    TxtLogDir.Text = path;
                    AdjustPathFieldAndWindowWidth(); // <- przelicz po zmianie ścieżki
                }
            }
        }

        private void OpenLogDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var p = TxtLogDir.Text;
                if (!Directory.Exists(p)) Directory.CreateDirectory(p);
                Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true });
            }
            catch { }
        }

        private void Zapisz_Click(object sender, RoutedEventArgs e)
        {
            var app = DataManager.AppData;

            app.NazwaOddzialu = NazwaOddzialu;
            app.NazwaSzpitala = NazwaSzpitala;
            app.LogowanieWlaczone = ChkLogEnabled.IsChecked == true;
            app.TrybLogowania = (CmbLogMode.SelectedIndex == 1) ? LogMode.Debug : LogMode.Info;
            app.KatalogLogow = TxtLogDir.Text;

            DataManager.SaveData();

            var katalog = string.IsNullOrWhiteSpace(app.KatalogLogow)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")
                : app.KatalogLogow;
            RunLogger.Configure(app.LogowanieWlaczone, app.TrybLogowania, katalog);

            DialogResult = true;
            Close();
        }
    }
}
