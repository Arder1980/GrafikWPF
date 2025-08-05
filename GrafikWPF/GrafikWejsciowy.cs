using System;
using System.Collections.Generic;
using System.Linq;

namespace GrafikWPF
{
    public class GrafikWejsciowy
    {
        public List<Lekarz> Lekarze { get; set; } = new();
        public Dictionary<DateTime, Dictionary<string, TypDostepnosci>> Dostepnosc { get; set; } = new();

        // NOWY ELEMENT: Słownik przechowujący limity dyżurów (Symbol Lekarza -> Limit)
        public Dictionary<string, int> LimityDyzurow { get; set; } = new();

        public List<DateTime> DniWMiesiacu => Dostepnosc.Keys.OrderBy(d => d).ToList();
    }
}