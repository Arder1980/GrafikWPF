using System.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using GrafikWPF.Algorithms;

namespace GrafikWPF
{
    /// <summary>
    /// BacktrackingSolver – dwuetapowy solver:
    /// F1: maksymalizacja prefiksu (ciągłość od początku – jeśli to #1).
    /// F2: dogęszczanie reszty przy zachowaniu priorytetów.
    ///
    /// Wydajnościowe kroki:
    ///  - Krok 1: mikrosito d+1 (twardy forward-check) + pełny UB (flow) co N poziomów,
    ///  - Krok 2: buckety kandydatów + tańsze tie-breaki,
    ///  - Krok 3: cache UB (suffix + okno) i okienkowe UB co każdy poziom (bezpieczne odcięcie).
    /// </summary>
    public sealed class BacktrackingSolver : IGrafikSolver
    {
        // Preferencje / kody (zgodne z projektem)
        private const byte PREF_NONE = 0;
        private const byte PREF_MW = 1; // Mogę warunkowo (max 1)
        private const byte PREF_MG = 2; // Mogę
        private const byte PREF_CH = 3; // Chcę
        private const byte PREF_BC = 4; // Bardzo chcę (może łamać sąsiedztwo)
        private const byte PREF_RZ = 5; // Rezerwacja (wycięta w wrapperze przed solverem)
        private const byte PREF_OD = 6; // DyzurInny (blokuje ±1 dla nie-BC)

        private const int UNASSIGNED = int.MinValue;
        private const int EMPTY = -1;

        private readonly GrafikWejsciowy _input;
        private readonly IReadOnlyList<SolverPriority> _priorities;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _token;

        private readonly List<DateTime> _days;
        private readonly List<Lekarz> _docs;
        private readonly Dictionary<string, int> _docIdxBySymbol;
        private readonly Dictionary<DateTime, Dictionary<string, TypDostepnosci>> _av;
        private readonly int[] _limitsByDoc;
        private readonly byte[,] _pref; // [day, doc] -> PREF_*

        // Stan rozwiązania globalny (wypluwany po F1)
        private readonly int[] _assign;     // day -> doc idx; EMPTY/UNASSIGNED
        private readonly int[] _workPerDoc; // globalne obciążenia
        private readonly int[] _mwUsed;
        private int _filled;

        // Najlepszy wynik / scoring
        private RozwiazanyGrafik? _best;
        private long[]? _bestScore;

        // F1 – prefiks
        private int _bestPrefixLen;
        private int[]? _bestPrefixAssign;
        // === KROK 4: throttling logów F1 ===
        private const int LOG_EVERY_PRUNE_D1 = 100; // loguj co 100. odcięcie d+1 (0 = wylącz)
        private const int LOG_EVERY_WINUB = 10;  // loguj co 10. okienkowy UB-cut (0 = wylącz)
        private int _cntPruneD1;
        private int _cntWinUB;

        // === KROK 1: Pełny UB + mikrosito d+1 ===
        private const bool F1_USE_FULL_UB = true;
        private const int F1_FULL_UB_EVERY = 8;  // co ile poziomów liczony pełny UB (dinic)
        // === KROK 3: Okienkowy UB + cache UB ===
        private const int F1_WIN_SIZE = 10; // rozmiar okna dni do okienkowego UB
        private const int F1_WIN_UB_EVERY = 1;  // okienko liczymy co każdy poziom
        private readonly Dictionary<(int day, ulong capHash), int> _ubCache = new(1024);
        private readonly Dictionary<(int start, int end, ulong capHash), int> _winUbCache = new(2048);

        // === KROK 2: Buckety kandydatów per dzień ===
        private List<int>[] _buckBC;
        private List<int>[] _buckCH;
        private List<int>[] _buckMG;
        private List<int>[] _buckMW;
        private bool _bucketsReady;

        // Polityki
        private readonly bool _bcBreaksAdjacent = true; // BC może łamać sąsiedztwo
        private readonly int _mwMax = 1;    // 1× MW / miesiąc

        public BacktrackingSolver(GrafikWejsciowy input,
                                  IReadOnlyList<SolverPriority> priorities,
                                  IProgress<double>? progress,
                                  CancellationToken token)
        {
            _input = input;
            _priorities = priorities;
            _progress = progress;
            _token = token;

            _days = _input.Dostepnosc.Keys.OrderBy(d => d).ToList();
            _docs = _input.Lekarze.OrderBy(l => l.Nazwisko).ThenBy(l => l.Imie).ToList();

            _docIdxBySymbol = new Dictionary<string, int>(_docs.Count);
            for (int i = 0; i < _docs.Count; i++) _docIdxBySymbol[_docs[i].Symbol] = i;

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
            _workPerDoc = new int[_docs.Count];
            _mwUsed = new int[_docs.Count];
            _filled = 0;

            _bestPrefixLen = 0;
        }

        // === KROK 4: bufor współdzielony dla F2 (mniej alokacji) ===
        private readonly List<int> _tmp = new(64);


        // ===================== API =====================
        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            SolverDiagnostics.Start("BacktrackingSolver");

            // F1 – ciągłość od początku
            F1_MaximizePrefix();

            // Odtwórz najlepszy prefiks i przejdź do F2
            if (_bestPrefixAssign != null)
            {
                ApplyPrefixSnapshot(_bestPrefixAssign, _bestPrefixLen);
                F2_MaximizeCoverageFrom(_bestPrefixLen);
            }
            else
            {
                ApplyPrefixSnapshot(Array.Empty<int>(), 0);
                F2_MaximizeCoverageFrom(0);
            }

            var sol = BuildSolution();
            var score = EvaluateSolution(sol);
            if (_best is null || Better(score, _bestScore!))
            {
                _best = sol;
                _bestScore = score;
                SolverDiagnostics.Log($"[BEST] vec={FormatScore(score)}");
            }

            SolverDiagnostics.Stop();
            return _best ?? sol;
        }

        // ===================== Prekomputacja =====================
        private void PrecomputeAvailability()
        {
            for (int d = 0; d < _days.Count; d++)
            {
                var date = _days[d];
                for (int j = 0; j < _docs.Count; j++)
                {
                    var sym = _docs[j].Symbol;
                    var td = (_av.TryGetValue(date, out var map) && map.TryGetValue(sym, out var t))
                        ? t : TypDostepnosci.Niedostepny;
                    _pref[d, j] = td switch
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

        // ===================== F1: Maksymalizacja prefiksu =====================

        // Pełny upper bound (ogón ogona d..end) – z cache
        private int F1_SuffixUpperBoundCached(int startDay, int[] curWork)
        {
            var key = (startDay, HashRemCap(curWork));
            if (_ubCache.TryGetValue(key, out var ub)) return ub;

            ub = FlowUB.UBCount(
                days: _days.Count,
                docs: _docs.Count,
                avMask: (d, p) =>
                {
                    if (d < startDay) return AvMask.None;
                    var code = _pref[d, p];
                    return (code == PREF_NONE || code == PREF_OD) ? AvMask.None : AvMask.Any;
                },
                remCapPerDoc: p => Math.Max(0, _limitsByDoc[p] - curWork[p]),
                dayAllowed: d => d >= startDay
            );
            _ubCache[key] = ub;
            return ub;
        }

        // Okienkowy upper bound [start..end] – z cache
        private int F1_WindowUpperBoundCached(int startDay, int endDay, int[] curWork)
        {
            if (endDay < startDay) return 0;
            var key = (startDay, endDay, HashRemCap(curWork));
            if (_winUbCache.TryGetValue(key, out var ub)) return ub;

            ub = FlowUB.UBCount(
                days: _days.Count,
                docs: _docs.Count,
                avMask: (d, p) =>
                {
                    if (d < startDay || d > endDay) return AvMask.None;
                    var code = _pref[d, p];
                    return (code == PREF_NONE || code == PREF_OD) ? AvMask.None : AvMask.Any;
                },
                remCapPerDoc: p => Math.Max(0, _limitsByDoc[p] - curWork[p]),
                dayAllowed: d => d >= startDay && d <= endDay
            );
            _winUbCache[key] = ub;
            return ub;
        }

        // Hash rozkładu "pozostałych pojemności" – szybki, bez przydziałów
        private ulong HashRemCap(int[] curWork)
        {
            ulong h = 1469598103934665603ul; // FNV-1a 64
            const ulong P = 1099511628211ul;
            for (int p = 0; p < _docs.Count; p++)
            {
                uint rem = (uint)Math.Max(0, _limitsByDoc[p] - curWork[p]);
                h ^= (ulong)rem ^ (ulong)((p + 1) * 16777619);
                h *= P;
            }
            return h;
        }

        private void F1_MaximizePrefix()
        {
            var curAssign = new int[_days.Count];
            Array.Fill(curAssign, UNASSIGNED);
            var curWork = new int[_docs.Count];
            var curMW = new int[_docs.Count];

            EnsureBucketsBuilt();

            void DFS(int day)
            {
                _token.ThrowIfCancellationRequested();
                _progress?.Report(day / (double)_days.Count);

                // Aktualizacja najlepszego prefiksu
                if (day > _bestPrefixLen)
                {
                    _bestPrefixLen = day;
                    _bestPrefixAssign = new int[_bestPrefixLen];
                    Array.Copy(curAssign, _bestPrefixAssign, _bestPrefixLen);
                    SolverDiagnostics.Log($"[F1] Nowy najlepszy prefiks: {_bestPrefixLen} ({FormatDay(_bestPrefixLen - 1)})");
                }
                if (day >= _days.Count) return;

                // ---- KROK 3: okienkowe UB (bezpieczne odcięcie) ----
                if (F1_WIN_UB_EVERY > 0 && (day % F1_WIN_UB_EVERY == 0))
                {
                    int W = Math.Min(F1_WIN_SIZE, _days.Count - day);
                    int end = day + W - 1;
                    int wub = F1_WindowUpperBoundCached(day, end, curWork);
                    // aby dojść do day+W, trzeba w oknie zdobyć >= W przydziałów; jeśli upper bound < W,
                    // maksymalny nowy prefiks z tej gałęzi to day + wub
                    if (_bestPrefixLen >= day + wub)
                    {
                    if (LOG_EVERY_WINUB > 0 ? (++_cntWinUB % LOG_EVERY_WINUB == 0) : true)
                       SolverDiagnostics.Log($"[F1][WIN-UB-cut] {FormatDay(day)}..{FormatDay(Math.Max(day, end))}: wub={wub}, bestPref={_bestPrefixLen} → stop");
                        return;
                    }
                }

                // ---- KROK 1: pełny UB ogona (rzadziej, droższy) ----
                if (F1_USE_FULL_UB && (day % F1_FULL_UB_EVERY == 0))
                {
                    int ub = F1_SuffixUpperBoundCached(day, curWork);
                    if (day + ub <= _bestPrefixLen)
                    {
                        SolverDiagnostics.Log($"[F1][FULL-UB-cut] od {FormatDay(day)}: ub={ub}, bestPref={_bestPrefixLen} → stop");
                        return;
                    }
                }

                var cands = F1_OrderCandidates(day, curAssign, curWork, curMW);
                LogCandidates(day, cands, curAssign, curWork);
                if (cands.Count == 0) return;

                foreach (var doc in cands)
                {
                    byte code = _pref[day, doc];
                    if (!F1_IsFeasible(day, doc, curAssign, curWork, curMW)) continue;

                    // KROK 1: mikrosito d+1 – twarde odcięcie ślepych zaułków
                    if (!KeepsNextFeasible(day, doc, curAssign, curWork, curMW))
                    {
                    if (LOG_EVERY_PRUNE_D1 > 0 ? (++_cntPruneD1 % LOG_EVERY_PRUNE_D1 == 0) : true)
                       SolverDiagnostics.Log($"[F1] Prune d+1: {FormatDay(day)} ← {_docs[doc].Symbol}");
                        continue;
                    }

                    // pick
                    curAssign[day] = doc;
                    curWork[doc]++;
                    if (code == PREF_MW) curMW[doc]++;

                    SolverDiagnostics.Log($"[F1] Pick: {FormatDay(day)} ← {_docs[doc].Symbol} [{PrefToString(code)}]");
                    DFS(day + 1);

                    // backtrack
                    if (code == PREF_MW) curMW[doc]--;
                    curWork[doc]--;
                    curAssign[day] = UNASSIGNED;
                    SolverDiagnostics.Log($"[F1][Backtrack] ← {FormatDay(day)}");
                }
            }

            DFS(0);
        }

        // === KROK 2: Buckety kandydatów per dzień ===
        private void EnsureBucketsBuilt()
        {
            if (_bucketsReady) return;

            int D = _days.Count;
            int P = _docs.Count;

            _buckBC = new List<int>[D];
            _buckCH = new List<int>[D];
            _buckMG = new List<int>[D];
            _buckMW = new List<int>[D];

            for (int d = 0; d < D; d++)
            {
                _buckBC[d] = new List<int>(Math.Max(4, P / 6));
                _buckCH[d] = new List<int>(Math.Max(4, P / 6));
                _buckMG[d] = new List<int>(Math.Max(4, P / 6));
                _buckMW[d] = new List<int>(Math.Max(4, P / 6));

                for (int p = 0; p < P; p++)
                {
                    byte code = _pref[d, p];
                    if (code == PREF_BC) _buckBC[d].Add(p);
                    else if (code == PREF_CH) _buckCH[d].Add(p);
                    else if (code == PREF_MG) _buckMG[d].Add(p);
                    else if (code == PREF_MW) _buckMW[d].Add(p);
                }
            }

            _bucketsReady = true;
        }

        private static int PrefWeight(byte code)
        {
            return code switch
            {
                PREF_BC => 0,
                PREF_CH => 1,
                PREF_MG => 2,
                PREF_MW => 3,
                _ => 4
            };
        }

        private List<int> F1_OrderCandidates(int day, int[] curAssign, int[] curWork, int[] curMW)
        {
            // Rezerwacje – jeżeli istnieją w danych F1 (zwykle nie, wrapper je wyciął)
            var RZ = new List<int>();
            for (int p = 0; p < _docs.Count; p++)
            {
                if (_pref[day, p] == PREF_RZ && F1_IsFeasible(day, p, curAssign, curWork, curMW))
                    RZ.Add(p);
            }
            if (RZ.Count > 0)
            {
                RZ.Sort((a, b) => TieBreakF1(day, a, b, curAssign, curWork));
                SolverDiagnostics.Log($"[F1] RZ wymuszone – kandydaci: {string.Join(", ", RZ.Select(i => _docs[i].Symbol))}");
                return RZ;
            }

            // Buckety BC→CH→MG→MW
            var baseList = new List<int>(
                (_buckBC[day]?.Count ?? 0) +
                (_buckCH[day]?.Count ?? 0) +
                (_buckMG[day]?.Count ?? 0) +
                (_buckMW[day]?.Count ?? 0)
            );
            if (_buckBC[day] != null) baseList.AddRange(_buckBC[day]);
            if (_buckCH[day] != null) baseList.AddRange(_buckCH[day]);
            if (_buckMG[day] != null) baseList.AddRange(_buckMG[day]);
            if (_buckMW[day] != null) baseList.AddRange(_buckMW[day]);

            // Odrzuć tych, którzy dobili do limitu
            for (int i = baseList.Count - 1; i >= 0; i--)
            {
                int p = baseList[i];
                if (curWork[p] >= _limitsByDoc[p]) baseList.RemoveAt(i);
            }

            // Twarda wykonalność
            var cands = new List<int>(baseList.Count);
            foreach (var doc in baseList)
            {
                if (F1_IsFeasible(day, doc, curAssign, curWork, curMW))
                    cands.Add(doc);
            }

            if (cands.Count <= 1) return cands;

            // Cache odległości na ten dzień
            var nearestCache = new Dictionary<int, int>(cands.Count);
            int NearestCached(int doc)
            {
                if (!nearestCache.TryGetValue(doc, out var v))
                {
                    v = NearestAssignedDistance(day, doc, curAssign);
                    nearestCache[doc] = v;
                }
                return v;
            }

            cands.Sort((a, b) =>
            {
                // a) preferencja (BC<CH<MG<MW)
                int wa = PrefWeight(_pref[day, a]);
                int wb = PrefWeight(_pref[day, b]);
                if (wa != wb) return wa - wb;

                // b) mniejszy 'ratio after' bez double: (work+1)/limit
                int la = _limitsByDoc[a];
                int lb = _limitsByDoc[b];
                int wna = curWork[a] + 1;
                int wnb = curWork[b] + 1;
                long left = (long)wna * lb;
                long right = (long)wnb * la;
                if (left != right) return left < right ? -1 : 1;

                // c) większa odległość od najbliższego własnego dyżuru
                int da = NearestCached(a);
                int db = NearestCached(b);
                if (da != db) return db - da;

                // d) stabilizator
                return a - b;
            });

            return cands;
        }

        private int TieBreakF1(int day, int a, int b, int[] curAssign, int[] curWork)
        {
            int la = _limitsByDoc[a];
            int lb = _limitsByDoc[b];
            int wna = curWork[a] + 1;
            int wnb = curWork[b] + 1;
            long left = (long)wna * lb;
            long right = (long)wnb * la;
            int cmp = left.CompareTo(right);
            if (cmp != 0) return cmp;

            int da = NearestAssignedDistance(day, a, curAssign);
            int db = NearestAssignedDistance(day, b, curAssign);
            cmp = db.CompareTo(da);
            if (cmp != 0) return cmp;

            return a.CompareTo(b);
        }

        private bool F1_IsFeasible(int day, int doc, int[] curAssign, int[] curWork, int[] curMW)
        {
            if (curWork[doc] >= _limitsByDoc[doc]) return false;

            byte av = _pref[day, doc];
            if (av == PREF_NONE || av == PREF_OD) return false;

            bool isBC = (av == PREF_BC);

            // sąsiedztwo: BC może łamać
            if (!_bcBreaksAdjacent || !isBC)
            {
                if (day > 0 && curAssign[day - 1] == doc) return false;
                if (day + 1 < _days.Count && _pref[day + 1, doc] == PREF_OD) return false;
            }

            if (av == PREF_MW && (curMW[doc] >= _mwMax)) return false;

            return true;
        }

        private bool KeepsNextFeasible(int day, int doc, int[] curAssign, int[] curWork, int[] curMW)
        {
            int dNext = day + 1;
            if (dNext >= _days.Count) return true;

            byte code = _pref[day, doc];
            curAssign[day] = doc;
            curWork[doc]++;
            if (code == PREF_MW) curMW[doc]++;

            bool ok = false;
            for (int q = 0; q < _docs.Count; q++)
            {
                if (F1_IsFeasible(dNext, q, curAssign, curWork, curMW)) { ok = true; break; }
            }

            if (code == PREF_MW) curMW[doc]--;
            curWork[doc]--;
            curAssign[day] = UNASSIGNED;
            return ok;
        }

        // ===================== F2: Maksymalizacja obsady (greedy) =====================
        private void F2_MaximizeCoverageFrom(int startDay)
        {
            _filled = 0;
            Array.Fill(_workPerDoc, 0);
            Array.Fill(_mwUsed, 0);
            for (int d = 0; d < _days.Count; d++)
            {
                if (_assign[d] >= 0)
                {
                    _workPerDoc[_assign[d]]++;
                    if (_pref[d, _assign[d]] == PREF_MW) _mwUsed[_assign[d]]++;
                    _filled++;
                }
            }

            // 1) Rezerwacje (gdyby jakieś trafiły do F2 – zwykle już są uwzględnione w wrapperze)
            for (int d = startDay; d < _days.Count; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;
                var rz = Enumerable.Range(0, _docs.Count)
                                   .Where(p => _pref[d, p] == PREF_RZ && IsHardFeasibleGlobal(d, p))
                                   .ToList();
                if (rz.Count > 0)
                {
                    int sel = SelectByTieBreakF2(d, rz);
                    PlaceGlobal(d, sel);
                    SolverDiagnostics.Log($"[F2] RZ: {FormatDay(d)} ← {_docs[sel].Symbol}");
                }
            }

            // 2) Heurystyki w pętli
            bool progress;
            do
            {
                progress = TryFillUnique(startDay, PREF_BC);
                progress |= TryFillUnique(startDay, PREF_CH);
                progress |= TryFillAny(startDay, PREF_BC);
                progress |= TryFillAny(startDay, PREF_CH);
                progress |= TryFillAny(startDay, PREF_MG);
                progress |= TryFillAny(startDay, PREF_MW);
            }
            while (progress);

            // 3) Braki → EMPTY
            for (int d = startDay; d < _days.Count; d++)
                if (_assign[d] == UNASSIGNED) _assign[d] = EMPTY;
        }

        private bool TryFillUnique(int startDay, byte prefCode)
        {
            bool any = false;
            for (int d = startDay; d < _days.Count; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;

                _tmp.Clear();
                for (int p = 0; p < _docs.Count; p++)
                    if (_pref[d, p] == prefCode && IsHardFeasibleGlobal(d, p))
                        _tmp.Add(p);

                if (_tmp.Count == 1)
                {
                    int sel = _tmp[0];
                    PlaceGlobal(d, sel);
                    SolverDiagnostics.Log($"[F2] unique-{PrefToString(prefCode)}: {FormatDay(d)} ← {_docs[sel].Symbol}");
                    any = true;
                }
            }
            return any;
        }

        private bool TryFillAny(int startDay, byte prefCode)
        {
            bool any = false;
            for (int d = startDay; d < _days.Count; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;

                _tmp.Clear();
                for (int p = 0; p < _docs.Count; p++)
                    if (_pref[d, p] == prefCode && IsHardFeasibleGlobal(d, p))
                        _tmp.Add(p);

                if (_tmp.Count > 0)
                {
                    int sel = SelectByTieBreakF2(d, _tmp); // sortuje _tmp w miejscu
                    PlaceGlobal(d, sel);
                    SolverDiagnostics.Log($"[F2] {PrefToString(prefCode)}: {FormatDay(d)} ← {_docs[sel].Symbol}");
                    any = true;
                }
            }
            return any;
        }

        private int SelectByTieBreakF2(int day, List<int> candidates)
        {
            candidates.Sort((a, b) =>
            {
                foreach (var pr in _priorities.Skip(1)) // #2, #3, #4
                {
                    int cmp = 0;
                    switch (pr)
                    {
                        case SolverPriority.SprawiedliwoscObciazenia:
                            {
                                double ra = RatioAfter(_workPerDoc[a], _limitsByDoc[a]);
                                double rb = RatioAfter(_workPerDoc[b], _limitsByDoc[b]);
                                cmp = ra.CompareTo(rb);
                                if (cmp != 0) return cmp;
                                break;
                            }
                        case SolverPriority.RownomiernoscRozlozenia:
                            {
                                int da = NearestAssignedDistanceGlobal(day, a);
                                int db = NearestAssignedDistanceGlobal(day, b);
                                cmp = db.CompareTo(da);
                                if (cmp != 0) return cmp;
                                break;
                            }
                        case SolverPriority.CiagloscPoczatkowa:
                            {
                                // F2 nie modyfikuje prefiksu – ignorujemy
                                break;
                            }
                        case SolverPriority.LacznaLiczbaObsadzonychDni:
                            {
                                // to jest cel F2 – i tak maksymalizujemy pokrycie
                                break;
                            }
                    }
                }
                return a.CompareTo(b);
            });
            return candidates[0];
        }

        private bool IsHardFeasibleGlobal(int day, int doc)
        {
            if (_assign[day] != UNASSIGNED) return false;
            if (_workPerDoc[doc] >= _limitsByDoc[doc]) return false;

            byte av = _pref[day, doc];
            if (av == PREF_NONE || av == PREF_OD) return false;

            bool isBC = (av == PREF_BC);
            if (!isBC)
            {
                if (day > 0 && _assign[day - 1] == doc) return false;
                if (day + 1 < _days.Count && _pref[day + 1, doc] == PREF_OD) return false;
            }

            if (av == PREF_MW && (_mwUsed[doc] >= _mwMax)) return false;
            return true;
        }

        private void PlaceGlobal(int day, int doc)
        {
            _assign[day] = doc;
            _workPerDoc[doc]++;
            if (_pref[day, doc] == PREF_MW) _mwUsed[doc]++;
            _filled++;
        }

        private RozwiazanyGrafik BuildSolution()
        {
            var sol = new RozwiazanyGrafik { Przypisania = new Dictionary<DateTime, Lekarz?>() };
            for (int d = 0; d < _days.Count; d++)
                sol.Przypisania[_days[d]] = _assign[d] >= 0 ? _docs[_assign[d]] : null;
            return sol;
        }

        private long[] EvaluateSolution(RozwiazanyGrafik sol)
        {
            long sObs = 0, sCont = 0, sFair = 0, sEven = 0;

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
                lims[i] = _limitsByDoc[i];
                sumLimits += lims[i];
            }
            long sumWork = 0;
            for (int i = 0; i < _docs.Count; i++)
                sumWork += perDoc[i];

            if (sumWork == 0) sFair = 0;
            else
            {
                double acc = 0.0;
                for (int i = 0; i < _docs.Count; i++)
                {
                    double w = perDoc[i] / (double)sumWork;
                    double L = lims[i] / (double)sumLimits;
                    acc += Math.Abs(w - L);
                }
                sFair = (long)Math.Round((1.0 - acc) * 1_000_000);
            }

            int minDist = int.MaxValue;
            for (int d = 0; d < _days.Count; d++)
            {
                if (sol.Przypisania.TryGetValue(_days[d], out var l) && l is not null)
                {
                    var i = _docIdxBySymbol[l.Symbol];
                    int prev = int.MaxValue, next = int.MaxValue;
                    for (int k = d - 1; k >= 0; k--) if (sol.Przypisania[_days[k]]?.Symbol == l.Symbol) { prev = d - k; break; }
                    for (int k = d + 1; k < _days.Count; k++) if (sol.Przypisania[_days[k]]?.Symbol == l.Symbol) { next = k - d; break; }
                    minDist = Math.Min(minDist, Math.Min(prev, next));
                }
            }
            sEven = (minDist == int.MaxValue) ? 0 : minDist;

            long[] vec = new long[4];
            foreach (var pr in _priorities.Select((p, i) => (p, i)))
            {
                vec[pr.i] = pr.p switch
                {
                    SolverPriority.CiagloscPoczatkowa => sCont,
                    SolverPriority.LacznaLiczbaObsadzonychDni => sObs,
                    SolverPriority.SprawiedliwoscObciazenia => sFair,
                    SolverPriority.RownomiernoscRozlozenia => sEven,
                    _ => 0
                };
            }
            return vec;
        }

        private static string FormatScore(long[] v) => $"[{string.Join(", ", v)}]";

        // ===================== Utils =====================
        private double RatioAfter(int work, int limit)
        {
            double lim = Math.Max(1, limit);
            return (work + 1) / lim;
        }

        private int NearestAssignedDistance(int day, int doc, int[] curAssign)
        {
            int best = int.MaxValue;
            for (int d = day - 1; d >= 0; d--)
                if (curAssign[d] == doc) { best = Math.Min(best, day - d); break; }
            for (int d = day + 1; d < _days.Count; d++)
                if (curAssign[d] == doc) { best = Math.Min(best, d - day); break; }
            return best == int.MaxValue ? 9999 : best;
        }

        private int NearestAssignedDistanceGlobal(int day, int doc)
        {
            int best = int.MaxValue;
            for (int d = day - 1; d >= 0; d--)
                if (_assign[d] == doc) { best = Math.Min(best, day - d); break; }
            for (int d = day + 1; d < _days.Count; d++)
                if (_assign[d] == doc) { best = Math.Min(best, d - day); break; }
            return best == int.MaxValue ? 9999 : best;
        }

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

        private void ApplyPrefixSnapshot(int[] snap, int len)
        {
            for (int d = 0; d < _days.Count; d++) _assign[d] = UNASSIGNED;
            Array.Fill(_workPerDoc, 0);
            Array.Fill(_mwUsed, 0);
            _filled = 0;

            for (int d = 0; d < len; d++)
            {
                int doc = snap[d];
                if (doc >= 0)
                {
                    _assign[d] = doc;
                    _workPerDoc[doc]++;
                    if (_pref[d, doc] == PREF_MW) _mwUsed[doc]++;
                    _filled++;
                }
                else if (doc == EMPTY)
                {
                    _assign[d] = EMPTY;
                }
            }
        }

        private string FormatDay(int dayIndex) => $"{_days[dayIndex]:yyyy-MM-dd}";

        private bool Better(long[] cur, long[] best)
        {
            for (int i = 0; i < Math.Min(cur.Length, best.Length); i++)
            {
                if (cur[i] > best[i]) return true;
                if (cur[i] < best[i]) return false;
            }
            return false;
        }

        private void LogCandidates(int day, List<int> cand, int[] curAssign, int[] curWork)
        {
            SolverDiagnostics.Log($"--- [F1] Kandydaci dnia {FormatDay(day)} ---");
            foreach (var p in cand)
                SolverDiagnostics.Log($"  ✓ {_docs[p].Symbol} [{PrefToString(_pref[day, p])}]  (pracuje={curWork[p]}, limit={_limitsByDoc[p]})");
            SolverDiagnostics.Log($"--- /[F1] Kandydaci dnia {FormatDay(day)} ---");
        }
    }
}
