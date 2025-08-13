namespace GrafikWPF
{
    public class RozwiazanyGrafik
    {
        public Dictionary<DateTime, Lekarz?> Przypisania { get; set; } = new();
        public int DlugoscCiaguPoczatkowego { get; set; }
        public int LiczbaDniObsadzonych => Przypisania.Values.Count(l => l != null);

        public int ZrealizowaneRezerwacje { get; set; }
        public int ZrealizowaneBardzoChce { get; set; }
        public int ZrealizowaneChce { get; set; }
        public int ZrealizowaneMoge { get; set; }

        public double WskaznikRownomiernosci { get; set; }
        public double WskaznikRozlozeniaDyzurow { get; set; }

        public Dictionary<string, int> FinalneOblozenieLekarzy { get; set; } = new();
    }
}