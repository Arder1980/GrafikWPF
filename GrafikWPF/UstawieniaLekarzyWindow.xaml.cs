using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GrafikWPF
{
    public partial class UstawieniaLekarzyWindow : Window
    {
        private const int MAKS_AKTYWNYCH_LEKARZY = 20;
        private readonly List<Lekarz> _wszyscyLekarze;
        public ObservableCollection<Lekarz> WidoczniLekarze { get; set; }
        public List<Lekarz> ZaktualizowaniLekarze => _wszyscyLekarze;

        public UstawieniaLekarzyWindow(IEnumerable<Lekarz> aktualniLekarze)
        {
            InitializeComponent();
            _wszyscyLekarze = aktualniLekarze.Select(l => new Lekarz(l.Symbol, l.Imie, l.Nazwisko, l.IsAktywny)).ToList();
            WidoczniLekarze = new ObservableCollection<Lekarz>();
            this.DataContext = this;
        }

        private void UstawieniaLekarzyWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LekarzeItemsControl.ItemsSource = WidoczniLekarze;
            OdswiezWidokLekarzy();
        }

        private void OdswiezWidokLekarzy()
        {
            WidoczniLekarze.Clear();
            var czyPokazacUsunietych = PokazUsunietychCheckBox.IsChecked == true;

            var lekarzeDoWyswietlenia = _wszyscyLekarze
                .Where(l => !l.IsUkryty && (l.IsAktywny || czyPokazacUsunietych))
                .OrderBy(l => !l.IsAktywny)
                .ThenBy(l => l.Nazwisko)
                .ThenBy(l => l.Imie);

            foreach (var lekarz in lekarzeDoWyswietlenia)
            {
                WidoczniLekarze.Add(lekarz);
            }
        }

        private void ValidateForDuplicates(Lekarz currentLekarz)
        {
            if (currentLekarz == null || string.IsNullOrWhiteSpace(currentLekarz.Imie) || string.IsNullOrWhiteSpace(currentLekarz.Nazwisko) || string.IsNullOrWhiteSpace(currentLekarz.Symbol))
            {
                return;
            }

            var duplicate = _wszyscyLekarze.FirstOrDefault(l =>
                l != currentLekarz &&
                l.Imie.Trim().Equals(currentLekarz.Imie.Trim(), StringComparison.InvariantCultureIgnoreCase) &&
                l.Nazwisko.Trim().Equals(currentLekarz.Nazwisko.Trim(), StringComparison.InvariantCultureIgnoreCase) &&
                l.Symbol.Trim().Equals(currentLekarz.Symbol.Trim(), StringComparison.InvariantCultureIgnoreCase));

            if (duplicate != null)
            {
                MessageBox.Show($"Wykryto duplikat. Dyżurny '{duplicate.PelneImie}' z symbolem '{duplicate.Symbol}' już istnieje na liście.", "Zduplikowane Dane", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Zapisz_Click(object sender, RoutedEventArgs e)
        {
            var grupyDuplikatow = _wszyscyLekarze
                .Where(l => !l.IsUkryty && !string.IsNullOrWhiteSpace(l.Imie) && !string.IsNullOrWhiteSpace(l.Nazwisko) && !string.IsNullOrWhiteSpace(l.Symbol))
                .GroupBy(l => $"{l.Imie.Trim().ToLower()}|{l.Nazwisko.Trim().ToLower()}|{l.Symbol.Trim().ToUpper()}")
                .Where(g => g.Count() > 1)
                .ToList();

            if (grupyDuplikatow.Any())
            {
                string message = "Wykryto zduplikowane wpisy. Co chcesz zrobić?\n\n[Tak] = Automatycznie usuń duplikaty i zapisz.\n[Nie] = Wróć do edycji, aby ręcznie poprawić.\n[Anuluj] = Odrzuć wszystkie zmiany z tej sesji.";
                MessageBoxResult result = MessageBox.Show(this, message, "Wykryto Duplikaty", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

                switch (result)
                {
                    case MessageBoxResult.Yes:
                        foreach (var grupa in grupyDuplikatow)
                        {
                            var duplikatyDoUsuniecia = grupa.Skip(1).ToList();
                            foreach (var duplikat in duplikatyDoUsuniecia)
                            {
                                _wszyscyLekarze.Remove(duplikat);
                            }
                        }
                        break;

                    case MessageBoxResult.No:
                        OdswiezWidokLekarzy();
                        return;

                    case MessageBoxResult.Cancel:
                        this.DialogResult = false;
                        this.Close();
                        return;
                }
            }

            var niekompletnyWpis = _wszyscyLekarze.FirstOrDefault(l => l.IsAktywny && !l.IsUkryty && (string.IsNullOrWhiteSpace(l.Imie) || string.IsNullOrWhiteSpace(l.Nazwisko) || string.IsNullOrWhiteSpace(l.Symbol)));
            if (niekompletnyWpis != null)
            {
                MessageBox.Show("Uzupełnij wszystkie dane (Imię, Nazwisko, Symbol) dla aktywnych dyżurnych lub usuń pusty wiersz.", "Niekompletne Dane", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            this.DialogResult = true;
            this.Close();
        }

        private void Anuluj_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void DodajLekarza_Click(object sender, RoutedEventArgs e)
        {
            var pustyLekarz = _wszyscyLekarze.FirstOrDefault(l =>
                string.IsNullOrWhiteSpace(l.Imie) &&
                string.IsNullOrWhiteSpace(l.Nazwisko) &&
                string.IsNullOrWhiteSpace(l.Symbol) &&
                !l.IsUkryty);

            if (pustyLekarz != null)
            {
                LekarzeScrollViewer.ScrollToBottom();
                if (LekarzeItemsControl.ItemContainerGenerator.ContainerFromItem(pustyLekarz) is FrameworkElement container)
                {
                    var textBox = FindVisualChild<TextBox>(container);
                    textBox?.Focus();
                }
                return;
            }

            if (_wszyscyLekarze.Count(l => l.IsAktywny && !l.IsUkryty) >= MAKS_AKTYWNYCH_LEKARZY)
            {
                MessageBox.Show($"Osiągnięto maksymalny limit {MAKS_AKTYWNYCH_LEKARZY} aktywnych dyżurnych. Aby dodać nowego, najpierw usuń (zarchiwizuj) kogoś z listy.", "Limit Osiągnięty", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var nowyLekarz = new Lekarz();
            _wszyscyLekarze.Add(nowyLekarz);
            OdswiezWidokLekarzy();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                LekarzeScrollViewer.ScrollToEnd();
                if (LekarzeItemsControl.ItemContainerGenerator.ContainerFromItem(nowyLekarz) is FrameworkElement container)
                {
                    var textBox = FindVisualChild<TextBox>(container);
                    textBox?.Focus();
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ArchiwizujLekarza_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Lekarz lekarz)
            {
                var wynik = MessageBox.Show($"Czy na pewno chcesz usunąć dyżurnego: {lekarz.PelneImie}? Dyżurny zostanie zarchiwizowany i nie będzie już brany pod uwagę w przyszłych grafikach. Dane archiwalne pozostaną nienaruszone. Będzie można go przywrócić w dowolnym momencie.", "Potwierdzenie usunięcia", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (wynik == MessageBoxResult.Yes)
                {
                    lekarz.IsAktywny = false;
                    OdswiezWidokLekarzy();
                }
            }
        }

        private void PrzywrocLekarza_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Lekarz lekarz)
            {
                if (_wszyscyLekarze.Count(l => l.IsAktywny && !l.IsUkryty) >= MAKS_AKTYWNYCH_LEKARZY)
                {
                    MessageBox.Show($"Osiągnięto maksymalny limit {MAKS_AKTYWNYCH_LEKARZY} aktywnych dyżurnych. Aby przywrócić tego dyżurnego, należy najpierw usunąć (zarchiwizować) innego.", "Limit Osiągnięty", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                lekarz.IsAktywny = true;
                OdswiezWidokLekarzy();
            }
        }

        private void UsunTrwaleLekarza_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Lekarz lekarz)
            {
                var wynik = MessageBox.Show($"Czy na pewno chcesz TRWALE UKRYĆ dyżurnego '{lekarz.PelneImie}' z listy?\n\nTa operacja jest NIEODWRACALNA. Dyżurny zniknie z widoku, ale jego dane archiwalne zostaną nienaruszone. Aby go ponownie użyć, musisz dodać go ręcznie od nowa.", "Potwierdzenie trwałego ukrycia", MessageBoxButton.YesNo, MessageBoxImage.Exclamation);
                if (wynik == MessageBoxResult.Yes)
                {
                    lekarz.IsUkryty = true;
                    OdswiezWidokLekarzy();
                }
            }
        }

        private void PoleDanych_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.DataContext is Lekarz lekarz)
            {
                // Formatowanie
                if (textBox.Name == "ImieTextBox") lekarz.Imie = FormatPersonName(lekarz.Imie);
                if (textBox.Name == "NazwiskoTextBox") lekarz.Nazwisko = FormatSurname(lekarz.Nazwisko);

                // Sugerowanie symbolu
                if (textBox.Name == "NazwiskoTextBox" && string.IsNullOrWhiteSpace(lekarz.Symbol) && !string.IsNullOrWhiteSpace(lekarz.Nazwisko) && lekarz.Nazwisko.Length >= 3)
                {
                    string potencjalnySymbol = lekarz.Nazwisko.Substring(0, 3).ToUpper();
                    bool duplikatSymbolu = _wszyscyLekarze.Any(l => l != lekarz && !l.IsUkryty && l.Symbol == potencjalnySymbol);
                    if (!duplikatSymbolu)
                    {
                        lekarz.Symbol = potencjalnySymbol;
                    }
                }

                ValidateForDuplicates(lekarz);
            }
        }

        private string FormatPersonName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            string cleanedName = Regex.Replace(name.Trim(), @"\s+", " ");
            return new CultureInfo("pl-PL", false).TextInfo.ToTitleCase(cleanedName.ToLower());
        }

        private string FormatSurname(string surname)
        {
            if (string.IsNullOrWhiteSpace(surname)) return string.Empty;
            string cleanedSurname = Regex.Replace(surname.Trim(), @"\s*-\s*", "-");
            cleanedSurname = Regex.Replace(cleanedSurname, @"\s+", "-");
            TextInfo textInfo = new CultureInfo("pl-PL", false).TextInfo;
            var parts = cleanedSurname.Split('-').Select(p => textInfo.ToTitleCase(p.ToLower()));
            return string.Join("-", parts.Where(p => !string.IsNullOrEmpty(p)));
        }

        public static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    return typedChild;
                }
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void ContentPresenter_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ContentPresenter presenter)
            {
                var item = presenter.Content;
                int index = LekarzeItemsControl.Items.IndexOf(item);
                var textBlock = presenter.ContentTemplate.FindName("RowNumberTextBlock", presenter) as TextBlock;
                if (textBlock != null)
                {
                    textBlock.Text = (index + 1).ToString();
                }
            }
        }

        private void PokazUsunietychCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (this.IsLoaded)
            {
                OdswiezWidokLekarzy();
            }
        }
    }
}