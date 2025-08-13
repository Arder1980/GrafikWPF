namespace GrafikWPF
{
    public static class BenchmarkDataFactory
    {
        private const int RandomSeed = 12345; // Stałe ziarno dla 100% powtarzalności testów

        public static GrafikWejsciowy CreateTestCase(int doctorCount)
        {
            var random = new Random(RandomSeed + doctorCount); // Inne ziarno dla każdego scenariusza
            var lekarze = new List<Lekarz>();
            for (int i = 0; i < doctorCount; i++)
            {
                lekarze.Add(new Lekarz($"L{i + 1:D2}", $"Imie{i + 1}", $"Nazwisko{i + 1}", true));
            }

            var limity = lekarze.ToDictionary(
                l => l.Symbol,
                l => random.Next(1, doctorCount <= 10 ? 6 : 11)
            );

            var dostepnosc = new Dictionary<DateTime, Dictionary<string, TypDostepnosci>>();
            var rok = DateTime.Now.Year;
            var miesiac = 1;
            int dniWMiesiacu = DateTime.DaysInMonth(rok, miesiac);
            var wszystkieDni = Enumerable.Range(1, dniWMiesiacu).Select(d => new DateTime(rok, miesiac, d)).ToList();

            foreach (var dzien in wszystkieDni)
            {
                dostepnosc[dzien] = new Dictionary<string, TypDostepnosci>();
            }

            foreach (var lekarz in lekarze)
            {
                var dostepneDni = new HashSet<DateTime>(wszystkieDni);

                // Krok 1: Urlopy w blokach
                if (random.NextDouble() < 0.3) // 30% szans na urlop
                {
                    var dlugoscUrlopu = new[] { 4, 7, 14 }[random.Next(3)];
                    if (dniWMiesiacu > dlugoscUrlopu)
                    {
                        var startUrlopu = random.Next(1, dniWMiesiacu - dlugoscUrlopu);
                        for (int i = 0; i < dlugoscUrlopu; i++)
                        {
                            var dzienUrlopu = new DateTime(rok, miesiac, startUrlopu + i);
                            dostepnosc[dzienUrlopu][lekarz.Symbol] = TypDostepnosci.Urlop;
                            dostepneDni.Remove(dzienUrlopu);
                        }
                    }
                }

                // Krok 2: Określenie "chętnych dni"
                var iloscCheci = random.Next(2, 21);
                var chetneDni = dostepneDni.OrderBy(d => random.Next()).Take(iloscCheci).ToList();

                // Krok 3: Przypisanie pozytywnych deklaracji
                foreach (var dzien in chetneDni)
                {
                    double roll = random.NextDouble();
                    if (roll < 0.05) dostepnosc[dzien][lekarz.Symbol] = TypDostepnosci.BardzoChce;
                    else if (roll < 0.25) dostepnosc[dzien][lekarz.Symbol] = TypDostepnosci.Chce;
                    else if (roll < 0.95) dostepnosc[dzien][lekarz.Symbol] = TypDostepnosci.Moge;
                    else dostepnosc[dzien][lekarz.Symbol] = TypDostepnosci.MogeWarunkowo;
                    dostepneDni.Remove(dzien);
                }

                // Krok 4: Uzupełnienie deklaracjami negatywnymi
                foreach (var dzien in dostepneDni) // Pozostałe dni
                {
                    if (random.NextDouble() < 0.05) // 5% szans na "Inny Dyżur"
                    {
                        dostepnosc[dzien][lekarz.Symbol] = TypDostepnosci.DyzurInny;
                    }
                    else
                    {
                        dostepnosc[dzien][lekarz.Symbol] = TypDostepnosci.Niedostepny;
                    }
                }
            }

            return new GrafikWejsciowy
            {
                Lekarze = lekarze,
                LimityDyzurow = limity,
                Dostepnosc = dostepnosc
            };
        }
    }
}