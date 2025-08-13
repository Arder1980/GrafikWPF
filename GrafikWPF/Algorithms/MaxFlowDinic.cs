namespace GrafikWPF.Algorithms
{
    // Prosty Dinic (int capacity). Tablicowe struktury, bez LINQ.
    public sealed class MaxFlowDinic
    {
        private readonly int _n;
        private readonly List<Edge>[] _g;
        private int[] _level;
        private int[] _it;

        private sealed class Edge
        {
            public int To, Rev, Cap;
            public Edge(int to, int rev, int cap) { To = to; Rev = rev; Cap = cap; }
        }

        public MaxFlowDinic(int n)
        {
            _n = n;
            _g = new List<Edge>[n];
            for (int i = 0; i < n; i++) _g[i] = new List<Edge>(8);
            _level = new int[n];
            _it = new int[n];
        }

        public void AddEdge(int u, int v, int cap)
        {
            var a = new Edge(v, _g[v].Count, cap);
            var b = new Edge(u, _g[u].Count, 0);
            _g[u].Add(a); _g[v].Add(b);
        }

        private bool Bfs(int s, int t)
        {
            Array.Fill(_level, -1);
            var q = new Queue<int>();
            _level[s] = 0; q.Enqueue(s);
            while (q.Count > 0)
            {
                int v = q.Dequeue();
                foreach (var e in _g[v])
                {
                    if (e.Cap <= 0) continue;
                    if (_level[e.To] >= 0) continue;
                    _level[e.To] = _level[v] + 1;
                    if (e.To == t) return true;
                    q.Enqueue(e.To);
                }
            }
            return _level[t] >= 0;
        }

        private int Dfs(int v, int t, int f)
        {
            if (v == t) return f;
            for (int i = _it[v]; i < _g[v].Count; i++, _it[v] = i)
            {
                var e = _g[v][i];
                if (e.Cap <= 0 || _level[v] + 1 != _level[e.To]) continue;
                int d = Dfs(e.To, t, Math.Min(f, e.Cap));
                if (d <= 0) continue;
                e.Cap -= d;
                _g[e.To][e.Rev].Cap += d;
                return d;
            }
            return 0;
        }

        public int MaxFlow(int s, int t, int need = int.MaxValue)
        {
            int flow = 0;
            while (flow < need && Bfs(s, t))
            {
                Array.Fill(_it, 0);
                int f;
                while (flow < need && (f = Dfs(s, t, need - flow)) > 0) flow += f;
            }
            return flow;
        }
    }
}
