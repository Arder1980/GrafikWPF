using System.Collections.Generic;
using System.Linq;

namespace GrafikWPF
{
    public class DaneAplikacji
    {
        /// <summary>
        /// Kompletna lista wszystkich lekarzy, zarówno aktywnych, jak i archiwalnych.
        /// </summary>
        public List<Lekarz> WszyscyLekarze { get; set; } = new();

        /// <summary>
        /// Słownik przechowujący dane dla poszczególnych miesięcy.
        /// Klucz jest w formacie "YYYY-MM" (np. "2025-08").
        /// </summary>
        public Dictionary<string, DaneMiesiaca> DaneGrafikow { get; set; } = new();

        /// <summary>
        /// Przechowuje ustawioną przez użytkownika kolejność priorytetów dla algorytmu Solver'a.
        /// </summary>
        public List<SolverPriority> KolejnoscPriorytetowSolvera { get; set; } = new();


        /// <summary>
        /// Metoda zapewniająca, że lista priorytetów nie jest pusta i ma domyślną, sensowną kolejność.
        /// </summary>
        public void InicjalizujPriorytety()
        {
            if (KolejnoscPriorytetowSolvera == null || !KolejnoscPriorytetowSolvera.Any())
            {
                KolejnoscPriorytetowSolvera = new List<SolverPriority>
                {
                    SolverPriority.CiagloscPoczatkowa,
                    SolverPriority.LacznaLiczbaObsadzonychDni,
                    SolverPriority.ZrealizowaneBardzoChce,
                    SolverPriority.ZrealizowaneChce,
                    SolverPriority.RownomiernoscObciazenia
                };
            }
        }
    }
}