using System;
using System.Collections.Generic;

namespace GrafikWPF
{
    public class PlikZapisu
    {
        public List<Lekarz> Lekarze { get; set; } = new();
        public List<string[]> DaneTabeli { get; set; } = new();
        public int WybranyRok { get; set; }
        public int WybranyMiesiacIndex { get; set; }
    }
}