namespace GrafikWPF
{
    public class DaneAplikacji
    {
        public string NazwaOddzialu { get; set; } = "Zakład Diagnostyki Obrazowej";
        public string NazwaSzpitala { get; set; } = "Szpital Kliniczny im. dr. Emila Warmińskiego Politechniki Bydgoskiej";

        public List<Lekarz> WszyscyLekarze { get; set; } = new();

        public Dictionary<string, DaneMiesiaca> DaneGrafikow { get; set; } = new();

        public List<SolverPriority> KolejnoscPriorytetowSolvera { get; set; } = new();

        public SolverType WybranyAlgorytm { get; set; } = SolverType.Backtracking;
        public bool LogowanieWlaczone { get; set; } = true;
        public LogMode TrybLogowania { get; set; } = LogMode.Info;
        public string KatalogLogow { get; set; } = "";

        public void InicjalizujPriorytety()
        {
            if (KolejnoscPriorytetowSolvera == null || !KolejnoscPriorytetowSolvera.Any())
            {
                KolejnoscPriorytetowSolvera = new List<SolverPriority>
                {
                    SolverPriority.CiagloscPoczatkowa,
                    SolverPriority.LacznaLiczbaObsadzonychDni,
                    SolverPriority.SprawiedliwoscObciazenia,
                    SolverPriority.RownomiernoscRozlozenia
                };
            }
        }
    }
}