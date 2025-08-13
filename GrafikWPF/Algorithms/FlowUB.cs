namespace GrafikWPF.Algorithms
{
    public enum AvMask : byte { None = 0, BC = 1, Chce = 2, MogeLike = 4, Any = 7 }

    public static class FlowUB
    {
        // Buduje graf: S -> dni -> lekarze -> T
        // dayCap = 1 jeśli dana kategoria ruchu ma być liczona, inaczej 0.
        public static int UBCount(
            int days, int docs,
            Func<int, int, AvMask> avMask,           // (day,doc) -> AvMask
            Func<int, int> remCapPerDoc,            // doc -> limit - workload
            Func<int, bool> dayAllowed)             // day -> czy dopuszczamy 1 jednostkę
        {
            int N = 2 + days + docs;
            int S = days + docs, T = S + 1;
            var din = new MaxFlowDinic(N);

            for (int d = 0; d < days; d++)
            {
                if (!dayAllowed(d)) continue;
                din.AddEdge(S, d, 1);
            }
            for (int p = 0; p < docs; p++)
            {
                int cap = remCapPerDoc(p);
                if (cap > 0) din.AddEdge(days + p, T, cap);
            }
            for (int d = 0; d < days; d++)
            {
                if (!dayAllowed(d)) continue;
                for (int p = 0; p < docs; p++)
                {
                    var m = avMask(d, p);
                    if (m != AvMask.None) din.AddEdge(d, days + p, 1);
                }
            }
            return din.MaxFlow(S, T);
        }
    }
}
