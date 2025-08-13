using System.Windows;

namespace GrafikWPF
{
    public partial class TimeoutWindow : Window
    {
        public enum TimeoutResult
        {
            OK,
            ChangeAlgorithm
        }

        public TimeoutResult Result { get; private set; }

        public TimeoutWindow()
        {
            InitializeComponent();
            Result = TimeoutResult.OK; // Domyślna akcja, jeśli okno zostanie zamknięte inaczej
        }

        private void ChangeAlgorithm_Click(object sender, RoutedEventArgs e)
        {
            Result = TimeoutResult.ChangeAlgorithm;
            this.DialogResult = true;
            this.Close();
        }
    }
}