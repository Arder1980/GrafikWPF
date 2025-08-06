using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace GrafikWPF
{
    public partial class InfoWindow : Window
    {
        public InfoWindow()
        {
            InitializeComponent();
        }

        private void Zamknij_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // ### ZMIANA ### Dodano metodę do obsługi kliknięcia w link licencji
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}