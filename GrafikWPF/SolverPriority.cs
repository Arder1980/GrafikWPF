using System.ComponentModel;

namespace GrafikWPF
{
    // Enum do identyfikacji priorytetów. Atrybuty Description posłużą nam do wyświetlania nazw w UI.
    public enum SolverPriority
    {
        [Description("Ciągłość obsady od początku miesiąca")]
        CiagloscPoczatkowa,

        [Description("Maksymalna liczba obsadzonych dni")]
        LacznaLiczbaObsadzonychDni,

        [Description("Maksymalna liczba dyżurów 'Bardzo chcę'")]
        ZrealizowaneBardzoChce,

        [Description("Maksymalna liczba dyżurów 'Chcę'")]
        ZrealizowaneChce,

        [Description("Równomierne obciążenie lekarzy")]
        RownomiernoscObciazenia
    }
}