# ExportProject.ps1
# Tworzy jeden TXT ze zrzutem calego kodu (pliki tekstowe) z projektu/rozwiazania.
# Usuwa wczesniejsze logi zgodne ze wzorcem i tworzy nowy.
# Nazwa: <NazwaProjektu>_YYYYMMDD_HHMM.txt

[CmdletBinding()]
param(
    [string[]]$IncludeExtensions = @(
        '.sln','.cs','.xaml','.csproj','.props','.targets','.resx',
        '.config','.json','.xml','.yaml','.yml','.md','.txt',
        '.ps1','.bat','.cmd','.tt','.sql',
        '.cpp','.h','.hpp','.c','.cc',
        '.fs','.fsproj','.vb','.vbproj',
        '.razor','.cshtml'
    ),
    [string[]]$ExcludeDirs = @(
        '.git','.vs','bin','obj','packages','node_modules',
        'dist','out','Debug','Release','.idea','.vscode','.cache'
    )
)

Set-Location -LiteralPath $PSScriptRoot
$root = (Get-Item -LiteralPath $PSScriptRoot).FullName
if (-not $root.EndsWith('\')) { $root += '\' }

# Nazwa projektu wg .sln (pierwszy alfabetycznie) lub nazwa folderu
$sln = Get-ChildItem -LiteralPath $root -Filter *.sln | Sort-Object Name | Select-Object -First 1
$projectName = if ($sln) { [IO.Path]::GetFileNameWithoutExtension($sln.Name) } else { Split-Path -Leaf $root.TrimEnd('\') }

# Czas w formacie YYYYMMDD_HHMM (wymaganie: ..._data_hhmm.txt)
$timestamp = Get-Date -Format "yyyyMMdd_HHmm"
$logName   = "{0}_{1}.txt" -f $projectName, $timestamp
$logPath   = Join-Path -Path $root -ChildPath $logName

# Usun poprzednie logi tego projektu o nazwie <Projekt>_YYYYMMDD_HHMM.txt
Get-ChildItem -LiteralPath $root -Filter "$projectName*.txt" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "^$([regex]::Escape($projectName))_\d{8}_\d{4}\.txt$" } |
    Remove-Item -Force -ErrorAction SilentlyContinue

# Zbuduj wzorzec katalogow do pominiecia
$excludePattern = ($ExcludeDirs | ForEach-Object { [regex]::Escape($_) }) -join '|'

# Zbierz pliki do zrzutu
$files = Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $IncludeExtensions -contains $_.Extension.ToLower() -and
        ($_.FullName -notmatch "(\\|/)(?:$excludePattern)(\\|/)")
    } | Sort-Object FullName

# Piszemy jako UTF-8 (bez BOM) poprzez StreamWriter
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$sw = New-Object System.IO.StreamWriter($logPath, $false, $utf8NoBom)

try {
    $sw.WriteLine("=== PROJECT: {0}" -f $projectName)
    $sw.WriteLine("=== DATE:    {0}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz"))
    $sw.WriteLine("=== ROOT:    {0}" -f $root.TrimEnd('\'))
    $sw.WriteLine("=== FILES:   {0}" -f $files.Count)
    $sw.WriteLine()

    foreach ($f in $files) {
        $rel = $f.FullName.Substring($root.Length)
        $sw.WriteLine("----- BEGIN FILE: {0} -----" -f $rel)
        try {
            $content = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction Stop
            $sw.WriteLine($content)
        }
        catch {
            $sw.WriteLine("[WARNING] Could not read file: {0}" -f $_.Exception.Message)
        }
        $sw.WriteLine("----- END FILE: {0} -----" -f $rel)
        $sw.WriteLine()
    }
}
finally {
    $sw.Close()
}

Write-Host ("Saved file: {0}" -f $logName)
