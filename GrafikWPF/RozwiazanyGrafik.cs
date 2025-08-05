using System;
using System.Collections.Generic;
using System.Linq;

namespace GrafikWPF
{
    public class RozwiazanyGrafik
    {
        public Dictionary<DateTime, Lekarz?> Przypisania { get; set; } = new();
        public int DlugoscCiaguPoczatkowego { get; set; }
        public int LiczbaDniObsadzonych => Przypisania.Values.Count(l => l != null);

        // Oddzielne liczniki dla nowych priorytetów
        public int ZrealizowaneBardzoChce { get; set; }
        public int ZrealizowaneChce { get; set; }

        // NOWY ELEMENT: Wskaźnik równomierności (im niższy, tym lepiej)
        public double WskaznikRownomiernosci { get; set; }

        public Dictionary<string, int> FinalneOblozenieLekarzy { get; set; } = new();
    }
}