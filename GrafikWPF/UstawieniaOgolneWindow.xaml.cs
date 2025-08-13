using System.Windows;

namespace GrafikWPF
{
    public partial class UstawieniaOgolneWindow : Window
    {
        public string NazwaOddzialu { get; set; }
        public string NazwaSzpitala { get; set; }

        public UstawieniaOgolneWindow(DaneAplikacji dane)
        {
            InitializeComponent();
            NazwaOddzialu = dane.NazwaOddzialu;
            NazwaSzpitala = dane.NazwaSzpitala;
            this.DataContext = this;
        }

        private void Zapisz_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }
    }
}