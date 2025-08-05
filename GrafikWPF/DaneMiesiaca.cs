using System;
using System.Collections.Generic;

namespace GrafikWPF
{
    public class DaneMiesiaca
    {
        /// <summary>
        /// Słownik przechowujący zadeklarowaną dostępność lekarzy w danym miesiącu.
        /// Klucz: Data (DateTime), Wartość: Słownik (Symbol Lekarza -> TypDostepnosci).
        /// </summary>
        public Dictionary<DateTime, Dictionary<string, TypDostepnosci>> Dostepnosc { get; set; } = new();

        /// <summary>
        /// Słownik przechowujący limity dyżurów dla lekarzy w danym miesiącu.
        /// Klucz: Symbol Lekarza, Wartość: Limit (int).
        /// </summary>
        public Dictionary<string, int> LimityDyzurow { get; set; } = new();

        /// <summary>
        /// Wygenerowany i zapisany przez użytkownika grafik dla danego miesiąca.
        /// Może być null, jeśli grafik nie został jeszcze wygenerowany lub zapisany.
        /// </summary>
        public RozwiazanyGrafik? ZapisanyGrafik { get; set; }
    }
}