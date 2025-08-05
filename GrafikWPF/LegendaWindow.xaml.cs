using System.Windows;

namespace GrafikWPF
{
    public partial class LegendaWindow : Window
    {
        public LegendaWindow()
        {
            InitializeComponent();
            this.Loaded += LegendaWindow_Loaded;
        }

        private void LegendaWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Ograniczamy maksymalną wysokość okna do wysokości ekranu
            this.MaxHeight = SystemParameters.WorkArea.Height;
        }

        private void Zamknij_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}