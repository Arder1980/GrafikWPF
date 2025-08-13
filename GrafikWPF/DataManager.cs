using System.IO;
using System.Text.Json;
using System.Windows;

namespace GrafikWPF
{
    public static class DataManager
    {
        private static readonly string _dataFolderPath;
        private static readonly string _dataFilePath;
        private static readonly string _backupFilePath;
        private static readonly JsonSerializerOptions _jsonOptions;

        public static DaneAplikacji AppData { get; private set; } = new();

        static DataManager()
        {
            _dataFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrafikOptymalny");
            _dataFilePath = Path.Combine(_dataFolderPath, "data.json");
            _backupFilePath = Path.Combine(_dataFolderPath, "data.bak");
            _jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        }

        public static void LoadData()
        {
            try
            {
                Directory.CreateDirectory(_dataFolderPath);

                if (File.Exists(_dataFilePath))
                {
                    string jsonString = File.ReadAllText(_dataFilePath);
                    AppData = JsonSerializer.Deserialize<DaneAplikacji>(jsonString) ?? new DaneAplikacji();
                }
                else if (File.Exists(_backupFilePath))
                {
                    string jsonString = File.ReadAllText(_backupFilePath);
                    AppData = JsonSerializer.Deserialize<DaneAplikacji>(jsonString) ?? new DaneAplikacji();
                    MessageBox.Show("Nie znaleziono głównego pliku danych. Wczytano dane z kopii zapasowej.", "Informacja", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                if (AppData.WszyscyLekarze == null || AppData.WszyscyLekarze.Count == 0)
                {
                    InitializeDefaultData();
                }

                // Czyszczenie priorytetów z nieaktualnych wartości
                var aktualnePriorytety = Enum.GetValues(typeof(SolverPriority)).Cast<SolverPriority>();
                AppData.KolejnoscPriorytetowSolvera = AppData.KolejnoscPriorytetowSolvera
                                                        .Where(p => aktualnePriorytety.Contains(p))
                                                        .ToList();

                AppData.InicjalizujPriorytety();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Wystąpił krytyczny błąd podczas wczytywania danych: {ex.Message}\nAplikacja spróbuje użyć danych domyślnych.", "Błąd wczytywania", MessageBoxButton.OK, MessageBoxImage.Error);
                InitializeDefaultData();
                AppData.InicjalizujPriorytety();
            }
        }

        public static void SaveData()
        {
            try
            {
                if (File.Exists(_dataFilePath))
                {
                    File.Copy(_dataFilePath, _backupFilePath, true);
                }

                string jsonString = JsonSerializer.Serialize(AppData, _jsonOptions);
                File.WriteAllText(_dataFilePath, jsonString);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Wystąpił błąd podczas zapisywania danych: {ex.Message}", "Błąd zapisu", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void InitializeDefaultData()
        {
            AppData = new DaneAplikacji
            {
                WszyscyLekarze = new List<Lekarz>
                {
                    new("BIE", "Aleksandra", "Biedroń"),
                    new("GRA", "Rafał", "Grabowski"),
                    new("HRY", "Julia", "Hrycyk"),
                    new("LEM", "Adam", "Lemanowicz"),
                    new("LES", "Natalia", "Leszczyńska"),
                    new("NAR", "Agnieszka", "Narolska-Jochemczak"),
                    new("POL", "Maria", "Polska"),
                    new("PRU", "Kamil", "Prusakowski"),
                    new("SER", "Zbigniew", "Serafin"),
                    new("SOB", "Bartosz", "Sobociński"),
                    new("ŻAK", "Kinga", "Żak")
                }
            };
        }
    }
}