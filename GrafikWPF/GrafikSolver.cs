using System;
using System.Collections.Generic;
using System.Linq;

namespace GrafikWPF
{
    public class GrafikSolver
    {
        private readonly GrafikWejsciowy _daneWejsciowe;
        private readonly List<DateTime> _dni;
        private RozwiazanyGrafik? _najlepszyWynik;
        private readonly IProgress<double>? _progressReporter;
        private readonly List<SolverPriority> _kolejnoscPriorytetow; // NOWE POLE

        private class StanWewnetrzny
        {
            public Dictionary<string, int> Oblozenie { get; }
            public HashSet<string> WykorzystaneDyzuryW { get; }
            public Dictionary<DateTime, Lekarz?> Przypisania { get; }

            public StanWewnetrzny(Dictionary<string, int> oblozenie, HashSet<string> wykorzystaneW, Dictionary<DateTime, Lekarz?> przypisania)
            {
                Oblozenie = oblozenie;
                WykorzystaneDyzuryW = wykorzystaneW;
                Przypisania = przypisania;
            }
        }

        // ZMIANA: Konstruktor przyjmuje teraz listę priorytetów
        public GrafikSolver(GrafikWejsciowy daneWejsciowe, List<SolverPriority> kolejnoscPriorytetow, IProgress<double>? progress = null)
        {
            _daneWejsciowe = daneWejsciowe;
            _dni = _daneWejsciowe.DniWMiesiacu;
            _najlepszyWynik = null;
            _progressReporter = progress;
            _kolejnoscPriorytetow = kolejnoscPriorytetow; // Zapisujemy kolejność
        }

        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            var stanPoczatkowy = new StanWewnetrzny(
                _daneWejsciowe.Lekarze.ToDictionary(l => l.Symbol, l => 0),
                new HashSet<string>(),
                new Dictionary<DateTime, Lekarz?>()
            );
            ZnajdzRekurencyjnie(0, stanPoczatkowy);
            return _najlepszyWynik ?? StworzWynikZStanu(stanPoczatkowy);
        }

        private void ZnajdzRekurencyjnie(int indexDnia, StanWewnetrzny aktualnyStan)
        {
            if (indexDnia % 3 == 0)
            {
                _progressReporter?.Report((double)indexDnia / _dni.Count);
            }

            if (indexDnia >= _dni.Count)
            {
                var ukonczonyWynik = StworzWynikZStanu(aktualnyStan);
                if (JestLepszy(ukonczonyWynik, _najlepszyWynik))
                {
                    _najlepszyWynik = ukonczonyWynik;
                }
                _progressReporter?.Report(1.0);
                return;
            }

            var dzien = _dni[indexDnia];
            var kandydaci = ZnajdzWaznychKandydatow(dzien, aktualnyStan);

            if (kandydaci.Count == 0)
            {
                if (_najlepszyWynik != null && indexDnia < _najlepszyWynik.DlugoscCiaguPoczatkowego)
                {
                    return;
                }
                var stanBezObsady = new StanWewnetrzny(
                    aktualnyStan.Oblozenie,
                    aktualnyStan.WykorzystaneDyzuryW,
                    new Dictionary<DateTime, Lekarz?>(aktualnyStan.Przypisania) { [dzien] = null }
                );
                ZnajdzRekurencyjnie(indexDnia + 1, stanBezObsady);
            }
            else
            {
                foreach (var kandydat in kandydaci)
                {
                    var noweOblozenie = new Dictionary<string, int>(aktualnyStan.Oblozenie) { [kandydat.Symbol] = aktualnyStan.Oblozenie[kandydat.Symbol] + 1 };
                    var noweW = new HashSet<string>(aktualnyStan.WykorzystaneDyzuryW);
                    if (_daneWejsciowe.Dostepnosc[dzien][kandydat.Symbol] == TypDostepnosci.MogeWarunkowo)
                    {
                        noweW.Add(kandydat.Symbol);
                    }
                    var stanZObsada = new StanWewnetrzny(
                        noweOblozenie,
                        noweW,
                        new Dictionary<DateTime, Lekarz?>(aktualnyStan.Przypisania) { [dzien] = kandydat }
                    );
                    ZnajdzRekurencyjnie(indexDnia + 1, stanZObsada);
                }
            }
        }

        private List<Lekarz> ZnajdzWaznychKandydatow(DateTime dzien, StanWewnetrzny stan)
        {
            var kandydaci = new List<Lekarz>();
            var lekarzDniaPoprzedniego = dzien > _dni.First() && stan.Przypisania.TryGetValue(dzien.AddDays(-1), out var wczorajszyLekarz) ? wczorajszyLekarz : null;

            foreach (var lekarz in _daneWejsciowe.Lekarze)
            {
                int maksymalnaLiczbaDyzurow = _daneWejsciowe.LimityDyzurow.GetValueOrDefault(lekarz.Symbol, 0);
                if (maksymalnaLiczbaDyzurow <= 0 || stan.Oblozenie[lekarz.Symbol] >= maksymalnaLiczbaDyzurow) continue;

                var dostepnoscDzis = _daneWejsciowe.Dostepnosc[dzien][lekarz.Symbol];
                bool maBardzoChce = (dostepnoscDzis == TypDostepnosci.BardzoChce);

                if (dostepnoscDzis is TypDostepnosci.Niedostepny or TypDostepnosci.Urlop or TypDostepnosci.DyzurInny) continue;

                if (!maBardzoChce && lekarzDniaPoprzedniego?.Symbol == lekarz.Symbol) continue;

                if (!maBardzoChce)
                {
                    if ((dzien < _dni.Last() && _daneWejsciowe.Dostepnosc[dzien.AddDays(1)][lekarz.Symbol] == TypDostepnosci.DyzurInny) ||
                        (dzien > _dni.First() && _daneWejsciowe.Dostepnosc[dzien.AddDays(-1)][lekarz.Symbol] == TypDostepnosci.DyzurInny))
                    {
                        continue;
                    }
                }

                if (dostepnoscDzis == TypDostepnosci.MogeWarunkowo && stan.WykorzystaneDyzuryW.Contains(lekarz.Symbol)) continue;

                kandydaci.Add(lekarz);
            }
            return kandydaci;
        }

        // PRZEBUDOWANA METODA: Używa pętli i dynamicznej listy priorytetów
        private bool JestLepszy(RozwiazanyGrafik nowy, RozwiazanyGrafik? stary)
        {
            if (stary == null) return true;

            foreach (var priorytet in _kolejnoscPriorytetow)
            {
                switch (priorytet)
                {
                    case SolverPriority.CiagloscPoczatkowa:
                        if (nowy.DlugoscCiaguPoczatkowego > stary.DlugoscCiaguPoczatkowego) return true;
                        if (nowy.DlugoscCiaguPoczatkowego < stary.DlugoscCiaguPoczatkowego) return false;
                        break;
                    case SolverPriority.LacznaLiczbaObsadzonychDni:
                        if (nowy.LiczbaDniObsadzonych > stary.LiczbaDniObsadzonych) return true;
                        if (nowy.LiczbaDniObsadzonych < stary.LiczbaDniObsadzonych) return false;
                        break;
                    case SolverPriority.ZrealizowaneBardzoChce:
                        if (nowy.ZrealizowaneBardzoChce > stary.ZrealizowaneBardzoChce) return true;
                        if (nowy.ZrealizowaneBardzoChce < stary.ZrealizowaneBardzoChce) return false;
                        break;
                    case SolverPriority.ZrealizowaneChce:
                        if (nowy.ZrealizowaneChce > stary.ZrealizowaneChce) return true;
                        if (nowy.ZrealizowaneChce < stary.ZrealizowaneChce) return false;
                        break;
                    case SolverPriority.RownomiernoscObciazenia:
                        // Uwaga: Mniejsza wartość jest lepsza
                        if (nowy.WskaznikRownomiernosci < stary.WskaznikRownomiernosci) return true;
                        if (nowy.WskaznikRownomiernosci > stary.WskaznikRownomiernosci) return false;
                        break;
                }
            }
            // Jeśli po wszystkich sprawdzeniach jest remis, nowy grafik nie jest lepszy
            return false;
        }

        private RozwiazanyGrafik StworzWynikZStanu(StanWewnetrzny stan)
        {
            var p = _dni.ToDictionary(d => d, d => stan.Przypisania.GetValueOrDefault(d));
            int cp = 0;
            foreach (var key in _dni) { if (p.TryGetValue(key, out var l) && l != null) cp++; else break; }

            int zrealizowaneChce = 0;
            int zrealizowaneBardzoChce = 0;
            foreach (var wpis in p.Where(x => x.Value != null))
            {
                var typ = _daneWejsciowe.Dostepnosc[wpis.Key][wpis.Value!.Symbol];
                if (typ == TypDostepnosci.Chce) zrealizowaneChce++;
                else if (typ == TypDostepnosci.BardzoChce) zrealizowaneBardzoChce++;
            }

            var obciazeniaProcentowe = new List<double>();
            foreach (var lekarz in _daneWejsciowe.Lekarze)
            {
                int maksymalnaLiczbaDyzurow = _daneWejsciowe.LimityDyzurow.GetValueOrDefault(lekarz.Symbol, 0);
                if (maksymalnaLiczbaDyzurow > 0)
                {
                    double obciazenie = stan.Oblozenie.GetValueOrDefault(lekarz.Symbol, 0);
                    obciazeniaProcentowe.Add(obciazenie * 100.0 / maksymalnaLiczbaDyzurow);
                }
            }

            double wskaznikRownomiernosci = 0.0;
            if (obciazeniaProcentowe.Count > 1)
            {
                double srednia = obciazeniaProcentowe.Average();
                double sumaKwadratowRoznic = obciazeniaProcentowe.Sum(val => (val - srednia) * (val - srednia));
                wskaznikRownomiernosci = Math.Sqrt(sumaKwadratowRoznic / obciazeniaProcentowe.Count);
            }

            return new RozwiazanyGrafik
            {
                Przypisania = p,
                DlugoscCiaguPoczatkowego = cp,
                ZrealizowaneChce = zrealizowaneChce,
                ZrealizowaneBardzoChce = zrealizowaneBardzoChce,
                FinalneOblozenieLekarzy = new Dictionary<string, int>(stan.Oblozenie),
                WskaznikRownomiernosci = wskaznikRownomiernosci
            };
        }
    }
}