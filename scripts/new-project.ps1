<#
.SYNOPSIS
    Legt ein neues Projekt an: kopiert src/projects/_Vorlage und setzt den Namen ein.

.DESCRIPTION
    Ein Projekt ist ein Ordner unter src/projects/ (Entscheidung 1: der Ordnername IST der
    Projektname). Dieses Skript nimmt die Vorlage, kopiert sie auf den gewuenschten Namen und
    ersetzt darin das Platzhalterwort "MeinProjekt" -- in Dateinamen, Klassennamen, im
    Namensraum und in den Kommentarkoepfen der JSON-Dateien.

    Danach ist NICHTS weiter anzumelden: die ProjectRegistry findet die neue
    IProjectDefinition beim Start per Reflection. Kein Eintrag in Program.cs, keine Liste,
    kein switch (TODO-23).

    Die JSON-Dateien werden bewusst KOPIERT und nicht aus dem Code erzeugt. Ein
    JsonConfigLoader.Save schreibt JSON ohne Kommentare -- und genau die Erklaerungen in den
    Kommentarkoepfen sind das, was einen neuen Nutzer traegt (TODO-27).

.PARAMETER Name
    Der Projektname und damit der Ordnername. Muss ein gueltiger C#-Bezeichner sein, denn er
    wird auch zum Klassennamen (<Name>Project) und zum Namensraum
    (MyPicoGkProject.Projects.<Name>).

.EXAMPLE
    .\scripts\new-project.ps1 Propeller
    Legt src/projects/Propeller/ an und nennt den Startbefehl.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Name
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectsRoot = Join-Path $RepoRoot 'src\projects'
$TemplateDir = Join-Path $ProjectsRoot '_Vorlage'

# Das Wort, das in der Vorlage ueberall steht, wo der Projektname hin muss. Bewusst ein Wort,
# das in keinem deutschen Kommentarsatz vorkommt: "Vorlage" waere in Prosa mitgetroffen worden.
$Placeholder = 'MeinProjekt'

# Dateiendungen, in denen der Platzhalter ersetzt wird. Die STL- oder Binaerdateien eines
# spaeteren Projekts sollen unangetastet bleiben, deshalb eine ausdrueckliche Liste.
$TextExtensions = @('.cs', '.json')

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Write-Info([string]$Text) {
    Write-Host "    $Text"
}

function Write-Warn([string]$Text) {
    Write-Host "    $Text" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Pruefen, bevor irgendetwas angelegt wird
# ---------------------------------------------------------------------------

# Der Name wird zu einem Klassennamen und zu einem Namensraum-Teil. Ein Bindestrich, ein
# Leerzeichen oder eine fuehrende Ziffer wuerden erst beim naechsten Build auffallen -- und
# dann mit einer Fehlermeldung, die nicht von diesem Skript spricht.
if ($Name -notmatch '^[A-Za-z][A-Za-z0-9_]*$') {
    throw "Ungueltiger Projektname '$Name'. Erlaubt sind ein Buchstabe am Anfang, danach Buchstaben, Ziffern und Unterstrich -- der Name wird auch Klassenname (${Name}Project) und Namensraum (MyPicoGkProject.Projects.${Name})."
}

if ($Name -eq $Placeholder) {
    throw "'$Placeholder' ist das Platzhalterwort der Vorlage und als Projektname nicht zu gebrauchen. Nimm den Namen, unter dem du das Projekt wirklich rechnen willst."
}

if (-not (Test-Path $TemplateDir)) {
    throw "Die Vorlage fehlt: $TemplateDir. Ohne sie kann dieses Skript nichts kopieren."
}

$TargetDir = Join-Path $ProjectsRoot $Name

if (Test-Path $TargetDir) {
    throw "Es gibt schon einen Ordner src/projects/$Name. Dieses Skript ueberschreibt nichts -- loesche ihn selbst oder nimm einen anderen Namen."
}

# Gross-/Kleinschreibung ignorieren: Linux unterscheidet sie, Windows nicht. Zwei Ordner
# "Propeller" und "propeller" liefen auf dem Server auseinander, und die ProjectRegistry
# wuerde die beiden Definitionen als Namenskonflikt melden.
$existing = Get-ChildItem -Path $ProjectsRoot -Directory | Where-Object { $_.Name -ieq $Name }
if ($existing) {
    throw "Es gibt schon einen Ordner src/projects/$($existing.Name), der sich nur in der Gross-/Kleinschreibung unterscheidet. Auf Linux waeren das zwei Projekte, auf Windows eines -- nimm einen anderen Namen."
}

# ---------------------------------------------------------------------------
# Kopieren
# ---------------------------------------------------------------------------

Write-Step "Lege src/projects/$Name an (Kopie von _Vorlage)"

Copy-Item -Path $TemplateDir -Destination $TargetDir -Recurse

# Die README der Vorlage erklaert die Vorlage -- im neuen Projekt waere sie irrefuehrend
# (sie sagt unter anderem, dass der Ordner nicht gebaut wird; dieser hier wird es).
$copiedReadme = Join-Path $TargetDir 'README.md'
if (Test-Path $copiedReadme) { Remove-Item $copiedReadme }

# ---------------------------------------------------------------------------
# Umbenennen und ersetzen
# ---------------------------------------------------------------------------

Write-Step "Setze den Namen ein: $Placeholder -> $Name"

# Zuerst die Dateinamen, dann die Inhalte. Reihenfolge ist egal, aber so stimmt die
# Dateiliste in der Ausgabe schon mit den endgueltigen Namen.
foreach ($file in Get-ChildItem -Path $TargetDir -Recurse -File) {
    if ($file.Name -like "*$Placeholder*") {
        $newName = $file.Name.Replace($Placeholder, $Name)
        Rename-Item -LiteralPath $file.FullName -NewName $newName
        Write-Info "$($file.Name)  ->  $newName"
    }
}

$totalReplacements = 0

foreach ($file in Get-ChildItem -Path $TargetDir -Recurse -File) {
    if ($TextExtensions -notcontains $file.Extension.ToLowerInvariant()) { continue }

    # Ueber .NET lesen und schreiben, nicht ueber Get-/Set-Content: die Dateien enthalten
    # Umlaute und muessen als UTF-8 OHNE BOM zurueckgeschrieben werden. Set-Content schreibt
    # unter PowerShell 5.1 je nach Schalter ANSI oder UTF-8 mit BOM -- beides bricht die
    # Umlaute oder stoert den JSON-Parser.
    $text = [System.IO.File]::ReadAllText($file.FullName)

    $count = ([regex]::Matches($text, [regex]::Escape($Placeholder))).Count
    if ($count -eq 0) { continue }

    $text = $text.Replace($Placeholder, $Name)
    [System.IO.File]::WriteAllText($file.FullName, $text, (New-Object System.Text.UTF8Encoding($false)))

    $totalReplacements += $count
    Write-Info "$($file.Name): $count Stelle(n)"
}

# Kontrolle: nach dem Ersetzen darf das Platzhalterwort nirgends mehr stehen. Bliebe eines
# uebrig, hiesse die Klasse anders als die Datei -- und der Build meldet etwas, das nichts
# mit diesem Skript zu tun hat.
$leftovers = Get-ChildItem -Path $TargetDir -Recurse -File |
    Where-Object { $TextExtensions -contains $_.Extension.ToLowerInvariant() } |
    Where-Object { [System.IO.File]::ReadAllText($_.FullName).Contains($Placeholder) }

if ($leftovers) {
    Write-Warn "Es steht noch '$Placeholder' in: $(($leftovers | ForEach-Object { $_.Name }) -join ', ')"
    Write-Warn "Bitte von Hand nachziehen -- das sollte nicht vorkommen."
}

Write-Info "$totalReplacements Vorkommen ersetzt."

# ---------------------------------------------------------------------------
# Wie es weitergeht
# ---------------------------------------------------------------------------

Write-Step "Fertig. src/projects/$Name/"

Write-Host @"
    Naechste Schritte:

    1. Geometrie   src/projects/$Name/${Name}GeometryGenerator.cs
                   Der Quader ist ein Platzhalter -- BuildVoxelModel ersetzen.

    2. Bewertung   src/projects/$Name/${Name}FitnessCalculator.cs
                   Fitness = 1 / Drag. Das Framework MAXIMIERT die Fitness.

    3. Zahlen      src/projects/$Name/project.json
                   Parameter, Grenzen, Ziele. Dieselben Werte zusaetzlich in
                   CreateDefaults() in ${Name}Project.cs -- das ist die Code-Schicht.

    4. Aufraeumen  simulation.json und solvers/*.json nennen JEDEN Schluessel mit dem
                   Code-Standard. Das ist als Nachschlagewerk gedacht: loesche alles,
                   was du nicht aenderst. Nur MaxIterations=1 und
                   VariantsPerIteration=2 sind bewusst klein -- der erste Lauf soll ein
                   Rauchtest sein und keine Nachtschicht.

    Ohne Server pruefen, ob die Konfigurationskette steht:

        dotnet build Automatisierung.sln
        dotnet run --project src\Automatisierung_v2 --no-build -- $Name --print-config

    Und dann rechnen:

        .\scripts\sim.ps1 run -Project $Name

    src/projects/_Vorlage bleibt unberuehrt liegen, MantaAuv ebenso -- die Projekte
    kommen sich nicht in die Quere, auch die Ergebnisse nicht (Ergebnisse/$Name/).
"@
