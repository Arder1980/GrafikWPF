namespace GrafikWPF.Heuristics
{
    public static class LocalSearch
    {
        // Bardzo lekka poprawa: próby swap/relocate, akceptuj gdy poprawia metryki.
        public static void Improve(int[] assignments, GrafikWejsciowy data, int maxIters = 2000)
        {
            var days = data.DniWMiesiacu.Count;
            var wl = new Dictionary<string, int>();
            foreach (var l in data.Lekarze) if (l.IsAktywny) wl[l.Symbol] = 0;
            for (int d = 0; d < days; d++) if (assignments[d] >= 0) wl[data.Lekarze[assignments[d]].Symbol]++;

            var best = Score(assignments, data);
            var rnd = new Random(17);

            for (int it = 0; it < maxIters; it++)
            {
                int a = rnd.Next(days), b = rnd.Next(days);
                if (a == b) continue;
                (assignments[a], assignments[b]) = (assignments[b], assignments[a]);

                var sc = Score(assignments, data);
                if (Better(sc, best)) best = sc;
                else (assignments[a], assignments[b]) = (assignments[b], assignments[a]);
            }
        }

        private static RozwiazanyGrafik Score(int[] a, GrafikWejsciowy d)
        {
            var map = new Dictionary<DateTime, Lekarz?>();
            var ob = new Dictionary<string, int>();
            foreach (var l in d.Lekarze) if (l.IsAktywny) ob[l.Symbol] = 0;
            for (int i = 0; i < d.DniWMiesiacu.Count; i++)
            {
                var day = d.DniWMiesiacu[i];
                if (a[i] >= 0) { var L = d.Lekarze[a[i]]; map[day] = L; ob[L.Symbol]++; }
                else map[day] = null;
            }
            return EvaluationAndScoringService.CalculateMetrics(map, ob, d);
        }
        private static bool Better(RozwiazanyGrafik x, RozwiazanyGrafik y)
        {
            // uproszczona, ale deterministyczna kolejność ważna dla seeda
            if (x.LiczbaDniObsadzonych != y.LiczbaDniObsadzonych) return x.LiczbaDniObsadzonych > y.LiczbaDniObsadzonych;
            if (x.DlugoscCiaguPoczatkowego != y.DlugoscCiaguPoczatkowego) return x.DlugoscCiaguPoczatkowego > y.DlugoscCiaguPoczatkowego;
            var xf = 1.0 / (1.0 + x.WskaznikSprawiedliwosci); var yf = 1.0 / (1.0 + y.WskaznikSprawiedliwosci);
            if (xf != yf) return xf > yf;
            var xr = 1.0 / (1.0 + x.WskaznikRownomiernosci); var yr = 1.0 / (1.0 + y.WskaznikRownomiernosci);
            if (xr != yr) return xr > yr;
            if (x.ZrealizowaneBardzoChce != y.ZrealizowaneBardzoChce) return x.ZrealizowaneBardzoChce > y.ZrealizowaneBardzoChce;
            if (x.ZrealizowaneChce != y.ZrealizowaneChce) return x.ZrealizowaneChce > y.ZrealizowaneChce;
            if (x.ZrealizowaneMoge != y.ZrealizowaneMoge) return x.ZrealizowaneMoge > y.ZrealizowaneMoge;
            return false;
        }
    }
}
