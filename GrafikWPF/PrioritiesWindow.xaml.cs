using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace GrafikWPF
{
    public partial class PrioritiesWindow : Window, INotifyPropertyChanged
    {
        public class PriorityItem
        {
            public SolverPriority Priority { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
        }

        public ObservableCollection<PriorityItem> PriorityList { get; set; }
        public List<SolverPriority> NewOrder { get; private set; }

        private PriorityItem? _selectedPriority;
        public PriorityItem? SelectedPriority
        {
            get => _selectedPriority;
            set { _selectedPriority = value; OnPropertyChanged(); UpdateButtonState(); }
        }

        public PrioritiesWindow(List<SolverPriority> currentOrder)
        {
            InitializeComponent();
            this.DataContext = this;

            PriorityList = new ObservableCollection<PriorityItem>();
            NewOrder = new List<SolverPriority>(currentOrder);

            LoadPriorities(currentOrder);
            UpdateItemNames();
        }

        private void LoadPriorities(List<SolverPriority> order)
        {
            PriorityList.Clear();
            var allPriorities = Enum.GetValues(typeof(SolverPriority)).Cast<SolverPriority>();

            foreach (var priority in order)
            {
                if (allPriorities.Contains(priority))
                {
                    PriorityList.Add(CreatePriorityItem(priority));
                }
            }
            foreach (var priority in allPriorities)
            {
                if (!PriorityList.Any(p => p.Priority == priority))
                {
                    PriorityList.Add(CreatePriorityItem(priority));
                }
            }
        }

        private PriorityItem CreatePriorityItem(SolverPriority priority)
        {
            return new PriorityItem
            {
                Priority = priority,
                Name = GetEnumDescription(priority),
                Description = GetEnumLongDescription(priority)
            };
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

        private void UpdateItemNames()
        {
            for (int i = 0; i < PriorityList.Count; i++)
            {
                PriorityList[i].Name = $"Priorytet {i + 1}: {GetEnumDescription(PriorityList[i].Priority)}";
            }
            PrioritiesListBox.Items.Refresh();
        }

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
            if (index < PriorityList.Count - 1)
            {
                PriorityList.Move(index, index + 1);
                UpdateItemNames();
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            NewOrder = PriorityList.Select(p => p.Priority).ToList();
            this.DialogResult = true;
            this.Close();
        }

        private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            var defaultOrder = new List<SolverPriority>
            {
                SolverPriority.CiagloscPoczatkowa,
                SolverPriority.LacznaLiczbaObsadzonychDni,
                SolverPriority.ZrealizowaneBardzoChce,
                SolverPriority.ZrealizowaneChce,
                SolverPriority.RownomiernoscObciazenia
            };
            LoadPriorities(defaultOrder);
            UpdateItemNames();
        }

        public static string GetEnumDescription(Enum value)
        {
            FieldInfo? fi = value.GetType().GetField(value.ToString());
            if (fi == null) return value.ToString();

            DescriptionAttribute[]? attributes = fi.GetCustomAttributes(typeof(DescriptionAttribute), false) as DescriptionAttribute[];
            if (attributes != null && attributes.Any())
            {
                return attributes.First().Description;
            }
            return value.ToString();
        }

        public static string GetEnumLongDescription(SolverPriority priority)
        {
            switch (priority)
            {
                case SolverPriority.CiagloscPoczatkowa:
                    return "Cel: zapewnienie jak najdłuższej nieprzerwanej obsady dyżurów od początku miesiąca.";
                case SolverPriority.LacznaLiczbaObsadzonychDni:
                    return "Cel: zapewnienie jak największej liczby obsadzonych dyżurów w całym miesiącu.";
                case SolverPriority.ZrealizowaneBardzoChce:
                    return "Cel: przydzielenie jak największej liczby dyżurów o statusie 'Bardzo chcę'.";
                case SolverPriority.ZrealizowaneChce:
                    return "Cel: przydzielenie jak największej liczby dyżurów o statusie 'Chcę'.";
                case SolverPriority.RownomiernoscObciazenia:
                    return "Cel: jak najbardziej sprawiedliwy i proporcjonalny podział dyżurów między lekarzy.";
                default:
                    return "";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            UpdateButtonState();
        }
    }
}