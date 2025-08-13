namespace GrafikWPF
{
    public class ReservationSolverWrapper : IGrafikSolver
    {
        private readonly GrafikWejsciowy _oryginalneDane;
        private readonly IGrafikSolver _wewnetrznySolver;
        private readonly Dictionary<DateTime, Lekarz> _rezerwacje = new();

        public ReservationSolverWrapper(SolverType typSolvera, GrafikWejsciowy daneWejsciowe, List<SolverPriority> kolejnoscPriorytetow, IProgress<double>? progress, CancellationToken token)
        {
            _oryginalneDane = daneWejsciowe;

            // Krok 1: Pre-procesing - znajdź i zweryfikuj rezerwacje
            var (zweryfikowaneRezerwacje, zredukowaneDane) = PrzygotujProblemDlaSolvera(daneWejsciowe);
            _rezerwacje = zweryfikowaneRezerwacje;

            // Krok 2: Stwórz właściwy solver, który zajmie się resztą problemu
            _wewnetrznySolver = SolverFactory.CreateSolver(typSolvera, zredukowaneDane, kolejnoscPriorytetow, progress, token);
        }

        private (Dictionary<DateTime, Lekarz> rezerwacje, GrafikWejsciowy zredukowaneDane) PrzygotujProblemDlaSolvera(GrafikWejsciowy dane)
        {
            var rezerwacje = new Dictionary<DateTime, Lekarz>();
            var zredukowaneDni = new Dictionary<DateTime, Dictionary<string, TypDostepnosci>>();
            var zredukowaneLimity = new Dictionary<string, int>(dane.LimityDyzurow);

            // Znajdź wszystkie rezerwacje
            foreach (var dzien in dane.DniWMiesiacu)
            {
                foreach (var lekarz in dane.Lekarze)
                {
                    if (dane.Dostepnosc[dzien].GetValueOrDefault(lekarz.Symbol) == TypDostepnosci.Rezerwacja)
                    {
                        rezerwacje.Add(dzien, lekarz);
                        break;
                    }
                }
            }

            // Zweryfikuj spójność rezerwacji i zredukuj limity
            var rezerwacjeLekarza = rezerwacje.GroupBy(r => r.Value.Symbol)
                                              .ToDictionary(g => g.Key, g => g.OrderBy(kv => kv.Key).ToList());

            foreach (var para in rezerwacjeLekarza)
            {
                var symbolLekarza = para.Key;
                var jegoRezerwacje = para.Value;

                // Sprawdź, czy liczba rezerwacji nie przekracza limitu
                if (jegoRezerwacje.Count > zredukowaneLimity.GetValueOrDefault(symbolLekarza, 0))
                {
                    throw new InvalidOperationException($"Błąd w danych wejściowych: Lekarz {symbolLekarza} ma {jegoRezerwacje.Count} rezerwacji, a jego limit dyżurów to {zredukowaneLimity.GetValueOrDefault(symbolLekarza, 0)}.");
                }
                zredukowaneLimity[symbolLekarza] -= jegoRezerwacje.Count;

                // Sprawdź konflikty między samymi rezerwacjami (np. dzień po dniu)
                for (int i = 0; i < jegoRezerwacje.Count - 1; i++)
                {
                    var obecnyDzien = jegoRezerwacje[i].Key;
                    var nastepnyDzien = jegoRezerwacje[i + 1].Key;
                    if ((nastepnyDzien - obecnyDzien).TotalDays == 1)
                    {
                        // Wyjątek tylko dla "Bardzo Chcę" - Rezerwacja nie jest wyjątkiem
                        throw new InvalidOperationException($"Błąd w danych wejściowych: Lekarz {symbolLekarza} ma dwie rezerwacje dzień po dniu ({obecnyDzien:dd.MM} i {nastepnyDzien:dd.MM}), co łamie zasadę odpoczynku.");
                    }
                }
            }

            // Stwórz zredukowany problem (tylko dni bez rezerwacji)
            foreach (var dzien in dane.DniWMiesiacu)
            {
                if (!rezerwacje.ContainsKey(dzien))
                {
                    zredukowaneDni.Add(dzien, dane.Dostepnosc[dzien]);
                }
            }

            return (rezerwacje, new GrafikWejsciowy
            {
                Lekarze = dane.Lekarze,
                Dostepnosc = zredukowaneDni,
                LimityDyzurow = zredukowaneLimity
            });
        }


        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            // Krok 3: Uruchom właściwy solver na zredukowanym problemie
            var wynikCzesciowy = _wewnetrznySolver.ZnajdzOptymalneRozwiazanie();

            // Krok 4: Scal wyniki - połącz przydzielone rezerwacje z grafikiem wygenerowanym przez solver
            var finalnePrzypisania = new Dictionary<DateTime, Lekarz?>(_rezerwacje.ToDictionary(kvp => kvp.Key, kvp => (Lekarz?)kvp.Value));
            foreach (var przypisanie in wynikCzesciowy.Przypisania)
            {
                finalnePrzypisania[przypisanie.Key] = przypisanie.Value;
            }

            // Przelicz metryki na nowo dla pełnego, scalonego grafiku
            var finalneOblozenie = _oryginalneDane.Lekarze.ToDictionary(l => l.Symbol, l => 0);
            foreach (var przypisanie in finalnePrzypisania.Values)
            {
                if (przypisanie != null) finalneOblozenie[przypisanie.Symbol]++;
            }

            return EvaluationAndScoringService.CalculateMetrics(finalnePrzypisania, finalneOblozenie, _oryginalneDane);
        }
    }
}