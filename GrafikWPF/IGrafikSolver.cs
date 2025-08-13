namespace GrafikWPF
{
    /// <summary>
    /// Definiuje wspólny kontrakt dla wszystkich silników obliczeniowych generujących grafik.
    /// </summary>
    public interface IGrafikSolver
    {
        /// <summary>
        /// Uruchamia proces obliczeniowy w celu znalezienia najlepszego możliwego grafiku.
        /// </summary>
        /// <returns>Obiekt RozwiazanyGrafik zawierający najlepsze znalezione rozwiązanie.</returns>
        RozwiazanyGrafik ZnajdzOptymalneRozwiazanie();
    }
}