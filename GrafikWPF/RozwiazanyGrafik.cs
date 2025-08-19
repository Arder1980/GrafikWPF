// FILE: GrafikWPF/RozwiazanyGrafik.cs
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

        public int ZrealizowaneRezerwacje { get; set; }
        public int ZrealizowaneBardzoChce { get; set; }
        public int ZrealizowaneChce { get; set; }
        public int ZrealizowaneMoge { get; set; }

        // NOWE, docelowe nazwy:
        // 1) Sprawiedliwość = proporcjonalność obciążeń do limitów (wcześniej trzymane w WskaznikRownomiernosci)
        public double WskaznikSprawiedliwosci { get; set; }

        // 2) Równomierność = rozstrzelenie dyżurów w miesiącu (wcześniej WskaznikRozlozeniaDyzurow)
        public double WskaznikRownomiernosci { get; set; }

        public Dictionary<string, int> FinalneOblozenieLekarzy { get; set; } = new();
    }
}
