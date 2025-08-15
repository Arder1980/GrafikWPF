#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace GrafikWPF
{
    /// <summary>
    /// Backtracking z priorytetem ciągłości (gdy wybrany przez użytkownika).
    /// - Rolling horizon (K<=7) egzekwowany twardo na froncie ciągłości,
    /// - CRP: wybór kandydata maksymalizującego gwarantowany prefiks w oknie,
    /// - (WYŁĄCZONE w ETAP 1) rezerwowanie CH/BC w przyszłość,
    /// - Adaptacyjna lokalna naprawa pierwszej dziury (cofka 5–7),
    /// - MW = 1 na osobę,
    /// - Reguły sąsiedztwa: BC może łamać „dzień-po-dniu” i „Inny dyżur ±1”; MG/CH/MW nie mogą.
    /// </summary>
    public sealed class BacktrackingSolver : IGrafikSolver
    {
        // Kody preferencji (twarda dostępność):
        private const byte PREF_NONE = 0; // brak / niedostępny
        private const byte PREF_MW = 1; // Mogę warunkowo (max 1 w miesiącu)
        private const byte PREF_MG = 2; // Mogę
        private const byte PREF_CH = 3; // Chcę
        private const byte PREF_BC = 4; // Bardzo chcę
        private const byte PREF_RZ = 5; // Rezerwacja (musi być)
        private const byte PREF_OD = 6; // Dyżur (inny) – dzień +/-1 (dla MG/CH/MW działa blokada)

        // Stałe wyszukiwania
        private const int UNASSIGNED = int.MinValue;
        private const int EMPTY = -1;

        // Rolling horizon / CRP
        private const int RH_MIN_K = 2;
        private const int RH_MAX_K = 7; // okno przodu dla prefiksu

        // (Pozostawione dla ewentualnego późniejszego użycia w ETAP 5; aktualnie nieużywane)
        private const int CH_PROTECT_K = 7; // okno dla balansu U(c)/R(c) – TERAZ WYŁĄCZONE

        // LocalRepair
        private const int LR_MIN_BACK = 5;
        private const int LR_MAX_BACK = 7;
        private const int LR_FWD = 4;

        // Pomocnicze
        private const int UNIT_PROP_LIMIT = 2;

        // Dane wejściowe / kontekst
        private readonly GrafikWejsciowy _input;
        private readonly IReadOnlyList<SolverPriority> _priorities;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _token;

        private readonly List<DateTime> _days;
        private readonly List<Lekarz> _docs;
        private readonly Dictionary<string, int> _docIdxBySymbol;
        private readonly Dictionary<DateTime, Dictionary<string, TypDostepnosci>> _av;
        private readonly int[] _limitsByDoc;

        // Prekomputacja dostępności
        private readonly byte[,] _pref; // [day, doc] -> PREF_*

        // Stan bieżący
        private readonly int[] _assign; // day -> doc idx; EMPTY/UNASSIGNED
        private readonly int[] _work;   // ile przydzielono lekarzowi
        private readonly int[] _mwUsed; // ile MW wykorzystał lekarz
        private int _assignedFilled;     // ile dni obsadzonych

        // Prefiks (ciągłość od początku)
        private bool _isPrefixActive;
        private int _prefixFirstHole = -1;
        private bool _localRepairTried = false;

        // Najlepszy wynik
        private RozwiazanyGrafik? _best;
        private long[]? _bestScore;

        public BacktrackingSolver(GrafikWejsciowy input,
                                  IReadOnlyList<SolverPriority> priorities,
                                  IProgress<double>? progress,
                                  CancellationToken token)
        {
            _input = input;
            _priorities = priorities ?? Array.Empty<SolverPriority>();
            _progress = progress;
            _token = token;

            _days = _input.Dostepnosc.Keys.OrderBy(d => d).ToList();
            _docs = _input.Lekarze.OrderBy(l => l.Nazwisko).ThenBy(l => l.Imie).ToList();

            _docIdxBySymbol = new Dictionary<string, int>(_docs.Count);
            for (int i = 0; i < _docs.Count; i++)
                _docIdxBySymbol[_docs[i].Symbol] = i;

            _av = _input.Dostepnosc;

            _limitsByDoc = new int[_docs.Count];
            for (int i = 0; i < _docs.Count; i++)
            {
                var sym = _docs[i].Symbol;
                _limitsByDoc[i] = _input.LimityDyzurow.TryGetValue(sym, out var lim) ? lim : _days.Count;
            }

            _pref = new byte[_days.Count, _docs.Count];
            PrecomputeAvailability();

            _assign = Enumerable.Repeat(UNASSIGNED, _days.Count).ToArray();
            _work = new int[_docs.Count];
            _mwUsed = new int[_docs.Count];
            _assignedFilled = 0;

            _isPrefixActive = true;
        }

        // ====== Prekomputacja ======
        private void PrecomputeAvailability()
        {
            for (int d = 0; d < _days.Count; d++)
            {
                var date = _days[d];
                for (int p = 0; p < _docs.Count; p++)
                {
                    var sym = _docs[p].Symbol;
                    var td = (_av.TryGetValue(date, out var map) && map.TryGetValue(sym, out var t))
                        ? t : TypDostepnosci.Niedostepny;

                    _pref[d, p] = td switch
                    {
                        TypDostepnosci.MogeWarunkowo => PREF_MW,
                        TypDostepnosci.Moge => PREF_MG,
                        TypDostepnosci.Chce => PREF_CH,
                        TypDostepnosci.BardzoChce => PREF_BC,
                        TypDostepnosci.Rezerwacja => PREF_RZ,
                        TypDostepnosci.DyzurInny => PREF_OD,
                        _ => PREF_NONE
                    };
                }
            }
        }

        // ====== API ======
        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            SolverDiagnostics.Log("=== Start BacktrackingSolver ===");
            SolverDiagnostics.Log($"Dni: {_days.Count}, lekarze: {_docs.Count}");
            SolverDiagnostics.Log($"Priorytety: {string.Join(", ", _priorities)}");

            // === ETAP 0: Nagłówek statusu polityk (tylko log, zero zmian logiki) ===
            try
            {
                const string solverName = "Backtracking";
                IReadOnlyList<SolverPriority> pri =
                    _priorities ?? DataManager.AppData.KolejnoscPriorytetowSolvera;

                bool chProtectEnabled = false; // WYŁĄCZONE
                bool bcBreaksAdjacent = true;  // BC może łamać ±1
                int mwMax = 1;     // maks. 1 MW na osobę

                var rhK = (min: RH_MIN_K, max: RH_MAX_K);
                var lrBack = (min: LR_MIN_BACK, max: LR_MAX_BACK);
                var lrFwd = (min: LR_FWD, max: LR_FWD);

                SolverPolicyStatus.LogStartupHeader(
                    solverName: solverName,
                    priorities: pri,
                    chProtectEnabled: chProtectEnabled,
                    bcBreaksAdjacent: bcBreaksAdjacent,
                    mwMax: mwMax,
                    rhK: rhK,
                    lrBack: lrBack,
                    lrFwd: lrFwd
                );
            }
            catch (Exception ex)
            {
                SolverDiagnostics.Log("[Policy] Header logging failed: " + ex.Message);
            }
            // === /ETAP 0 ===

            LogLimits();
            LogLegendAndAvailability();

            BuildGreedyIncumbent();

            try { Search(0); }
            catch (OperationCanceledException) { SolverDiagnostics.Log("Przerwano (CancellationToken)."); }

            if (_best is null)
            {
                var dict = new Dictionary<DateTime, Lekarz?>();
                for (int d = 0; d < _days.Count; d++)
                    dict[_days[d]] = _assign[d] >= 0 ? _docs[_assign[d]] : null;
                _best = new RozwiazanyGrafik { Przypisania = dict };
            }

            // Podsumowanie polityk po biegu (diagnostyka)
            try
            {
                int total = _days.Count;
                int filled = _best.Przypisania.Count(kv => kv.Value != null);
                int prefix = 0;
                for (int i = 0; i < _days.Count; i++)
                {
                    if (!_best.Przypisania.TryGetValue(_days[i], out var l) || l is null) break;
                    prefix++;
                }
                int empty = total - filled;
                SolverPolicyStatus.LogPostRunSummary("Backtracking", total, filled, prefix, empty);
            }
            catch { /* no-op */ }

            return _best!;
        }

        // ====== Backtracking ======
        private void Search(int depth)
        {
            _token.ThrowIfCancellationRequested();

            if (depth % 4 == 0)
                _progress?.Report((_assignedFilled + CountEmpties()) / (double)_days.Count);

            int day = NextDayToBranch();
            if (day == -1) { ConsiderAsBest(); return; }

            if (!BoundAllowsImprovement(day)) return;

            var candidates = OrderCandidates(day);

            // Rolling-horizon + CRP tylko na froncie prefiksu (gdy ciągłość to priorytet #1)
            bool continuityFirst = _priorities.Count > 0 && _priorities[0] == SolverPriority.CiagloscPoczatkowa;
            bool atPrefixFront = _isPrefixActive && day == PrefixLength();

            if (continuityFirst && atPrefixFront && candidates.Count > 0)
            {
                // (ETAP 1) BRAK twardej ochrony CH/BC – nie wzywamy FilterByCoverBalance

                // Rolling horizon: wymagaj wykonalności okna
                var filtered = FilterByRollingFeasibility(day, candidates);
                if (filtered.Count == 0)
                {
                    SolverDiagnostics.Log($"[RH] 0 kandydatów po filtrze okna – {FormatDay(day)} → wymuszone PUSTO i LocalRepair.");
                    HandleFirstHole(day);
                    return;
                }

                // CRP: wybierz kandydata, który maksymalizuje gwarantowany prefiks
                candidates = OrderByCRP(day, filtered, RH_MAX_K);
            }
            else
            {
                // Po prefiksie: brak kar za „przyszłe CH/BC” – czyste dynamiczne priorytety
                if (candidates.Count > 1)
                    candidates.Sort((a, b) => CompareCandidates(day, a, b));
            }

            if (candidates.Count == 0)
            {
                bool firstHole = _isPrefixActive && day == PrefixLength();
                if (firstHole) HandleFirstHole(day);
                else
                {
                    PlaceEmpty(day);
                    Search(depth + 1);
                    UnplaceEmpty(day);
                }
                return;
            }

            foreach (var doc in candidates)
            {
                if (!IsHardFeasible(day, doc)) continue;

                SolverDiagnostics.Log($"WYBÓR: {FormatDay(day)} ← {_docs[doc].Symbol} [{PrefToString(_pref[day, doc])}]");

                Place(day, doc, out byte codeToday);

                int autoSteps = 0;
                if (codeToday == PREF_RZ) autoSteps = UnitPropagateReservations(day);

                Search(depth + 1);

                if (autoSteps > 0) UnpropagateReservations(day, autoSteps);

                Unplace(day, doc, codeToday);
            }
        }

        private void HandleFirstHole(int day)
        {
            _isPrefixActive = false;
            _prefixFirstHole = day;
            SolverDiagnostics.Log($"[BT] Pierwsza pustka na {FormatDay(day)} – koniec prefiksu.");

            if (!_localRepairTried)
            {
                _localRepairTried = true;
                TryLocalRepairAround(day);
            }

            PlaceEmpty(day);
            Search(0 + 1);
            UnplaceEmpty(day);
        }

        // ====== Wybór kolejnej zmiennej (MRV po prefiksie) ======
        private int NextDayToBranch()
        {
            if (_isPrefixActive)
            {
                for (int d = 0; d < _days.Count; d++)
                    if (_assign[d] == UNASSIGNED) return d;
                return -1;
            }

            int bestDay = -1, bestCnt = int.MaxValue;

            for (int d = 0; d < _days.Count; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;

                int c = 0;
                for (int p = 0; p < _docs.Count; p++)
                {
                    if (IsHardFeasible(d, p))
                    {
                        c++;
                        if (c >= bestCnt) break;
                    }
                }
                if (c < bestCnt)
                {
                    bestCnt = c;
                    bestDay = d;
                    if (bestCnt <= 1) break;
                }
            }
            return bestDay;
        }

        // ====== Branch&Bound – górne oszacowanie ======
        private bool BoundAllowsImprovement(int firstUnassignedDay)
        {
            if (_bestScore == null) return true;

            long obsSoFar = _assignedFilled;
            long remain = _days.Count - (firstUnassignedDay < 0 ? _days.Count : firstUnassignedDay);
            long obsBound = obsSoFar + remain;

            long contSoFar = CurrentPrefixLengthFast();
            long contBound = _isPrefixActive ? contSoFar + (_days.Count - contSoFar) : contSoFar;

            long fairBound = 0;
            long evenBound = 0;

            var map = new Dictionary<SolverPriority, long>
            {
                { SolverPriority.LacznaLiczbaObsadzonychDni, obsBound },
                { SolverPriority.CiagloscPoczatkowa,         contBound },
                { SolverPriority.SprawiedliwoscObciazenia,   fairBound },
                { SolverPriority.RownomiernoscRozlozenia,     evenBound }
            };

            var vec = new long[_priorities.Count];
            for (int i = 0; i < _priorities.Count; i++)
                vec[i] = map.TryGetValue(_priorities[i], out var v) ? v : 0;

            return LexGreater(vec, _bestScore);
        }

        private int CurrentPrefixLengthFast()
        {
            int len = 0;
            for (; len < _days.Count; len++)
            {
                if (_assign[len] == UNASSIGNED) break;
                if (_assign[len] == EMPTY) break;
            }
            return len;
        }

        // ====== Rolling horizon / CRP ======

        // (B) Rolling-horizon – wymagaj wykonalności okna
        private List<int> FilterByRollingFeasibility(int day, List<int> cand)
        {
            for (int K = RH_MIN_K; K <= RH_MAX_K; K++)
            {
                var ok = new List<int>(cand.Count);
                foreach (var p in cand)
                    if (ExistsFullWindow(day, p, K)) ok.Add(p);

                if (ok.Count > 0)
                {
                    SolverDiagnostics.Log($"[RH] {ok.Count}/{cand.Count} kandydatów przechodzi okno K={K}.");
                    return ok;
                }
            }
            return new List<int>();
        }

        // (C) CRP – preferuj kandydata maksymalizującego gwarantowany prefiks w oknie
        private List<int> OrderByCRP(int day, List<int> cand, int K)
        {
            var scored = new List<(int doc, int pref)>(cand.Count);
            foreach (var p in cand)
            {
                int len = MaxWindowPrefix(day, p, K);
                scored.Add((p, len));
            }
            scored.Sort((x, y) =>
            {
                if (x.pref != y.pref) return y.pref.CompareTo(x.pref);
                // remis: tie-break wg miękkich priorytetów i preferencji
                return CompareCandidates(day, x.doc, y.doc);
            });
            var ordered = new List<int>(scored.Count);
            foreach (var s in scored) ordered.Add(s.doc);
            SolverDiagnostics.Log($"[CRP] Kolejność po CRP: {string.Join(", ", ordered.ConvertAll(i => _docs[i].Symbol))}");
            return ordered;
        }

        // ====== Kontekst okna RH/CRP ======
        private sealed class WinCtx
        {
            public List<int> Days = new();
            public Dictionary<int, int> PosByDay = new();
            public int[] LocalAssign = Array.Empty<int>();  // pos -> doc or UNASSIGNED
            public int[] LocalWorkInc = Array.Empty<int>(); // per doc
            public int[] LocalMwInc = Array.Empty<int>();   // per doc
        }

        private bool ExistsFullWindow(int day, int forcedDoc, int K)
        {
            int end = Math.Min(_days.Count - 1, day + K);
            var ctx = NewWindowCtx(day, end);

            int pos0 = ctx.PosByDay[day];
            if (!TryPlaceInWindow(ctx, pos0, forcedDoc, forced: true))
                return false;

            return WindowDFS_AllAssigned(ctx);
        }

        private int MaxWindowPrefix(int day, int forcedDoc, int K)
        {
            int end = Math.Min(_days.Count - 1, day + K);
            var ctx = NewWindowCtx(day, end);

            int pos0 = ctx.PosByDay[day];
            if (!TryPlaceInWindow(ctx, pos0, forcedDoc, forced: true))
                return 0;

            return WindowDFS_MaxPrefix(ctx);
        }

        private WinCtx NewWindowCtx(int start, int end)
        {
            var ctx = new WinCtx();
            for (int d = start; d <= end; d++)
            {
                ctx.PosByDay[d] = ctx.Days.Count;
                ctx.Days.Add(d);
            }
            ctx.LocalAssign = Enumerable.Repeat(UNASSIGNED, ctx.Days.Count).ToArray();
            ctx.LocalWorkInc = new int[_docs.Count];
            ctx.LocalMwInc = new int[_docs.Count];
            return ctx;
        }

        private bool WindowDFS_AllAssigned(WinCtx ctx)
        {
            _token.ThrowIfCancellationRequested();

            int bestPos = -1, bestCnt = int.MaxValue;
            List<int>? bestCands = null;

            // MRV w oknie
            for (int pos = 0; pos < ctx.Days.Count; pos++)
            {
                if (ctx.LocalAssign[pos] != UNASSIGNED) continue;

                int d = ctx.Days[pos];
                if (_assign[d] != UNASSIGNED)
                {
                    var fixedDoc = _assign[d];
                    if (!TryPlaceInWindow(ctx, pos, fixedDoc, forced: true)) return false;
                    continue;
                }

                var cand = WindowCandidates(ctx, pos);
                int c = cand.Count;
                if (c == 0) return false;
                if (c < bestCnt)
                {
                    bestCnt = c; bestPos = pos; bestCands = cand;
                    if (bestCnt <= 1) break;
                }
            }

            bool allAssigned = true;
            for (int pos = 0; pos < ctx.Days.Count; pos++)
                if (ctx.LocalAssign[pos] == UNASSIGNED) { allAssigned = false; break; }
            if (allAssigned) return true;

            Debug.Assert(bestPos >= 0 && bestCands != null);

            // Priorytet CH/BC w oknie (tie-break)
            bestCands.Sort((a, b) =>
            {
                int ra = PrefRank(_pref[ctx.Days[bestPos], a]);
                int rb = PrefRank(_pref[ctx.Days[bestPos], b]);
                if (ra != rb) return rb.CompareTo(ra);

                double fa = RatioAfterWithLocal(a, ctx.LocalWorkInc[a]);
                double fb = RatioAfterWithLocal(b, ctx.LocalWorkInc[b]);
                int cmp = fa.CompareTo(fb);
                if (cmp != 0) return cmp;

                int da = NearestAssignedDistanceGlobal(ctx.Days[bestPos], a);
                int db = NearestAssignedDistanceGlobal(ctx.Days[bestPos], b);
                return db.CompareTo(da);
            });

            foreach (var doc in bestCands)
            {
                if (!TryPlaceInWindow(ctx, bestPos, doc, forced: false)) continue;
                if (WindowDFS_AllAssigned(ctx)) return true;
                UndoPlaceInWindow(ctx, bestPos, doc);
            }
            return false;
        }

        private int WindowDFS_MaxPrefix(WinCtx ctx)
        {
            _token.ThrowIfCancellationRequested();

            int start = ctx.Days[0];
            int end = ctx.Days[^1];
            int best = 0;

            void DFS()
            {
                // Znajdź pierwszy nieprzydzielony w oknie
                int nextPos = -1;
                for (int i = 0; i < ctx.Days.Count; i++)
                {
                    if (ctx.LocalAssign[i] == UNASSIGNED) { nextPos = i; break; }
                }

                if (nextPos == -1)
                {
                    best = Math.Max(best, end - start + 1);
                    return;
                }

                // Aktualny prefiks od startu
                int cur = 0;
                for (int d = start; d <= end; d++)
                {
                    int p = ctx.PosByDay[d];
                    int g = GetDocAssignedConsideringWindow(ctx, d);
                    if (g >= 0 || (_assign[d] >= 0)) cur++;
                    else break;
                }
                if (cur > best) best = cur;

                int dday = ctx.Days[nextPos];
                if (_assign[dday] != UNASSIGNED)
                {
                    var fixedDoc = _assign[dday];
                    if (TryPlaceInWindow(ctx, nextPos, fixedDoc, forced: true))
                    {
                        DFS();
                        UndoPlaceInWindow(ctx, nextPos, fixedDoc);
                    }
                    return;
                }

                var cand = WindowCandidates(ctx, nextPos);
                if (cand.Count == 0) return;

                cand.Sort((a, b) =>
                {
                    int ra = PrefRank(_pref[dday, a]);
                    int rb = PrefRank(_pref[dday, b]);
                    if (ra != rb) return rb.CompareTo(ra);
                    double fa = RatioAfterWithLocal(a, ctx.LocalWorkInc[a]);
                    double fb = RatioAfterWithLocal(b, ctx.LocalWorkInc[b]);
                    int cmp = fa.CompareTo(fb);
                    if (cmp != 0) return cmp;
                    int da = NearestAssignedDistanceGlobal(dday, a);
                    int db = NearestAssignedDistanceGlobal(dday, b);
                    return db.CompareTo(da);
                });

                foreach (var doc in cand)
                {
                    if (!TryPlaceInWindow(ctx, nextPos, doc, forced: false)) continue;
                    DFS();
                    UndoPlaceInWindow(ctx, nextPos, doc);
                }
            }

            DFS();
            return best;
        }

        private List<int> WindowCandidates(WinCtx ctx, int pos)
        {
            int d = ctx.Days[pos];
            var res = new List<int>(_docs.Count);
            for (int p = 0; p < _docs.Count; p++)
                if (IsFeasibleInWindow(ctx, pos, p)) res.Add(p);
            return res;
        }

        private bool IsFeasibleInWindow(WinCtx ctx, int pos, int doc)
        {
            int d = ctx.Days[pos];
            byte av = _pref[d, doc];

            if (av == PREF_NONE || av == PREF_OD) return false; // OD jako „dzień inny” (sam dzień) – nie stajemy na OD

            int used = _work[doc] + ctx.LocalWorkInc[doc];
            if (used >= _limitsByDoc[doc]) return false;

            if (av == PREF_MW && (_mwUsed[doc] + ctx.LocalMwInc[doc]) >= 1) return false;

            bool isBC = (av == PREF_BC);

            // Zakaz dzień-po-dniu: BC może łamać
            if (!isBC)
            {
                int prev = d - 1, next = d + 1;
                if (prev >= 0)
                {
                    int prevDoc = GetDocAssignedConsideringWindow(ctx, prev);
                    if (prevDoc == doc) return false;
                }
                if (next < _days.Count)
                {
                    int nextDoc = GetDocAssignedConsideringWindow(ctx, next);
                    if (nextDoc == doc) return false;
                }
            }

            // Sąsiedztwo „Inny dyżur” ±1: **BC może łamać** (MG/CH/MW nie mogą)
            if (!isBC)
            {
                int pv = d - 1, nx = d + 1;
                if (pv >= 0 && _pref[pv, doc] == PREF_OD) return false;
                if (nx < _days.Count && _pref[nx, doc] == PREF_OD) return false;
            }

            return true;
        }

        private int GetDocAssignedConsideringWindow(WinCtx ctx, int dayIdx)
        {
            if (_assign[dayIdx] >= 0) return _assign[dayIdx];
            if (ctx.PosByDay.TryGetValue(dayIdx, out int pos))
            {
                int local = ctx.LocalAssign[pos];
                return local >= 0 ? local : -2;
            }
            return -2;
        }

        private bool TryPlaceInWindow(WinCtx ctx, int pos, int doc, bool forced)
        {
            int d = ctx.Days[pos];

            if (!forced && _assign[d] >= 0 && _assign[d] != doc) return false;

            if (!IsFeasibleInWindow(ctx, pos, doc)) return false;

            ctx.LocalAssign[pos] = doc;
            ctx.LocalWorkInc[doc]++;
            if (_pref[d, doc] == PREF_MW) ctx.LocalMwInc[doc]++;
            return true;
        }

        private void UndoPlaceInWindow(WinCtx ctx, int pos, int doc)
        {
            int d = ctx.Days[pos];
            ctx.LocalAssign[pos] = UNASSIGNED;
            ctx.LocalWorkInc[doc]--;
            if (_pref[d, doc] == PREF_MW) ctx.LocalMwInc[doc]--;
        }

        private double RatioAfterWithLocal(int doc, int localInc)
        {
            double lim = Math.Max(1, _limitsByDoc[doc]);
            return (_work[doc] + localInc + 1) / lim;
        }

        // ====== Lokalna naprawa ======
        private void TryLocalRepairAround(int holeDay)
        {
            _token.ThrowIfCancellationRequested();

            int bestPrefixLen = CurrentPrefixLengthFast();
            int appliedBack = 0;

            int budgetMs = GetLocalRepairBudgetMs();
            var swGlobal = Stopwatch.StartNew();

            for (int back = LR_MIN_BACK; back <= LR_MAX_BACK; back++)
            {
                if (swGlobal.ElapsedMilliseconds > budgetMs) break;

                int start = Math.Max(0, holeDay - back);
                int end = Math.Min(_days.Count - 1, holeDay + LR_FWD);

                SolverDiagnostics.Log($"[LocalRepair] Okno: {FormatDay(start)}..{FormatDay(end)} (dziura: {FormatDay(holeDay)}, back={back})");

                var snapAssign = (int[])_assign.Clone();
                var snapWork = (int[])_work.Clone();
                var snapMw = (int[])_mwUsed.Clone();
                int snapFilled = _assignedFilled;
                bool snapPrefix = _isPrefixActive;
                int snapPrefixHole = _prefixFirstHole;

                // wyczyść okno
                for (int d = start; d <= end; d++)
                {
                    if (_assign[d] >= 0)
                    {
                        int who = _assign[d];
                        byte code = _pref[d, who];
                        Unplace(d, who, code);
                        _assign[d] = UNASSIGNED;
                    }
                    else if (_assign[d] == EMPTY) _assign[d] = UNASSIGNED;
                }
                _isPrefixActive = false;

                var sw = Stopwatch.StartNew();
                int localBudget = Math.Max(350, Math.Min(1000, budgetMs / 2));
                long[]? bestLocalScore = null;
                int[]? bestLocalAssign = null;
                int bestLocalPrefix = bestPrefixLen;
                int nodes = 0;

                void DFS(int d)
                {
                    if (sw.ElapsedMilliseconds > localBudget) return;
                    _token.ThrowIfCancellationRequested();

                    while (d <= end && _assign[d] != UNASSIGNED) d++;
                    if (d > end)
                    {
                        int pref = CurrentPrefixLengthFast();
                        if (pref > bestLocalPrefix)
                        {
                            bestLocalPrefix = pref;
                            bestLocalAssign = (int[])_assign.Clone();
                            bestLocalScore = null;
                            SolverDiagnostics.Log($"[LocalRepair] Lepszy prefiks: {pref}");
                        }
                        else if (pref == bestLocalPrefix)
                        {
                            var sol = SnapshotToSolution();
                            var sc = EvaluateSolution(sol);
                            if (bestLocalScore == null || LexGreater(sc, bestLocalScore))
                            {
                                bestLocalAssign = (int[])_assign.Clone();
                                bestLocalScore = sc;
                                SolverDiagnostics.Log($"[LocalRepair] Lepszy kandydat (remis prefiksu): vec={FormatScore(sc)}");
                            }
                        }
                        return;
                    }

                    nodes++;
                    var cand = OrderCandidates(d);

                    // mini-prefiks w oknie: (ETAP 1) bez twardej ochrony CH/BC; RH + CRP zostają
                    if (_priorities.Count > 0 && _priorities[0] == SolverPriority.CiagloscPoczatkowa)
                    {
                        var ok = FilterByRollingFeasibility(d, cand);
                        if (ok.Count > 0) cand = OrderByCRP(d, ok, RH_MAX_K);
                        else cand.Clear();
                    }

                    if (cand.Count == 0)
                    {
                        PlaceEmpty(d);
                        DFS(d + 1);
                        UnplaceEmpty(d);
                        return;
                    }

                    foreach (var doc in cand)
                    {
                        if (!IsHardFeasible(d, doc)) continue;
                        Place(d, doc, out var code);
                        DFS(d + 1);
                        Unplace(d, doc, code);
                        if (sw.ElapsedMilliseconds > localBudget) break;
                    }
                }

                try { DFS(start); }
                finally
                {
                    SolverDiagnostics.Log($"[LocalRepair] Węzły={nodes}, czas={sw.ElapsedMilliseconds} ms.");
                }

                if (bestLocalAssign != null && bestLocalPrefix > bestPrefixLen)
                {
                    Array.Copy(bestLocalAssign, _assign, _assign.Length);

                    Array.Fill(_work, 0);
                    Array.Fill(_mwUsed, 0);
                    _assignedFilled = 0;
                    for (int d = 0; d < _days.Count; d++)
                    {
                        int a = _assign[d];
                        if (a >= 0)
                        {
                            _work[a]++;
                            if (_pref[d, a] == PREF_MW) _mwUsed[a]++;
                            _assignedFilled++;
                        }
                    }
                    _isPrefixActive = false;
                    appliedBack = back;
                    SolverDiagnostics.Log($"[LocalRepair] Zastosowano (prefiks {bestPrefixLen} → {bestLocalPrefix}, back={back}).");
                    break;
                }
                else
                {
                    Array.Copy(snapAssign, _assign, _assign.Length);
                    Array.Copy(snapWork, _work, _work.Length);
                    Array.Copy(snapMw, _mwUsed, _mwUsed.Length);
                    _assignedFilled = snapFilled;
                    _isPrefixActive = snapPrefix;
                    _prefixFirstHole = snapPrefixHole;
                }
            }

            SolverDiagnostics.Log(appliedBack > 0
                ? $"[LocalRepair] Zakończono – użyto back={appliedBack}"
                : "[LocalRepair] Brak poprawy prefiksu.");
        }

        private int GetLocalRepairBudgetMs()
        {
            int baseMs = 700 + (_days.Count * _docs.Count) / 7;
            if (baseMs < 450) baseMs = 450;
            if (baseMs > 1400) baseMs = 1400;
            return baseMs;
        }

        // ====== Lista kandydatów i porządkowanie (ogólne) ======
        private List<int> OrderCandidates(int day)
        {
            var legal = new List<int>(_docs.Count);
            for (int p = 0; p < _docs.Count; p++)
                if (IsHardFeasible(day, p)) legal.Add(p);

            if (legal.Count == 0) return legal;

            legal.Sort((a, b) => CompareCandidates(day, a, b));
            LogCandidates(day, legal);
            return legal;
        }

        private int CompareCandidates(int day, int a, int b)
        {
            byte avA = _pref[day, a];
            byte avB = _pref[day, b];

            // Rezerwacja ma bezwzględne pierwszeństwo
            int rzA = avA == PREF_RZ ? 1 : 0;
            int rzB = avB == PREF_RZ ? 1 : 0;
            if (rzA != rzB) return rzB.CompareTo(rzA);

            // Priorytety użytkownika (miękkie)
            foreach (var pr in _priorities)
            {
                int cmp = 0;
                switch (pr)
                {
                    case SolverPriority.CiagloscPoczatkowa:
                        {
                            // drobny lookahead: preferuj kandydata, który nie zabija dnia+1
                            bool keepA = KeepsNextFeasible(day, a);
                            bool keepB = KeepsNextFeasible(day, b);
                            cmp = keepB.CompareTo(keepA);
                            if (cmp != 0) return cmp;
                            break;
                        }
                    case SolverPriority.LacznaLiczbaObsadzonychDni:
                        break;
                    case SolverPriority.SprawiedliwoscObciazenia:
                        {
                            double ra = RatioAfter(a);
                            double rb = RatioAfter(b);
                            cmp = ra.CompareTo(rb);
                            if (cmp != 0) return cmp;
                            break;
                        }
                    case SolverPriority.RownomiernoscRozlozenia:
                        {
                            int da = NearestAssignedDistance(day, a);
                            int db = NearestAssignedDistance(day, b);
                            cmp = db.CompareTo(da);
                            if (cmp != 0) return cmp;
                            break;
                        }
                }
            }

            // Tie-break: CH/BC > MG > MW
            int pa = PrefRank(avA), pb = PrefRank(avB);
            if (pa != pb) return pb.CompareTo(pa);

            // Potem mniej przydziałów, większy zapas limitu
            int wCmp = _work[a].CompareTo(_work[b]);
            if (wCmp != 0) return wCmp;

            int remA = _limitsByDoc[a] - _work[a];
            int remB = _limitsByDoc[b] - _work[b];
            int remCmp = remB.CompareTo(remA);
            if (remCmp != 0) return remCmp;

            return a.CompareTo(b);
        }

        private double RatioAfter(int doc)
        {
            double lim = Math.Max(1, _limitsByDoc[doc]);
            return (_work[doc] + 1) / lim;
        }

        private int NearestAssignedDistance(int day, int doc)
        {
            int best = int.MaxValue;
            for (int d = day - 1; d >= 0; d--)
                if (_assign[d] == doc) { best = Math.Min(best, day - d); break; }

            for (int d = day + 1; d < _days.Count; d++)
                if (_assign[d] == doc) { best = Math.Min(best, d - day); break; }

            return best == int.MaxValue ? 9999 : best;
        }

        private int NearestAssignedDistanceGlobal(int day, int doc) => NearestAssignedDistance(day, doc);

        // ====== Twarde zasady (globalnie) ======
        private bool IsHardFeasible(int day, int doc)
        {
            if (_work[doc] >= _limitsByDoc[doc]) return false;

            byte av = _pref[day, doc];

            if (av == PREF_NONE || av == PREF_OD) return false; // OD = „Dyżur inny” w samym dniu – nie stawiamy

            bool isBC = av == PREF_BC;

            // dzień-po-dniu: BC może łamać
            if (!isBC)
            {
                if (day > 0 && _assign[day - 1] == doc) return false;
                if (day + 1 < _days.Count && _assign[day + 1] == doc) return false;
            }

            // sąsiedztwo „Inny dyżur” ±1: **BC może łamać**, MG/CH/MW nie
            if (!isBC)
            {
                if (day > 0 && _pref[day - 1, doc] == PREF_OD) return false;
                if (day + 1 < _days.Count && _pref[day + 1, doc] == PREF_OD) return false;
            }

            if (av == PREF_MW && _mwUsed[doc] >= 1) return false;

            return true;
        }

        private bool IsHardFeasibleWithHypo(int dayToCheck, int cand, int hypoDay, int hypoDoc, byte hypoCode)
        {
            if (_work[cand] >= _limitsByDoc[cand]) return false;

            byte av = _pref[dayToCheck, cand];
            if (av == PREF_NONE || av == PREF_OD) return false;

            bool isBC = av == PREF_BC;

            // dzień-po-dniu: BC może łamać
            if (!isBC && cand == hypoDoc && Math.Abs(dayToCheck - hypoDay) == 1)
                return false;

            // sąsiedztwo „Inny dyżur” ±1: **BC może łamać**
            if (!isBC)
            {
                if (dayToCheck > 0 && _pref[dayToCheck - 1, cand] == PREF_OD) return false;
                if (dayToCheck + 1 < _days.Count && _pref[dayToCheck + 1, cand] == PREF_OD) return false;
            }

            int mw = _mwUsed[cand];
            if (cand == hypoDoc && hypoCode == PREF_MW) mw++;
            if (av == PREF_MW && mw >= 1) return false;

            return true;
        }

        private bool KeepsNextFeasible(int day, int doc)
        {
            int dNext = day + 1;
            if (dNext >= _days.Count) return true;
            if (_assign[dNext] != UNASSIGNED) return true;

            byte av0 = _pref[day, doc];
            for (int q = 0; q < _docs.Count; q++)
            {
                if (!IsHardFeasibleWithHypo(dNext, q, day, doc, av0)) continue;
                return true;
            }
            return false;
        }

        private static int PrefRank(byte code) => code switch
        {
            PREF_RZ => 5,
            PREF_BC => 4,
            PREF_CH => 3,
            PREF_MG => 2,
            PREF_MW => 1,
            _ => 0
        };

        private string PrefToString(byte code) => code switch
        {
            PREF_BC => "BC",
            PREF_CH => "CH",
            PREF_MG => "MG",
            PREF_MW => "MW",
            PREF_RZ => "RZ",
            PREF_OD => "OD",
            _ => "--"
        };

        // ====== (Wyłączone) ochrona CH/BC – pozostawione dla ETAP 5 ======
        private int UniqueCHBCForDocInWindow(int doc, int wStart, int wEnd, int day0, int doc0, byte code0)
        {
            if (wStart > wEnd) return 0;
            int U = 0;

            for (int d = wStart; d <= wEnd; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;

                bool hasChBc = false;
                for (int q = 0; q < _docs.Count; q++)
                {
                    var pv = _pref[d, q];
                    if (pv == PREF_CH || pv == PREF_BC) { hasChBc = true; break; }
                }
                if (!hasChBc) continue;

                int feasibleCh = 0;
                int who = -1;
                for (int q = 0; q < _docs.Count; q++)
                {
                    var pv = _pref[d, q];
                    if (pv != PREF_CH && pv != PREF_BC) continue;
                    if (IsHardFeasibleWithHypo(d, q, day0, doc0, code0))
                    {
                        feasibleCh++;
                        who = q;
                        if (feasibleCh >= 2) break;
                    }
                }

                if (feasibleCh == 1 && who == doc) U++;
            }
            return U;
        }

        private int RealSlotsForDocInWindow(int doc, int wStart, int wEnd, int day0, int doc0, byte code0)
        {
            if (wStart > wEnd) return 0;
            int R = 0;

            for (int d = wStart; d <= wEnd; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;

                if (IsHardFeasibleWithHypo(d, doc, day0, doc0, code0))
                    R++;
            }
            return R;
        }

        private int FutureCHPenalty(int day, int doc, int k)
        {
            // ETAP 1: ochrona CH/BC wyłączona -> brak kary
            return 0;
        }

        // ====== Operacje na stanie ======
        private void Place(int day, int doc, out byte codeToday)
        {
            codeToday = _pref[day, doc];
            _assign[day] = doc;
            _work[doc]++;
            if (codeToday == PREF_MW) _mwUsed[doc]++;
            _assignedFilled++;
        }

        private void Unplace(int day, int doc, byte codeToday)
        {
            _assign[day] = UNASSIGNED;
            _work[doc]--;
            if (codeToday == PREF_MW) _mwUsed[doc]--;
            _assignedFilled--;
        }

        private void PlaceEmpty(int day) { _assign[day] = EMPTY; }
        private void UnplaceEmpty(int day) { _assign[day] = UNASSIGNED; }

        private int UnitPropagateReservations(int fromDay)
        {
            int steps = 0;
            int d = fromDay + 1;
            while (steps < UNIT_PROP_LIMIT && d < _days.Count)
            {
                if (_assign[d] != UNASSIGNED) { d++; continue; }

                int onlyDoc = -2;
                for (int p = 0; p < _docs.Count; p++)
                {
                    byte av = _pref[d, p];
                    if (av != PREF_RZ) continue;
                    if (!IsHardFeasible(d, p)) continue;

                    if (onlyDoc == -2) onlyDoc = p;
                    else { onlyDoc = -1; break; }
                }

                if (onlyDoc >= 0)
                {
                    Place(d, onlyDoc, out _);
                    steps++;
                    d++;
                }
                else break;
            }

            if (steps > 0)
                SolverDiagnostics.Log($"[BT] Unit-prop (Rezerwacje) – auto-kroki: {steps}");

            return steps;
        }

        private void UnpropagateReservations(int fromDay, int steps)
        {
            for (int i = 1; i <= steps; i++)
            {
                int d = fromDay + i;
                if (d < 0 || d >= _days.Count) break;
                int doc = _assign[d];
                if (doc >= 0)
                {
                    byte av = _pref[d, doc];
                    Unplace(d, doc, av);
                }
            }
        }

        // ====== Scoring / incumbent ======
        private void ConsiderAsBest()
        {
            var sol = SnapshotToSolution();
            var score = EvaluateSolution(sol);

            if (_best is null || LexGreater(score, _bestScore!))
            {
                _best = sol;
                _bestScore = score;
                SolverDiagnostics.Log($"[BEST] Nowy najlepszy: vec={FormatScore(score)}");
            }
        }

        private RozwiazanyGrafik SnapshotToSolution()
        {
            var sol = new RozwiazanyGrafik { Przypisania = new Dictionary<DateTime, Lekarz?>() };
            for (int d = 0; d < _days.Count; d++)
                sol.Przypisania[_days[d]] = _assign[d] >= 0 ? _docs[_assign[d]] : null;
            return sol;
        }

        private long[] EvaluateSolution(RozwiazanyGrafik sol)
        {
            long sObs = 0;
            long sCont = 0;
            long sFair;
            long sEven;

            var perDoc = new int[_docs.Count];
            for (int d = 0; d < _days.Count; d++)
            {
                if (sol.Przypisania.TryGetValue(_days[d], out var l) && l is not null)
                {
                    sObs++;
                    if (_docIdxBySymbol.TryGetValue(l.Symbol, out var idx))
                        perDoc[idx]++;
                }
            }

            for (int d = 0; d < _days.Count; d++)
            {
                if (!sol.Przypisania.TryGetValue(_days[d], out var l) || l is null) break;
                sCont++;
            }

            long sumLimits = 0;
            var lims = new long[_docs.Count];
            for (int i = 0; i < _docs.Count; i++)
            {
                long L = Math.Max(0, _limitsByDoc[i]);
                lims[i] = L;
                sumLimits += L;
            }
            if (sObs <= 0 || sumLimits <= 0)
            {
                sFair = 0;
            }
            else
            {
                // sprawiedliwość = proporcjonalność do limitów (mniejsza suma odchyleń = lepiej)
                double sumAbs = 0.0;
                for (int i = 0; i < _docs.Count; i++)
                {
                    double expected = sObs * (lims[i] / (double)sumLimits);
                    sumAbs += Math.Abs(perDoc[i] - expected);
                }
                sFair = -(long)Math.Round(sumAbs * 1000.0);
            }

            // równomierność (prosty karny „zlepek” za bliskie sąsiedztwo)
            int penalty = 0;
            int[] perDayDoc = new int[_days.Count];
            for (int d = 0; d < _days.Count; d++)
            {
                if (sol.Przypisania.TryGetValue(_days[d], out var l) && l is not null &&
                    _docIdxBySymbol.TryGetValue(l.Symbol, out var idx))
                    perDayDoc[d] = idx;
                else
                    perDayDoc[d] = -1;
            }
            for (int d = 0; d < _days.Count; d++)
            {
                int p = perDayDoc[d];
                if (p < 0) continue;
                if (d > 0 && perDayDoc[d - 1] == p) penalty++;
                if (d + 1 < _days.Count && perDayDoc[d + 1] == p) penalty++;
            }
            sEven = -penalty;

            var map = new Dictionary<SolverPriority, long>
            {
                { SolverPriority.LacznaLiczbaObsadzonychDni, sObs },
                { SolverPriority.CiagloscPoczatkowa,         sCont },
                { SolverPriority.SprawiedliwoscObciazenia,   sFair },
                { SolverPriority.RownomiernoscRozlozenia,     sEven }
            };

            var vec = new long[_priorities.Count];
            for (int i = 0; i < _priorities.Count; i++)
            {
                var pr = _priorities[i];
                vec[i] = map.TryGetValue(pr, out var v) ? v : 0;
            }
            return vec;
        }

        private static bool LexGreater(long[] a, long[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                if (a[i] > b[i]) return true;
                if (a[i] < b[i]) return false;
            }
            return a.Length > b.Length;
        }

        private string FormatScore(long[] v) => $"[{string.Join(", ", v)}]";

        private void BuildGreedyIncumbent()
        {
            var snapshotAssign = (int[])_assign.Clone();
            var snapshotWork = (int[])_work.Clone();
            var snapshotMw = (int[])_mwUsed.Clone();
            int snapshotFilled = _assignedFilled;
            bool snapshotPrefix = _isPrefixActive;

            try
            {
                for (int day = 0; day < _days.Count; day++)
                {
                    if (_token.IsCancellationRequested) break;
                    if (_assign[day] != UNASSIGNED) continue;

                    var cand = OrderCandidates(day);
                    if (cand.Count == 0)
                    {
                        PlaceEmpty(day);
                        if (_isPrefixActive && day == PrefixLength())
                        {
                            _isPrefixActive = false;
                            _prefixFirstHole = day;
                        }
                        continue;
                    }

                    var doc = cand[0];
                    Place(day, doc, out var code);
                    if (code == PREF_RZ)
                        UnitPropagateReservations(day);
                }

                ConsiderAsBest();
                int obs = 0; foreach (var kv in _best!.Przypisania) if (kv.Value != null) obs++;
                SolverDiagnostics.Log($"Greedy incumbent: obs={obs}/{_days.Count}");
            }
            finally
            {
                Array.Copy(snapshotAssign, _assign, _assign.Length);
                Array.Copy(snapshotWork, _work, _work.Length);
                Array.Copy(snapshotMw, _mwUsed, _mwUsed.Length);
                _assignedFilled = snapshotFilled;
                _isPrefixActive = snapshotPrefix;
            }
        }

        private int CountEmpties()
        {
            int c = 0;
            for (int d = 0; d < _days.Count; d++)
                if (_assign[d] == EMPTY) c++;
            return c;
        }

        private int PrefixLength()
        {
            int len = 0;
            for (; len < _days.Count; len++)
            {
                if (_assign[len] == UNASSIGNED) break;
                if (_assign[len] == EMPTY) break;
            }
            return len;
        }

        private string FormatDay(int d) => $"{_days[d]:yyyy-MM-dd}";

        // ====== Logging ======
        private void LogLimits()
        {
            SolverDiagnostics.Log("--- Limity lekarzy ---");
            for (int i = 0; i < _docs.Count; i++)
                SolverDiagnostics.Log($"{_docs[i].Symbol}: limit={_limitsByDoc[i]}");
            SolverDiagnostics.Log("--- /Limity lekarzy ---");
        }

        private void LogLegendAndAvailability()
        {
            SolverDiagnostics.Log("Legenda: BC=BardzoChce, CH=Chce, MG=Moge, MW=MogeWarunkowo, RZ=Rezerwacja, OD=Dyżur(inny), --=brak");
            SolverDiagnostics.Log("--- Deklaracje (dzień → lekarz:deklaracja) ---");
            for (int d = 0; d < _days.Count; d++)
            {
                var date = _days[d];
                var parts = new List<string>(_docs.Count);
                for (int i = 0; i < _docs.Count; i++)
                    parts.Add($"{_docs[i].Symbol}:{PrefToString(_pref[d, i])}");
                SolverDiagnostics.Log($"{date:yyyy-MM-dd} | {string.Join(", ", parts)}");
            }
            SolverDiagnostics.Log("--- /Deklaracje (dzień → lekarz:deklaracja) ---");
        }

        private void LogCandidates(int day, List<int> cand)
        {
            SolverDiagnostics.Log($"--- Kandydaci dnia {FormatDay(day)} ---");
            SolverDiagnostics.Log($"Dzień {FormatDay(day)} (prefiks={(_isPrefixActive && day == PrefixLength() ? PrefixLength().ToString() : "—")}) – kandydaci:");
            foreach (var p in cand)
                SolverDiagnostics.Log($"  ✓ {_docs[p].Symbol} [{PrefToString(_pref[day, p])}]  (pracuje={_work[p]}, limit={_limitsByDoc[p]})");
            SolverDiagnostics.Log($"--- /Kandydaci dnia {FormatDay(day)} ---");
        }
    }
}
