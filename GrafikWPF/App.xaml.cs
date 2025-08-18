using System;
using System.IO;
using System.Windows;

namespace GrafikWPF
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1) Wczytaj ustawienia
            DataManager.LoadData();
            var a = DataManager.AppData;

            // 2) Ustal katalog logów (domyślnie: folder programu/Logs)
            var dir = string.IsNullOrWhiteSpace(a.KatalogLogow)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")
                : a.KatalogLogow;

            // 3) Skonfiguruj logger ZANIM cokolwiek zacznie logować
            RunLogger.Configure(a.LogowanieWlaczone, a.TrybLogowania, dir);
        }
    }
}
