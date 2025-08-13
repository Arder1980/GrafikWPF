namespace GrafikWPF.Heuristics
{
    public static class GreedySeed
    {
        // MRV po dniach, kandydaci BC->Chce->Moge->MogeWar, w grupie najmniejszy workload.
        // Respektuje limity dyżurów.
        public static int[] Generate(GrafikWejsciowy data, out int[] seedWorkload)
        {
            int days = data.DniWMiesiacu.Count;
            var doctors = data.Lekarze.FindAll(l => l.IsAktywny);
            int D = doctors.Count;

            var limit = new int[D];
            for (int p = 0; p < D; p++) limit[p] = data.LimityDyzurow.GetValueOrDefault(doctors[p].Symbol, 0);

            var ass = new int[days];
            var wl = new int[D];
            Array.Fill(ass, -1);

            for (int step = 0; step < days; step++)
            {
                // wybór dnia o najmniejszej liczbie kandydatów
                int bestDay = -1, bestCnt = int.MaxValue, bestBC = -1;
                for (int d = 0; d < days; d++)
                {
                    if (ass[d] != -1) continue;
                    int cnt = 0, hasBC = 0;
                    for (int p = 0; p < D; p++)
                    {
                        if (wl[p] >= limit[p]) continue;
                        var sym = doctors[p].Symbol;
                        var av = data.Dostepnosc[data.DniWMiesiacu[d]].GetValueOrDefault(sym, TypDostepnosci.Niedostepny);
                        if (av is TypDostepnosci.Niedostepny or TypDostepnosci.Urlop or TypDostepnosci.DyzurInny) continue;
                        cnt++;
                        if (av == TypDostepnosci.BardzoChce) hasBC = 1;
                    }
                    if (cnt == 0) { bestDay = d; bestCnt = 0; bestBC = 0; break; }
                    if (cnt < bestCnt || (cnt == bestCnt && hasBC > bestBC))
                    { bestDay = d; bestCnt = cnt; bestBC = hasBC; }
                }

                // wybór lekarza z poszanowaniem limitów
                int sel = -1, selGroup = 999, selWl = int.MaxValue;
                for (int p = 0; p < D; p++)
                {
                    if (wl[p] >= limit[p]) continue;
                    var sym = doctors[p].Symbol;
                    var av = data.Dostepnosc[data.DniWMiesiacu[bestDay]].GetValueOrDefault(sym, TypDostepnosci.Niedostepny);
                    if (av is TypDostepnosci.Niedostepny or TypDostepnosci.Urlop or TypDostepnosci.DyzurInny) continue;
                    int g = CandidateOrdering.GroupFor(av);
                    if (g > selGroup) continue;
                    if (g < selGroup || wl[p] < selWl) { sel = p; selGroup = g; selWl = wl[p]; }
                }
                ass[bestDay] = sel;
                if (sel >= 0) wl[sel]++;
            }

            seedWorkload = wl;
            return ass;
        }
    }
}
