namespace GrafikWPF.Heuristics
{
    public static class CandidateOrdering
    {
        // Zwraca grupę porządkową 0..3 (BC,Chce,Moge,MogeWar) dla danego TypDostepnosci
        public static int GroupFor(TypDostepnosci av)
        {
            return av switch
            {
                TypDostepnosci.BardzoChce => 0,
                TypDostepnosci.Chce => 1,
                TypDostepnosci.Moge => 2,
                TypDostepnosci.MogeWarunkowo => 3,
                _ => 99
            };
        }
    }
}
