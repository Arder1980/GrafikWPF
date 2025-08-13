using System;
using System.Collections.Generic;
using System.Linq;

namespace GrafikWPF
{
    public static class ConstraintValidationService
    {
        public static List<Lekarz> GetValidCandidatesForDay(
            DateTime dzien,
            GrafikWejsciowy daneWejsciowe,
            IReadOnlyDictionary<DateTime, Lekarz?> aktualnePrzypisania,
            IReadOnlyDictionary<string, int> aktualneOblozenie,
            IReadOnlySet<string> wykorzystaneDyzuryW)
        {
            var dniMiesiaca = daneWejsciowe.DniWMiesiacu;
            var lekarzDniaPoprzedniego = dzien > dniMiesiaca.First() && aktualnePrzypisania.TryGetValue(dzien.AddDays(-1), out var wczorajszyLekarz) ? wczorajszyLekarz : null;

            var kandydaci = new List<Lekarz>();
            foreach (var lekarz in daneWejsciowe.Lekarze.Where(l => l.IsAktywny))
            {
                if (IsValidCandidate(lekarz, dzien, daneWejsciowe, lekarzDniaPoprzedniego, aktualneOblozenie, wykorzystaneDyzuryW))
                {
                    kandydaci.Add(lekarz);
                }
            }
            return kandydaci;
        }

        private static bool IsValidCandidate(Lekarz lekarz, DateTime dzien, GrafikWejsciowy daneWejsciowe, Lekarz? lekarzDniaPoprzedniego, IReadOnlyDictionary<string, int> aktualneOblozenie, IReadOnlySet<string> wykorzystaneDyzuryW)
        {
            int maksymalnaLiczbaDyzurow = daneWejsciowe.LimityDyzurow.GetValueOrDefault(lekarz.Symbol, 0);
            if (maksymalnaLiczbaDyzurow <= 0 || aktualneOblozenie.GetValueOrDefault(lekarz.Symbol, 0) >= maksymalnaLiczbaDyzurow)
                return false;

            var dostepnoscDzis = daneWejsciowe.Dostepnosc[dzien][lekarz.Symbol];
            if (dostepnoscDzis is TypDostepnosci.Niedostepny or TypDostepnosci.Urlop or TypDostepnosci.DyzurInny)
                return false;

            bool maBardzoChce = (dostepnoscDzis == TypDostepnosci.BardzoChce);
            if (!maBardzoChce && lekarzDniaPoprzedniego?.Symbol == lekarz.Symbol)
                return false;

            if (!maBardzoChce)
            {
                var jutro = dzien.AddDays(1);
                if (daneWejsciowe.Dostepnosc.ContainsKey(jutro) && daneWejsciowe.Dostepnosc[jutro][lekarz.Symbol] == TypDostepnosci.DyzurInny)
                    return false;

                var wczoraj = dzien.AddDays(-1);
                if (daneWejsciowe.Dostepnosc.ContainsKey(wczoraj) && daneWejsciowe.Dostepnosc[wczoraj][lekarz.Symbol] == TypDostepnosci.DyzurInny)
                    return false;
            }

            if (dostepnoscDzis == TypDostepnosci.MogeWarunkowo && wykorzystaneDyzuryW.Contains(lekarz.Symbol))
                return false;

            return true;
        }


        public static void RepairSchedule(Dictionary<DateTime, Lekarz?> grafik, GrafikWejsciowy daneWejsciowe)
        {
            bool dokonanoZmiany;
            do
            {
                dokonanoZmiany = false;
                var oblozenie = ObliczOblozenie(grafik, daneWejsciowe);
                var wykorzystaneW = ObliczWykorzystaneW(grafik, daneWejsciowe);

                foreach (var lekarzSymbol in oblozenie.Keys)
                {
                    var limit = daneWejsciowe.LimityDyzurow.GetValueOrDefault(lekarzSymbol, 0);
                    while (oblozenie[lekarzSymbol] > limit)
                    {
                        var dyzuryDoUsuniecia = grafik.Where(g => g.Value?.Symbol == lekarzSymbol).ToList();
                        if (dyzuryDoUsuniecia.Any())
                        {
                            var dyzurDoUsuniecia = dyzuryDoUsuniecia
                                .OrderBy(d => GetAvailabilityScore(daneWejsciowe.Dostepnosc[d.Key][d.Value!.Symbol]))
                                .ThenByDescending(d => d.Key)
                                .First();

                            grafik[dyzurDoUsuniecia.Key] = null;
                            oblozenie[lekarzSymbol]--;
                            dokonanoZmiany = true;
                        }
                        else break;
                    }
                }

                foreach (var symbolLekarza in wykorzystaneW.Keys)
                {
                    while (wykorzystaneW[symbolLekarza] > 1)
                    {
                        var dyzuryWdoUsuniecia = grafik.FirstOrDefault(g => g.Value?.Symbol == symbolLekarza && daneWejsciowe.Dostepnosc[g.Key][g.Value.Symbol] == TypDostepnosci.MogeWarunkowo);
                        if (dyzuryWdoUsuniecia.Key != default)
                        {
                            grafik[dyzuryWdoUsuniecia.Key] = null;
                            wykorzystaneW[symbolLekarza]--;
                            dokonanoZmiany = true;
                        }
                        else break;
                    }
                }

                foreach (var dzien in daneWejsciowe.DniWMiesiacu)
                {
                    var lekarz = grafik[dzien];
                    if (lekarz == null) continue;

                    var dostepnosc = daneWejsciowe.Dostepnosc[dzien][lekarz.Symbol];
                    if (dostepnosc == TypDostepnosci.Rezerwacja) continue;

                    if (dostepnosc == TypDostepnosci.BardzoChce)
                        continue;

                    var wczoraj = dzien.AddDays(-1);
                    if (grafik.ContainsKey(wczoraj))
                    {
                        if (grafik[wczoraj]?.Symbol == lekarz.Symbol || daneWejsciowe.Dostepnosc[wczoraj][lekarz.Symbol] == TypDostepnosci.DyzurInny)
                        {
                            grafik[dzien] = null;
                            dokonanoZmiany = true;
                            continue;
                        }
                    }

                    var jutro = dzien.AddDays(1);
                    if (grafik.ContainsKey(jutro) && daneWejsciowe.Dostepnosc[jutro][lekarz.Symbol] == TypDostepnosci.DyzurInny)
                    {
                        grafik[dzien] = null;
                        dokonanoZmiany = true;
                    }
                }

            } while (dokonanoZmiany);
        }

        private static int GetAvailabilityScore(TypDostepnosci typ)
        {
            return typ switch
            {
                TypDostepnosci.MogeWarunkowo => 0,
                TypDostepnosci.Moge => 1,
                TypDostepnosci.Chce => 2,
                TypDostepnosci.BardzoChce => 3,
                TypDostepnosci.Rezerwacja => 99,
                _ => -1
            };
        }

        private static Dictionary<string, int> ObliczOblozenie(IReadOnlyDictionary<DateTime, Lekarz?> genes, GrafikWejsciowy daneWejsciowe)
        {
            var oblozenie = daneWejsciowe.Lekarze.ToDictionary(l => l.Symbol, l => 0);
            foreach (var lekarz in genes.Values.Where(l => l != null))
            {
                if (lekarz != null) oblozenie[lekarz.Symbol]++;
            }
            return oblozenie;
        }

        private static Dictionary<string, int> ObliczWykorzystaneW(IReadOnlyDictionary<DateTime, Lekarz?> genes, GrafikWejsciowy daneWejsciowe)
        {
            var wykorzystane = daneWejsciowe.Lekarze.ToDictionary(l => l.Symbol, l => 0);
            foreach (var para in genes)
            {
                if (para.Value != null && daneWejsciowe.Dostepnosc.ContainsKey(para.Key) && daneWejsciowe.Dostepnosc[para.Key][para.Value.Symbol] == TypDostepnosci.MogeWarunkowo)
                {
                    wykorzystane[para.Value.Symbol]++;
                }
            }
            return wykorzystane;
        }
    }
}