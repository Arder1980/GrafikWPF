using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GrafikWPF
{
    public class Lekarz : INotifyPropertyChanged
    {
        private string _symbol = "";
        public string Symbol
        {
            get => _symbol;
            set { _symbol = value; OnPropertyChanged(); }
        }

        private string _imie = "";
        public string Imie
        {
            get => _imie;
            set { _imie = value; OnPropertyChanged(); }
        }

        private string _nazwisko = "";
        public string Nazwisko
        {
            get => _nazwisko;
            set { _nazwisko = value; OnPropertyChanged(); }
        }

        private bool _isAktywny = true;
        public bool IsAktywny
        {
            get => _isAktywny;
            set { _isAktywny = value; OnPropertyChanged(); }
        }

        public string PelneImie => $"{Imie} {Nazwisko}";

        public Lekarz() { }

        public Lekarz(string symbol, string imie, string nazwisko, bool isAktywny = true)
        {
            _symbol = symbol;
            _imie = imie;
            _nazwisko = nazwisko;
            _isAktywny = isAktywny;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}