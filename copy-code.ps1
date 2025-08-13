# Definicja plików do uwzględnienia i katalogów do wykluczenia
$fileTypes = @("*.cs", "*.xaml")
$excludeDirs = @("bin", "obj")

# Pobranie pełnej ścieżki do katalogu projektu
$projectRoot = $PWD.Path

# Przygotowanie bufora na połączony tekst
$stringBuilder = New-Object System.Text.StringBuilder

# Pobranie wszystkich pasujących plików z wykluczeniem niepotrzebnych katalogów
Get-ChildItem -Path $projectRoot -Include $fileTypes -Recurse | Where-Object {
    $_.DirectoryName -notlike "*\obj\*" -and $_.DirectoryName -notlike "*\bin\*"
} | ForEach-Object {
    # Dodanie nagłówka z nazwą pliku
    $relativePath = $_.FullName.Replace($projectRoot + "\", "")
    $header = "`n" + ("=" * 80) + "`n# FILE: " + $relativePath + "`n" + ("=" * 80) + "`n`n"
    $stringBuilder.Append($header) | Out-Null
    
    # Dodanie zawartości pliku
    $content = Get-Content -Path $_.FullName -Raw
    $stringBuilder.Append($content) | Out-Null
    $stringBuilder.Append("`n") | Out-Null
}

# Skopiowanie całości do schowka
$stringBuilder.ToString() | Set-Clipboard

Write-Host "Gotowe! Cały kod projektu został skopiowany do schowka."