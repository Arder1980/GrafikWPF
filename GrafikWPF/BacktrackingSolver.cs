using System.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using GrafikWPF.Algorithms; // +++ NEW: FlowUB (max-flow upper bound)


namespace GrafikWPF
{
    /// <summary>
    /// "BTSolver Adam" – dwuetapowy backtracking
    /// Etap F1: maksymalizacja ciągłości od początku miesiąca (jeśli taki priorytet jest #1).
    ///          Przeszukiwanie gałęziami dzień po dniu: BC > CH > MG > MW, twarde zasady (RZ, limity).
    ///          Gdy brak obsady danego dnia – backtrack. Najlepszy znaleziony prefiks zapamiętywany.
    /// Etap F2: maksymalizacja łącznej obsady pozostałych dni (tie-break wg pozostałych priorytetów).
    ///          Preferencje: BC > CH > MG > MW. MW ≤ 1/osoba.
    /// Świętości: RZ zawsze przydzielane; limitów nie przekraczamy.
    /// Zasady sąsiedztwa: BC może łamać „dzień po dniu” i „Inny dyżur ±1”. MG/CH/MW – nie.
    /// Logowanie zgodne z SolverDiagnostics.
    /// </summary>
    public sealed class BacktrackingSolver : IGrafikSolver
    {
        // Preferencje / kody
        private const byte PREF_NONE = 0; // brak / niedostępny
        private const byte PREF_MW = 1; // Mogę warunkowo (max 1 w miesiącu)
        private const byte PREF_MG = 2; // Mogę
        private const byte PREF_CH = 3; // Chcę
        private const byte PREF_BC = 4; // Bardzo chcę
        private const byte PREF_RZ = 5; // Rezerwacja (musi być)
        private const byte PREF_OD = 6; // Dyżur (inny) – dzień +/-1 (blok sąsiedztwa dla MG/CH/MW)

        // Stany przypisań
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

        // Bieżący stan
        private readonly int[] _assign; // day -> doc idx; EMPTY/UNASSIGNED
        private readonly int[] _workPerDoc; // przydzielone
        private readonly int[] _mwUsed; // wykorzystane MW
        private int _filled;

        // Najlepszy wynik / scoring
        private RozwiazanyGrafik? _best;
        private long[]? _bestScore;

        // F1 – prefiks
        private int _bestPrefixLen;
        private int[]? _bestPrefixAssign; // snapshot assign dla najlepszego prefiksu

        // +++ NEW (Krok 1): lekkie, bezpieczne przyspieszacze F1
        private const bool F1_USE_FLOW_UB = true;   // włącz/wyłącz pruning na flow upper bound
        private const int F1_FLOW_EVERY = 8;      // co ile poziomów F1 liczyć UB

        // Polityki (na teraz: CHProtect OFF, BC może łamać sąsiedztwo, MW<=1)
        private readonly bool _chProtectEnabled = false;
        private readonly bool _bcBreaksAdjacent = true;
        private readonly int _mwMax = 1;

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

        // ===================== API =====================
        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            if (!SolverDiagnostics.IsActive)
            {
                SolverDiagnostics.Enabled = true;
                SolverDiagnostics.Start();
            }
                // Nagłówek diagnostyki / polityk
                try
                {
                    var pri = _priorities ?? DataManager.AppData.KolejnoscPriorytetowSolvera;
                    SolverPolicyStatus.LogStartupHeader(
                        solverName: "Backtracking",
                        priorities: pri,
                        chProtectEnabled: _chProtectEnabled,
                        bcBreaksAdjacent: _bcBreaksAdjacent,
                        mwMax: _mwMax,
                        rhK: (2, 2),     // Rolling Horizon: min=2, max=2 (w tym solverze RH nie używamy)
                        lrBack: (0, 0),  // LocalRepair back: wyłączone
                        lrFwd: (0, 0)    // LocalRepair fwd: wyłączone
                    );
                }
                catch (Exception ex)
                {
                    SolverDiagnostics.Log("[Policy] Header logging failed: " + ex.Message);
                }

                SolverDiagnostics.Log("=== Start BacktrackingSolver (Adam) ===");
                SolverDiagnostics.Log($"Dni: {_days.Count}, lekarze: {_docs.Count}");
                SolverDiagnostics.Log($"Priorytety: {string.Join(", ", _priorities)}");
                LogLimits();
                LogLegendAndAvailability();

                // Faza 1: Maksymalizacja prefiksu (jeśli priorytet #1 to Ciągłość; w przeciwnym razie – pusta F1)
                bool continuityFirst = _priorities.Count > 0 && _priorities[0] == SolverPriority.CiagloscPoczatkowa;
                if (continuityFirst)
                {
                    SolverDiagnostics.Log("[F1] Start – maksymalizacja ciągłości od początku");
                    F1_MaximizePrefix();
                    SolverDiagnostics.Log($"[F1] Stop – najlepszy prefiks = {_bestPrefixLen}");
                }
                else
                {
                    SolverDiagnostics.Log("[F1] Pominięto – priorytet #1 ≠ Ciągłość");
                    _bestPrefixLen = 0;
                }

                // Zastosuj najlepszy prefiks do stanu głównego
                if (_bestPrefixAssign != null && _bestPrefixLen > 0)
                {
                    ApplyPrefixSnapshot(_bestPrefixAssign, _bestPrefixLen);
                }

                // Faza 2: Maksymalizacja obsady reszty
                SolverDiagnostics.Log("[F2] Start – maksymalizacja łącznej obsady pozostałych dni");
                F2_MaximizeCoverageFrom(_bestPrefixLen);
                SolverDiagnostics.Log("[F2] Stop");

                // Finalizacja: scoring po zadanej kolejności priorytetów
                var sol = SnapshotToSolution();
                var score = EvaluateSolution(sol);
                _best = sol;
                _bestScore = score;
                SolverDiagnostics.Log($"[BEST] vec={FormatScore(score)}");

                return _best;
                SolverDiagnostics.Stop();
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
        private void F1_MaximizePrefix()
        {
            var curAssign = new int[_days.Count];
            Array.Fill(curAssign, UNASSIGNED);
            var curWork = new int[_docs.Count];
            var curMW = new int[_docs.Count];
            int prefixLen = 0;

            void DFS(int day)
            {
                _token.ThrowIfCancellationRequested();
                _progress?.Report(day / (double)_days.Count);

                // Aktualizacja najlepszego prefiksu
                if (day > _bestPrefixLen)
                {
                    _bestPrefixLen = day;
                    _bestPrefixAssign = (int[])curAssign.Clone();
                    SolverDiagnostics.Log($"[F1] Nowy najlepszy prefiks: {_bestPrefixLen} ({FormatDay(_bestPrefixLen - 1)})");
                }
                if (day >= _days.Count) return;

                // +++ NEW (Krok 1): pruning na optimistycznym UB (co F1_FLOW_EVERY poziomów)
                if (F1_USE_FLOW_UB && (day % F1_FLOW_EVERY == 0))
                {
                    int ub = F1_SuffixUpperBound(day, curWork);
                    int potential = day + ub;
                    if (potential <= _bestPrefixLen)
                    {
                        SolverDiagnostics.Log($"[F1][UB-cut] {FormatDay(Math.Min(day, _days.Count - 1))}: UB={ub}, potential={potential} ≤ bestPref={_bestPrefixLen} → stop");
                        return;
                    }
                }

                // Kandydaci na ten dzień
                var cands = F1_OrderCandidates(day, curAssign, curWork, curMW);
                LogCandidates(day, cands, curAssign, curWork);

                if (cands.Count == 0)
                {
                    // brak obsady => zatrzymujemy się (koniec gałęzi)
                    return;
                }

                foreach (var doc in cands)
                {
                    byte code = _pref[day, doc];
                    // twarde zasady
                    if (!F1_IsFeasible(day, doc, curAssign, curWork, curMW)) continue;

                    // +++ NEW (Krok 1): mikrosito d+1 – nie wchodzimy w wybór, jeśli zabija obsadę jutra
                    if (!KeepsNextFeasible(day, doc, curAssign, curWork, curMW))
                    {
                        SolverDiagnostics.Log($"[F1] Prune d+1: {FormatDay(day)} ← {_docs[doc].Symbol} zablokuje {FormatDay(Math.Min(day + 1, _days.Count - 1))}");
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

        private List<int> F1_OrderCandidates(int day, int[] curAssign, int[] curWork, int[] curMW)
        {
            // Jeśli są RZ – musimy wybrać jednego z nich
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

            var list = new List<int>(_docs.Count);
            for (int p = 0; p < _docs.Count; p++)
                if (_pref[day, p] != PREF_NONE && _pref[day, p] != PREF_OD && F1_IsFeasible(day, p, curAssign, curWork, curMW))
                    list.Add(p);

            // BC > CH > MG > MW, a przy remisie: preferuj takiego, który nie zabije d+1 (krótki lookahead)
            list.Sort((a, b) =>
            {
                int pa = PrefRank(_pref[day, a]);
                int pb = PrefRank(_pref[day, b]);
                if (pa != pb) return pb.CompareTo(pa);

                bool ka = KeepsNextFeasible(day, a, curAssign, curWork, curMW);
                bool kb = KeepsNextFeasible(day, b, curAssign, curWork, curMW);
                int cmp = kb.CompareTo(ka);
                if (cmp != 0) return cmp;

                return TieBreakF1(day, a, b, curAssign, curWork);
            });

            return list;
        }

        private int TieBreakF1(int day, int a, int b, int[] curAssign, int[] curWork)
        {
            // mniejszy wskaźnik obciążenia (proporcja do limitu)
            double ra = RatioAfter(curWork[a], _limitsByDoc[a]);
            double rb = RatioAfter(curWork[b], _limitsByDoc[b]);
            int cmp = ra.CompareTo(rb);
            if (cmp != 0) return cmp;

            // większy dystans od istniejących dyżurów (równomierność)
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

            // dzień-po-dniu: BC może łamać
            if (!_bcBreaksAdjacent || !isBC)
            {
                if (day > 0 && curAssign[day - 1] == doc) return false;
                if (day + 1 < _days.Count && curAssign[day + 1] == doc) return false;
            }

            // inny dyżur ±1: BC może łamać
            if (!_bcBreaksAdjacent || !isBC)
            {
                if (day > 0 && _pref[day - 1, doc] == PREF_OD) return false;
                if (day + 1 < _days.Count && _pref[day + 1, doc] == PREF_OD) return false;
            }

            if (av == PREF_MW && (curMW[doc] >= _mwMax)) return false;

            return true;
        }

        private bool KeepsNextFeasible(int day, int doc, int[] curAssign, int[] curWork, int[] curMW)
        {
            int dNext = day + 1;
            if (dNext >= _days.Count) return true;

            // Testuj czy istnieje jakikolwiek kandydat na d+1 po hipotetycznym wyborze (day, doc)
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
        
        // +++ NEW: optymistyczny górny limit pokrycia ogona (dzień..koniec) z lim. lekarzy
        private int F1_SuffixUpperBound(int startDay, int[] curWork)
        {
            // Upper bound ignoruje reguły sąsiedztwa i MW — to celowe, ma przeszacowywać.
            return FlowUB.UBCount(
                days: _days.Count,
                docs: _docs.Count,
                avMask: (d, p) =>
                {
                    if (d < startDay) return AvMask.None;
                    var code = _pref[d, p];
                    // OD i brak dostępności nie wchodzą do matching'u
                    return (code == PREF_NONE || code == PREF_OD) ? AvMask.None : AvMask.Any;
                },
                remCapPerDoc: p => Math.Max(0, _limitsByDoc[p] - curWork[p]),
                dayAllowed: d => d >= startDay
            );
        }

        // ===================== F2: Maksymalizacja obsady =====================
        private void F2_MaximizeCoverageFrom(int startDay)
        {
            // Zainicjalizuj stan (na podstawie _assign – po F1 może zawierać przydziały 0..start-1)
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
                else if (_assign[d] == EMPTY)
                {
                    // nic
                }
            }

            // 1) Wymuś rezerwacje w całym końcu (świętość)
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

            // 2) Pętle heurystyczne: najpierw dni z unikalnym CH/BC, potem ogólnie CH/BC, potem MG, potem MW
            //    – każdą kategorię przechodzimy kilkoma przebiegami, aż brak postępu.
            bool progress;
            do
            {
                progress = false;

                // Unikalny CH/BC
                progress |= AssignUniqueCHBC(startDay);

                // Pozostałe CH/BC
                progress |= AssignAnyCHBC(startDay);

                // MG
                progress |= AssignByPref(startDay, PREF_MG);

                // MW (pilnując MW<=1)
                progress |= AssignByPref(startDay, PREF_MW);
            }
            while (progress);

            // Nie wymuszamy pełnej kompletności: to backtracking F1 był najważniejszy
        }

        private bool AssignUniqueCHBC(int startDay)
        {
            bool any = false;
            for (int d = startDay; d < _days.Count; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;
                var chbc = Enumerable.Range(0, _docs.Count)
                                     .Where(p => (_pref[d, p] == PREF_CH || _pref[d, p] == PREF_BC) && IsHardFeasibleGlobal(d, p))
                                     .ToList();
                if (chbc.Count == 1)
                {
                    int sel = chbc[0];
                    PlaceGlobal(d, sel);
                    SolverDiagnostics.Log($"[F2] Unique CH/BC: {FormatDay(d)} ← {_docs[sel].Symbol} [{PrefToString(_pref[d, sel])}]");
                    any = true;
                }
            }
            return any;
        }

        private bool AssignAnyCHBC(int startDay)
        {
            bool any = false;
            for (int d = startDay; d < _days.Count; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;
                var chbc = Enumerable.Range(0, _docs.Count)
                                     .Where(p => (_pref[d, p] == PREF_CH || _pref[d, p] == PREF_BC) && IsHardFeasibleGlobal(d, p))
                                     .ToList();
                if (chbc.Count > 0)
                {
                    int sel = SelectByTieBreakF2(d, chbc);
                    PlaceGlobal(d, sel);
                    SolverDiagnostics.Log($"[F2] CH/BC: {FormatDay(d)} ← {_docs[sel].Symbol} [{PrefToString(_pref[d, sel])}]");
                    any = true;
                }
            }
            return any;
        }

        private bool AssignByPref(int startDay, byte prefCode)
        {
            bool any = false;
            for (int d = startDay; d < _days.Count; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;
                var list = Enumerable.Range(0, _docs.Count)
                                     .Where(p => _pref[d, p] == prefCode && IsHardFeasibleGlobal(d, p))
                                     .ToList();
                if (list.Count > 0)
                {
                    int sel = SelectByTieBreakF2(d, list);
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
                // Priorytety użytkownika jako sekwencja tie-breakerów (poza tym, że cel główny F2 to maks. obsada)
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
                            // W F2 nie rozbijamy już prefiksu – ten priorytet ma mniejsze znaczenie; pomijamy.
                            break;
                        case SolverPriority.LacznaLiczbaObsadzonychDni:
                            // To już jest cel F2, nie stanowi tie-breakera.
                            break;
                    }
                }

                // Ostateczny tie-break: mniej przydzielony względem limitu, potem indeks
                int wcmp = _workPerDoc[a].CompareTo(_workPerDoc[b]);
                if (wcmp != 0) return wcmp;
                int remA = _limitsByDoc[a] - _workPerDoc[a];
                int remB = _limitsByDoc[b] - _workPerDoc[b];
                int rc = remB.CompareTo(remA);
                if (rc != 0) return rc;
                return a.CompareTo(b);
            });
            return candidates[0];
        }

        private bool IsHardFeasibleGlobal(int day, int doc)
        {
            if (_workPerDoc[doc] >= _limitsByDoc[doc]) return false;

            byte av = _pref[day, doc];
            if (av == PREF_NONE || av == PREF_OD) return false;

            bool isBC = (av == PREF_BC);

            // dzień-po-dniu: BC może łamać
            if (!_bcBreaksAdjacent || !isBC)
            {
                if (day > 0 && _assign[day - 1] == doc) return false;
                if (day + 1 < _days.Count && _assign[day + 1] == doc) return false;
            }

            // inny dyżur ±1: BC może łamać
            if (!_bcBreaksAdjacent || !isBC)
            {
                if (day > 0 && _pref[day - 1, doc] == PREF_OD) return false;
                if (day + 1 < _days.Count && _pref[day + 1, doc] == PREF_OD) return false;
            }

            if (av == PREF_MW && (_mwUsed[doc] >= _mwMax)) return false;

            return true;
        }

        private void PlaceGlobal(int day, int doc)
        {
            byte code = _pref[day, doc];
            _assign[day] = doc;
            _workPerDoc[doc]++;
            if (code == PREF_MW) _mwUsed[doc]++;
            _filled++;
        }

        // ===================== Scoring / wynik =====================
        private RozwiazanyGrafik SnapshotToSolution()
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
                long L = Math.Max(0, _limitsByDoc[i]);
                lims[i] = L;
                sumLimits += L;
            }
            if (sObs > 0 && sumLimits > 0)
            {
                double sumAbs = 0.0;
                for (int i = 0; i < _docs.Count; i++)
                {
                    double expected = sObs * (lims[i] / (double)sumLimits);
                    sumAbs += Math.Abs(perDoc[i] - expected);
                }
                sFair = -(long)Math.Round(sumAbs * 1000.0);
            }

            // prosty karny miernik równomierności (sąsiedztwo tego samego lekarza)
            int penalty = 0;
            int lastDoc = -1;
            for (int d = 0; d < _days.Count; d++)
            {
                int cur = -1;
                if (sol.Przypisania.TryGetValue(_days[d], out var l) && l is not null &&
                    _docIdxBySymbol.TryGetValue(l.Symbol, out var idx)) cur = idx;
                if (cur >= 0 && lastDoc == cur) penalty++;
                if (cur >= 0) lastDoc = cur;
                else lastDoc = -1;
            }
            sEven = -penalty;

            var map = new Dictionary<SolverPriority, long>
            {
                { SolverPriority.LacznaLiczbaObsadzonychDni, sObs },
                { SolverPriority.CiagloscPoczatkowa, sCont },
                { SolverPriority.SprawiedliwoscObciazenia, sFair },
                { SolverPriority.RownomiernoscRozlozenia, sEven }
            };
            var vec = new long[_priorities.Count];
            for (int i = 0; i < _priorities.Count; i++)
            {
                var pr = _priorities[i];
                vec[i] = map.TryGetValue(pr, out var v) ? v : 0;
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

        // ===================== Logging helpers =====================
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

        private void LogCandidates(int day, List<int> cand, int[] curAssign, int[] curWork)
        {
            var prefix = day; // bo w F1 układamy 0..day-1
            SolverDiagnostics.Log($"--- [F1] Kandydaci dnia {FormatDay(day)} ---");
            SolverDiagnostics.Log($"Dzień {FormatDay(day)} (prefiks={prefix}) – kandydaci:");
            foreach (var p in cand)
                SolverDiagnostics.Log($"  ✓ {_docs[p].Symbol} [{PrefToString(_pref[day, p])}]  (pracuje={curWork[p]}, limit={_limitsByDoc[p]})");
            SolverDiagnostics.Log($"--- /[F1] Kandydaci dnia {FormatDay(day)} ---");
        }
    }
}