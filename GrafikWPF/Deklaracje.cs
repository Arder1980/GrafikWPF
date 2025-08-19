#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace GrafikWPF
{
    /// <summary>
    /// Jedno źródło prawdy dla deklaracji dostępności:
    /// - kody skrótów (---, WAR, MOG, CHC, BCH, REZ, DYZ, URL),
    /// - pełne nazwy do UI,
    /// - semantyka (co wolno / czego nie wolno),
    /// - pomocnicze parse/format.
    /// </summary>
    public static class Deklaracje
    {
        // ------- Kody skrótów używane w logach i tabelach nagłówkowych -------
        private static readonly IReadOnlyDictionary<TypDostepnosci, string> _kody =
            new Dictionary<TypDostepnosci, string>
            {
                { TypDostepnosci.Niedostepny,     "---" },
                { TypDostepnosci.MogeWarunkowo,   "WAR" },
                { TypDostepnosci.Moge,            "MOG" },
                { TypDostepnosci.Chce,            "CHC" },
                { TypDostepnosci.BardzoChce,      "BCH" },
                { TypDostepnosci.Rezerwacja,      "REZ" },
                { TypDostepnosci.DyzurInny,       "DYZ" },
                { TypDostepnosci.Urlop,           "URL" },
            };

        public static string Kod(TypDostepnosci t) => _kody[t];

        // ------- Pełne nazwy do UI (nie używamy skrótów w oknach) -------
        public static string PelnaNazwa(TypDostepnosci t) => t switch
        {
            TypDostepnosci.Niedostepny => "---",
            TypDostepnosci.MogeWarunkowo => "Mogę warunkowo",
            TypDostepnosci.Moge => "Mogę",
            TypDostepnosci.Chce => "Chcę",
            TypDostepnosci.BardzoChce => "Bardzo chcę",
            TypDostepnosci.Rezerwacja => "Rezerwacja",
            TypDostepnosci.DyzurInny => "Dyżur (inny)",
            TypDostepnosci.Urlop => "Urlop",
            _ => t.ToString()
        };

        // ------- Semantyka (znaczenia) deklaracji -------
        // Czy ten typ pozwala w ogóle przypisać dyżur w danym dniu?
        public static bool CzyDopuszczaPrzydzial(TypDostepnosci t) =>
            t is TypDostepnosci.Moge
             or TypDostepnosci.MogeWarunkowo
             or TypDostepnosci.Chce
             or TypDostepnosci.BardzoChce
             or TypDostepnosci.Rezerwacja;

        // Czy jest twardą blokadą dnia (nie można przydzielić)?
        public static bool CzyBlokujeTwardoDzien(TypDostepnosci t) =>
            t is TypDostepnosci.Niedostepny
             or TypDostepnosci.Urlop
             or TypDostepnosci.DyzurInny;

        // Czy to rezerwacja (dzień jest z góry przydzielony konkretnej osobie)?
        public static bool CzyRezerwacja(TypDostepnosci t) =>
            t == TypDostepnosci.Rezerwacja;

        // Czy wolno złamać zakaz „dzień po dniu” oraz „±1 od DYZ”?
        // (tylko BardzoChce łagodzi te dwa zakazy)
        public static bool CzyLagodziZakazSasiedztwa(TypDostepnosci t) =>
            t == TypDostepnosci.BardzoChce;

        // Czy to warunkowe „Mogę” (limit 1 / lekarz / miesiąc)?
        public static bool CzyWarunkowa(TypDostepnosci t) =>
            t == TypDostepnosci.MogeWarunkowo;

        // Waga preferencji do metryki „Zgodność z ważnością deklaracji”
        // (REZ nie jest preferencją, to twarde przypisanie).
        public static int WagaPreferencji(TypDostepnosci t) => t switch
        {
            TypDostepnosci.BardzoChce => 3, // najważniejsze „chcę”
            TypDostepnosci.Chce => 2,
            TypDostepnosci.Moge => 1,
            TypDostepnosci.MogeWarunkowo => 0,
            _ => -100 // blokady itp.
        };

        // ------- Wsparcie dla UI: listy i mapy nazw -------
        /// <summary>
        /// Mapowanie „przyjaznych” nazw UI → enum (do ComboBoxów, importu CSV itd.).
        /// </summary>
        public static Dictionary<string, TypDostepnosci> UiMap()
        {
            return new()
            {
                { "---",               TypDostepnosci.Niedostepny },
                { "Mogę warunkowo",    TypDostepnosci.MogeWarunkowo },
                { "Mogę",              TypDostepnosci.Moge },
                { "Chcę",              TypDostepnosci.Chce },
                { "Bardzo chcę",       TypDostepnosci.BardzoChce },
                { "Rezerwacja",        TypDostepnosci.Rezerwacja },
                { "Dyżur (inny)",      TypDostepnosci.DyzurInny },
                { "Urlop",             TypDostepnosci.Urlop },
            };
        }

        /// <summary>
        /// Lista elementów do UI: (Label, Code, Enum) – gdy chcesz jednocześnie pokazywać pełną nazwę i kod.
        /// </summary>
        public static IReadOnlyList<(string Label, string Code, TypDostepnosci Typ)> UiLista() =>
            Enum.GetValues(typeof(TypDostepnosci))
                .Cast<TypDostepnosci>()
                .Select(t => (PelnaNazwa(t), Kod(t), t))
                .ToList();

        // ------- Parse z kodów skrótów / ciągów wejściowych -------
        public static bool TryParseKod(string? s, out TypDostepnosci t)
        {
            t = TypDostepnosci.Niedostepny;
            if (string.IsNullOrWhiteSpace(s)) return false;

            switch (s.Trim().ToUpperInvariant())
            {
                case "---": t = TypDostepnosci.Niedostepny; return true;
                case "WAR": t = TypDostepnosci.MogeWarunkowo; return true;
                case "MOG": t = TypDostepnosci.Moge; return true;
                case "CHC": t = TypDostepnosci.Chce; return true;
                case "BCH": t = TypDostepnosci.BardzoChce; return true;
                case "REZ": t = TypDostepnosci.Rezerwacja; return true;
                case "DYZ": t = TypDostepnosci.DyzurInny; return true;
                case "URL": t = TypDostepnosci.Urlop; return true;
                default: return false;
            }
        }
    }
}
