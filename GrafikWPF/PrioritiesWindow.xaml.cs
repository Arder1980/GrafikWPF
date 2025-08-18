using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;

namespace GrafikWPF
{
    public partial class PrioritiesWindow : Window, INotifyPropertyChanged
    {
        public class PriorityItem
        {
            public SolverPriority Priority { get; set; }
            public string Name { get; set; } = string.Empty;        // np. "Priorytet 1: Ciągłość obsady"
            public string Description { get; set; } = string.Empty; // dłuższe objaśnienie
        }

        // Zawartość listy w oknie
        public ObservableCollection<PriorityItem> PriorityList { get; set; } = new();

        // Zwracana kolejność po "Zapisz i Zamknij"
        public List<SolverPriority> NewOrder { get; private set; } = new();

        private PriorityItem? _selectedPriority;
        public PriorityItem? SelectedPriority
        {
            get => _selectedPriority;
            set { _selectedPriority = value; OnPropertyChanged(); UpdateButtonState(); }
        }

        public PrioritiesWindow(List<SolverPriority> currentOrder)
        {
            InitializeComponent();
            DataContext = this;

            // Bezpieczna kolejność: weź to, co przyszło, dołóż brakujące enumy (np. nowy piąty)
            var all = Enum.GetValues(typeof(SolverPriority)).Cast<SolverPriority>().ToList();
            var order = (currentOrder ?? new List<SolverPriority>()).Where(all.Contains).ToList();
            foreach (var p in all) if (!order.Contains(p)) order.Add(p);

            LoadPriorities(order);
            UpdateItemNames();
        }

        private void LoadPriorities(List<SolverPriority> order)
        {
            PriorityList.Clear();
            foreach (var p in order)
            {
                PriorityList.Add(new PriorityItem
                {
                    Priority = p,
                    Name = GetEnumDescription(p),           // krótka nazwa do wiersza
                    Description = GetEnumLongDescription(p) // dłuższe objaśnienie
                });
            }
        }

        private void UpdateItemNames()
        {
            // Nadaj numery "Priorytet 1/2/..."
            for (int i = 0; i < PriorityList.Count; i++)
            {
                string shortName = GetEnumDescription(PriorityList[i].Priority);
                PriorityList[i].Name = $"Priorytet {i + 1}: {shortName}";
            }
            PrioritiesListBox.Items.Refresh();
        }

        private void UpdateButtonState()
        {
            if (SelectedPriority != null)
            {
                int index = PriorityList.IndexOf(SelectedPriority);
                MoveUpButton.IsEnabled = index > 0;
                MoveDownButton.IsEnabled = index < PriorityList.Count - 1;
            }
            else
            {
                MoveUpButton.IsEnabled = false;
                MoveDownButton.IsEnabled = false;
            }
        }

        // ====== Handlery przycisków (layout jak w Twoim XAML) ======
        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPriority == null) return;
            int index = PriorityList.IndexOf(SelectedPriority);
            if (index > 0)
            {
                PriorityList.Move(index, index - 1);
                UpdateItemNames();
            }
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPriority == null) return;
            int index = PriorityList.IndexOf(SelectedPriority);
            if (index >= 0 && index < PriorityList.Count - 1)
            {
                PriorityList.Move(index, index + 1);
                UpdateItemNames();
            }
        }

        private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            // Twoja dotychczasowa „domyślna” kolejność + dopięty 5. priorytet na końcu
            var def = new List<SolverPriority>
            {
                SolverPriority.CiagloscPoczatkowa,
                SolverPriority.LacznaLiczbaObsadzonychDni,
                SolverPriority.SprawiedliwoscObciazenia,
                SolverPriority.RownomiernoscRozlozenia,
                SolverPriority.ZgodnoscWaznosciDeklaracji // nowy piąty
            };
            LoadPriorities(def);
            UpdateItemNames();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            NewOrder = PriorityList.Select(p => p.Priority).ToList();
            DialogResult = true;
            Close();
        }

        // ====== Nazwa krótka pobierana z [Description] na enumie (fallback: mapowanie) ======
        public static string GetEnumDescription(Enum value)
        {
            // jeśli enum ma [Description("...")] — użyj go
            FieldInfo? fi = value.GetType().GetField(value.ToString());
            if (fi != null)
            {
                var attrs = fi.GetCustomAttributes(typeof(DescriptionAttribute), false) as DescriptionAttribute[];
                if (attrs != null && attrs.Length > 0) return attrs[0].Description;
            }

            // fallback — nazwy „po ludzku”
            if (value is SolverPriority sp)
            {
                return sp switch
                {
                    SolverPriority.CiagloscPoczatkowa => "Ciągłość obsady",
                    SolverPriority.LacznaLiczbaObsadzonychDni => "Obsada (łączna)",
                    SolverPriority.SprawiedliwoscObciazenia => "Sprawiedliwość (σ obciążeń)",
                    SolverPriority.RownomiernoscRozlozenia => "Równomierność (czasowa)",
                    SolverPriority.ZgodnoscWaznosciDeklaracji => "Zgodność z ważnością deklaracji",
                    _ => value.ToString()
                };
            }
            return value.ToString();
        }

        // ====== Dłuższe opisy (pełne nazwy, bez skrótów) ======
        public static string GetEnumLongDescription(SolverPriority p) => p switch
        {
            SolverPriority.CiagloscPoczatkowa =>
                "Cel: zapewnienie jak najdłuższej nieprzerwanej obsady dyżurów od początku miesiąca.",
            SolverPriority.LacznaLiczbaObsadzonychDni =>
                "Cel: maksymalna liczba obsadzonych dyżurów w całym miesiącu.",
            SolverPriority.SprawiedliwoscObciazenia =>
                "Cel: możliwie sprawiedliwy podział dyżurów, proporcjonalny do zadeklarowanych limitów.",
            SolverPriority.RownomiernoscRozlozenia =>
                "Cel: równy rozrzut dyżurów w czasie (ograniczanie skupisk dzień-po-dniu).",
            SolverPriority.ZgodnoscWaznosciDeklaracji =>
                "Cel: preferowanie przydziałów zgodnych z deklaracjami: „Bardzo chcę” > „Chcę” > „Mogę” > „Mogę warunkowo”. Deklaracje takie jak: „Rezerwacja”, „Urlop”, „Dyżur inny” i „Nie mogę”, ze względu na swój szczególny charakter, nie są tu uwzględniane.",
            _ => string.Empty
        };

        // ====== INotifyPropertyChanged ======
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
