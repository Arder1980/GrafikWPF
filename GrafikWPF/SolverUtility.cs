using System;
using System.Collections.Generic;
using System.Linq;

namespace GrafikWPF
{
    public class SolverUtility
    {
        private readonly GrafikWejsciowy _daneWejsciowe;
        private readonly Random _random = new();

        public SolverUtility(GrafikWejsciowy daneWejsciowe)
        {
            _daneWejsciowe = daneWejsciowe;
        }

        public Dictionary<DateTime, Lekarz?> StworzChciweRozwiazaniePoczatkowe()
        {
            var genes = new Dictionary<DateTime, Lekarz?>();
            var oblozenie = _daneWejsciowe.Lekarze.ToDictionary(l => l.Symbol, l => 0);
            var wykorzystaneW = new HashSet<string>();

            foreach (var dzien in _daneWejsciowe.DniWMiesiacu)
            {
                var kandydaci = ConstraintValidationService.GetValidCandidatesForDay(dzien, _daneWejsciowe, genes, oblozenie, wykorzystaneW);
                if (kandydaci.Any())
                {
                    var najlepszyKandydat = kandydaci
                        .OrderByDescending(l => _daneWejsciowe.Dostepnosc[dzien][l.Symbol])
                        .ThenBy(l => oblozenie[l.Symbol])
                        .First();

                    genes[dzien] = najlepszyKandydat;
                    oblozenie[najlepszyKandydat.Symbol]++;
                    if (_daneWejsciowe.Dostepnosc[dzien][najlepszyKandydat.Symbol] == TypDostepnosci.MogeWarunkowo)
                    {
                        wykorzystaneW.Add(najlepszyKandydat.Symbol);
                    }
                }
                else
                {
                    genes[dzien] = null;
                }
            }
            return genes;
        }

        public Dictionary<DateTime, Lekarz?> StworzLosoweRozwiazanie()
        {
            var genes = new Dictionary<DateTime, Lekarz?>();
            var oblozenie = _daneWejsciowe.Lekarze.ToDictionary(l => l.Symbol, l => 0);
            var wykorzystaneW = new HashSet<string>();

            foreach (var dzien in _daneWejsciowe.DniWMiesiacu)
            {
                var kandydaci = ConstraintValidationService.GetValidCandidatesForDay(dzien, _daneWejsciowe, genes, oblozenie, wykorzystaneW);
                if (kandydaci.Any())
                {
                    var wybrany = kandydaci[_random.Next(kandydaci.Count)];
                    genes[dzien] = wybrany;
                    oblozenie[wybrany.Symbol]++;
                    if (_daneWejsciowe.Dostepnosc[dzien][wybrany.Symbol] == TypDostepnosci.MogeWarunkowo)
                    {
                        wykorzystaneW.Add(wybrany.Symbol);
                    }
                }
                else
                {
                    genes[dzien] = null;
                }
            }
            return genes;
        }

        public Dictionary<DateTime, Lekarz?> GenerujSasiada(Dictionary<DateTime, Lekarz?> obecnyGrafik)
        {
            var nowyGrafik = new Dictionary<DateTime, Lekarz?>(obecnyGrafik);
            var dzienDoZmiany = _daneWejsciowe.DniWMiesiacu[_random.Next(_daneWejsciowe.DniWMiesiacu.Count)];

            var oblozenie = ObliczOblozenie(nowyGrafik);
            var wykorzystaneW = ObliczWykorzystaneW(nowyGrafik).Keys.ToHashSet();

            var kandydaci = ConstraintValidationService.GetValidCandidatesForDay(dzienDoZmiany, _daneWejsciowe, nowyGrafik, oblozenie, wykorzystaneW);

            if (kandydaci.Any())
            {
                nowyGrafik[dzienDoZmiany] = kandydaci[_random.Next(kandydaci.Count)];
            }
            else
            {
                nowyGrafik[dzienDoZmiany] = null;
            }

            return nowyGrafik;
        }

        public Dictionary<string, int> ObliczOblozenie(IReadOnlyDictionary<DateTime, Lekarz?> genes)
        {
            var oblozenie = _daneWejsciowe.Lekarze.ToDictionary(l => l.Symbol, l => 0);
            foreach (var lekarz in genes.Values)
            {
                if (lekarz != null)
                {
                    oblozenie[lekarz.Symbol]++;
                }
            }
            return oblozenie;
        }

        private Dictionary<string, int> ObliczWykorzystaneW(IReadOnlyDictionary<DateTime, Lekarz?> genes)
        {
            var wykorzystane = _daneWejsciowe.Lekarze.ToDictionary(l => l.Symbol, l => 0);
            foreach (var para in genes)
            {
                if (para.Value != null && _daneWejsciowe.Dostepnosc.ContainsKey(para.Key) && _daneWejsciowe.Dostepnosc[para.Key][para.Value.Symbol] == TypDostepnosci.MogeWarunkowo)
                {
                    wykorzystane[para.Value.Symbol]++;
                }
            }
            return wykorzystane;
        }
    }
}