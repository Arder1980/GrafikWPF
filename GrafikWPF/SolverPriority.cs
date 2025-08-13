using System.ComponentModel;

namespace GrafikWPF
{
    public enum SolverPriority
    {
        [Description("Ciągłość obsady od początku miesiąca")]
        CiagloscPoczatkowa,

        [Description("Maksymalna liczba obsadzonych dni")]
        LacznaLiczbaObsadzonychDni,

        [Description("Sprawiedliwe obciążenie lekarzy")]
        SprawiedliwoscObciazenia,

        [Description("Równomierne rozłożenie dyżurów w czasie")]
        RownomiernoscRozlozenia
    }
}