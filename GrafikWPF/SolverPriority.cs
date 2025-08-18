using System.ComponentModel;

public enum SolverPriority
{
    [Description("Ciągłość obsady")]
    CiagloscPoczatkowa = 0,

    [Description("Obsada (łączna)")]
    LacznaLiczbaObsadzonychDni = 1,

    [Description("Sprawiedliwość (σ obciążeń)")]
    SprawiedliwoscObciazenia = 2,

    [Description("Równomierność (czasowa)")]
    RownomiernoscRozlozenia = 3,

    [Description("Zgodność z ważnością deklaracji")]
    ZgodnoscWaznosciDeklaracji = 4
}
