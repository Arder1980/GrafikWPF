using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GrafikWPF.UI
{
    /// <summary>
    /// Prosty, bezpieczny dla wątków „menedżer postępu” dla WPF.
    /// - Tryb nieokreślony (IsIndeterminate = true) – gdy nie umiemy policzyć procentów.
    /// - Tryb określony (0–100%) – gdy mamy licznik lub szacunkowy limit.
    /// - Throttling: maks. ~10 aktualizacji na sekundę, aby pasek nie „tańczył”.
    /// Użycie:
    ///   var pr = ProgressReporter.For(progressBar);
    ///   pr.StartIndeterminate(); // albo: pr.ReportCount(done, total);
    ///   ...
    ///   pr.StopIndeterminate();
    /// </summary>
    public sealed class ProgressReporter
    {
        private readonly Dispatcher _dispatcher;
        private readonly Action<double> _setValue;
        private readonly Action<bool> _setIndeterminate;

        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _lastTicks;
        private double _lastShownValue;

        /// <summary>Minimalny odstęp czasu między kolejnymi aktualizacjami UI (sekundy). 0.1 = 10 Hz.</summary>
        public double MinUpdateIntervalSeconds { get; set; } = 0.1;

        /// <summary>Minimalna różnica (w punktach procentowych 0–100), by wymusić aktualizację mimo throttlingu.</summary>
        public double MinDeltaToForceUpdate { get; set; } = 0.5;

        private ProgressReporter(Dispatcher dispatcher, Action<double> setValue, Action<bool> setIndeterminate)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _setValue = setValue ?? throw new ArgumentNullException(nameof(setValue));
            _setIndeterminate = setIndeterminate ?? throw new ArgumentNullException(nameof(setIndeterminate));
        }

        /// <summary>
        /// Fabryka dla paska WPF.
        /// </summary>
        public static ProgressReporter For(ProgressBar progressBar)
        {
            if (progressBar == null) throw new ArgumentNullException(nameof(progressBar));
            var dispatcher = progressBar.Dispatcher ?? Application.Current?.Dispatcher
                             ?? throw new InvalidOperationException("Brak Dispatcher dla UI.");

            return new ProgressReporter(
                dispatcher,
                setValue: v => progressBar.Value = Math.Max(progressBar.Minimum, Math.Min(progressBar.Maximum, v)),
                setIndeterminate: on =>
                {
                    progressBar.IsIndeterminate = on;
                    if (on)
                    {
                        // W trybie nieokreślonym nie pokazujemy wartości.
                        // Dla porządku ustaw 0 – pasek i tak animuje się „sam”.
                        progressBar.Value = progressBar.Minimum;
                    }
                });
        }

        /// <summary>Włącz tryb „nie wiem ile – liczę” (animacja płynącej belki).</summary>
        public void StartIndeterminate() => InvokeOnUi(() => _setIndeterminate(true));

        /// <summary>Wyłącz tryb nieokreślony.</summary>
        public void StopIndeterminate() => InvokeOnUi(() => _setIndeterminate(false));

        /// <summary>
        /// Zgłoś postęp jako ułamek 0..1. Wewnętrznie przeliczane na % (0..100).
        /// </summary>
        public void ReportRatio(double ratio01)
        {
            ratio01 = double.IsNaN(ratio01) || double.IsInfinity(ratio01) ? 0.0 : ratio01;
            var clamped = Math.Max(0.0, Math.Min(1.0, ratio01));
            ReportPercent(clamped * 100.0);
        }

        /// <summary>
        /// Zgłoś postęp jako „zrobione / razem”.
        /// </summary>
        public void ReportCount(long done, long total)
        {
            if (total <= 0) { ReportPercent(0); return; }
            var ratio = Math.Max(0.0, Math.Min(1.0, done / (double)total));
            ReportPercent(ratio * 100.0);
        }

        /// <summary>
        /// Zgłoś bezpośrednio procent 0..100. Gwarancja monotoniczności:
        /// wartość nie cofnie się poniżej ostatnio pokazanej.
        /// </summary>
        public void ReportPercent(double percent)
        {
            if (double.IsNaN(percent) || double.IsInfinity(percent)) return;

            // Monotoniczność: nie cofamy się.
            var v = Math.Max(_lastShownValue, Math.Max(0.0, Math.Min(100.0, percent)));

            // Throttling – nie częściej niż 1/MinUpdateIntervalSeconds.
            var nowTicks = _sw.ElapsedTicks;
            var ticksPerUpdate = (long)(Stopwatch.Frequency * MinUpdateIntervalSeconds);
            var enoughTime = nowTicks - _lastTicks >= ticksPerUpdate;
            var bigJump = Math.Abs(v - _lastShownValue) >= MinDeltaToForceUpdate;

            if (!enoughTime && !bigJump) return;

            _lastTicks = nowTicks;
            _lastShownValue = v;

            InvokeOnUi(() =>
            {
                _setIndeterminate(false);
                _setValue(v);
            });
        }

        private void InvokeOnUi(Action action)
        {
            if (action == null) return;
            var d = _dispatcher;
            if (d == null || d.CheckAccess()) { action(); return; }
            d.Invoke(action, DispatcherPriority.Background);
        }
    }
}
