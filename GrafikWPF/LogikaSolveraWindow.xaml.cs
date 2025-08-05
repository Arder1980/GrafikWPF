using System.Windows;

namespace GrafikWPF
{
    public partial class LogikaSolveraWindow : Window
    {
        public LogikaSolveraWindow()
        {
            InitializeComponent();
            this.Loaded += (s, e) => { this.MaxHeight = SystemParameters.WorkArea.Height; };
        }

        private void Zamknij_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}