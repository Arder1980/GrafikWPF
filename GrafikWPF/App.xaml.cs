using System.Windows;

namespace GrafikWPF
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DataManager.LoadData();
        }
    }
}