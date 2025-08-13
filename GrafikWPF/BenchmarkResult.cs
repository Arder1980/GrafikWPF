using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GrafikWPF
{
    public class BenchmarkResult : INotifyPropertyChanged
    {
        public string EngineName { get; set; } = "";
        public string TestCaseName { get; set; } = "";

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        private string _executionTime = "-";
        public string ExecutionTime
        {
            get => _executionTime;
            set { _executionTime = value; OnPropertyChanged(); }
        }

        private string _status = "Oczekuje";
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        private string _timeRemaining = "";
        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }

        private string _qualityScore = "-";
        public string QualityScore
        {
            get => _qualityScore;
            set { _qualityScore = value; OnPropertyChanged(); }
        }

        // NOWA WŁAŚCIWOŚĆ
        private string _qualityScoreDetails = "";
        public string QualityScoreDetails
        {
            get => _qualityScoreDetails;
            set { _qualityScoreDetails = value; OnPropertyChanged(); }
        }

        public SolverType SolverType { get; set; }
        public int DoctorCount { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
