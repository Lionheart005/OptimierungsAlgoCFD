<#
.SYNOPSIS
    Steuert den Optimierungslauf auf dem Linux-Rechner der Hochschule von Windows aus.

.DESCRIPTION
    Windows-Seite der Fernsteuerung. Packt den Quellcode, schiebt ihn per SSH auf den
    Server und ruft dort scripts/sim-runner.sh auf, das die eigentliche Linux-Arbeit
    macht (tmux, xvfb-run, Pfade, Start/Stop).

    Uebertragen wird per tar + scp, nicht per git: so landen auch nicht-committete
    Aenderungen auf dem Server und es muss kein GitHub-Token auf dem Hochschulrechner
    liegen. rsync gibt es unter Windows nicht.

    Laeuft beim Deploy bereits eine Simulation und hat sich der Code geaendert, wird
    sie gestoppt und mit der neuen Version neu gestartet ("-NoRestart" verhindert das).
    Ist der Code unveraendert, bleibt ein laufender Lauf unangetastet.

.PARAMETER Command
    deploy   Code hochladen und bauen (startet bei Bedarf neu, siehe oben)
    run      deploy + starten  -- der Alltagsbefehl
    status   Laeuft was? Wie lange? Letzte Logzeilen
    log      Logdatei live mitlesen (Strg+C beendet nur das Mitlesen, nicht den Lauf)
    stop     Lauf, tmux-Sitzung und haengende Kindprozesse beenden
    fetch    Ergebnisse vom Server nach Windows holen
    doctor   Prueft auf dem Server Werkzeuge und Pfade, ohne etwas zu starten
    build    Nur bauen, nicht starten

.EXAMPLE
    .\scripts\sim.ps1 doctor
    Vor dem ersten Lauf: prueft SU2, mpirun, gmsh, dotnet, tmux und libpicogk.so.

.EXAMPLE
    .\scripts\sim.ps1 run
    Code hochladen, bauen, in tmux starten. Danach kann das Fenster zu.

.EXAMPLE
    .\scripts\sim.ps1 log
    Live zusehen. Strg+C beendet nur die Anzeige.

.EXAMPLE
    .\scripts\sim.ps1 fetch -All
    Holt den kompletten Ergebnisse-Ordner (inkl. vtu-Dateien) nach .\Serverergebnisse\
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('deploy', 'run', 'status', 'log', 'stop', 'fetch', 'doctor', 'build')]
    [string]$Command = 'status',

    # Name aus ~/.ssh/config. BIC_12 = 192.168.122.121
    [string]$Server = 'BIC_12',

    # Zielverzeichnis auf dem Server, relativ zum Home-Verzeichnis.
    # Bewusst NICHT das alte Automatisierung_v2: das bleibt als main-Referenz liegen.
    [string]$RemoteDir = 'Documents/AutomatisierungCleanVersion',

    [string]$Project = 'MantaAuv',

    # Laufende Simulation trotz geaendertem Code weiterlaufen lassen
    [switch]$NoRestart,

    # Hochladen erzwingen, auch wenn sich nichts geaendert hat
    [switch]$Force,

    # fetch: kompletten Ergebnisse-Ordner statt nur CSV und Log
    [switch]$All,

    # log/status: wie viele Zeilen
    [int]$Lines = 40,

    # log: nur einmal ausgeben statt mitlaufen
    [switch]$NoFollow,

    # fetch: Zielordner unter Windows
    [string]$Out = 'Serverergebnisse'
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$RunnerCall = "bash scripts/sim-runner.sh"

# Verzeichnisse, die auf den Server gehoeren. tests/ bleibt hier: der Server soll
# rechnen, nicht testen -- und ohne tests/ wuerde die .sln dort nicht bauen,
# deshalb bauen wir drueben gezielt die csproj.
$DeployRoots = @('src', 'config', 'scripts')

# ---------------------------------------------------------------------------
# Hilfsfunktionen
# ---------------------------------------------------------------------------

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Write-Warn([string]$Text) {
    Write-Host "    $Text" -ForegroundColor Yellow
}

# Alle Dateien, die deployt werden:
#   - bin/ und obj/     Build-Artefakte, werden drueben neu erzeugt
#   - *.local.json      maschinenspezifische Ueberlagerungen. Eine
#                       simulation.local.json auf dem Server ueberlebt dadurch
#                       jedes Deploy.
#   - *.md und *.ps1    Dokumentation und die Windows-Seite selbst. Der Server
#                       braucht beides nicht -- und weil der Fingerabdruck genau
#                       ueber diese Liste gebildet wird, wuerde sonst schon eine
#                       Korrektur in dieser README einen laufenden Lauf neu starten.
function Get-DeployFiles {
    $files = @()
    foreach ($root in $DeployRoots) {
        $full = Join-Path $RepoRoot $root
        if (-not (Test-Path $full)) { continue }
        $files += Get-ChildItem -Path $full -Recurse -File
    }

    $files | Where-Object {
        $_.FullName -notmatch '\\(bin|obj)\\' -and
        $_.Name -notlike '*.local.json' -and
        $_.Extension -notin @('.md', '.ps1')
    } | Sort-Object FullName
}

function Get-RelativePath([System.IO.FileInfo]$File) {
    $relative = $File.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
    return $relative.Replace('\', '/')
}

# Fingerabdruck ueber Pfade UND Inhalte. Damit erkennt deploy, ob sich ueberhaupt
# etwas geaendert hat -- nur dann wird ein laufender Lauf angefasst.
function Get-LocalHash {
    $lines = foreach ($file in Get-DeployFiles) {
        $fileHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        "$(Get-RelativePath $file):$fileHash"
    }

    $joined = ($lines | Sort-Object) -join "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($joined)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = [BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', ''
        # 16 Stellen reichen zum Vergleichen und bleiben in der Ausgabe lesbar.
        return $digest.Substring(0, 16).ToLower()
    }
    finally { $sha.Dispose() }
}

# Fuehrt einen Befehl auf dem Server aus. Die Umgebung (PATH, LD_LIBRARY_PATH)
# setzt sich sim-runner.sh selbst -- ~/.bashrc greift bei "ssh host befehl" nicht.
function Invoke-Remote {
    param(
        [Parameter(Mandatory)][string]$CommandLine,
        [switch]$Quiet,
        [switch]$AllowFailure
    )

    $remote = "cd '$RemoteDir' && $CommandLine"

    if ($Quiet) {
        # Bewusst 2>$null statt 2>&1: PowerShell 5.1 verpackt den stderr eines
        # nativen Programms bei 2>&1 in ErrorRecords, was zusammen mit
        # $ErrorActionPreference='Stop' auch bei Exit-Code 0 eine Ausnahme wirft.
        $output = & ssh $Server $remote 2>$null
        $code = $LASTEXITCODE
    }
    else {
        # Out-Host, nicht einfach "& ssh ...": die Ausgabe eines nativen Programms
        # landet sonst im Erfolgs-Stream der Funktion und wuerde vom | Out-Null
        # des Aufrufers mit verschluckt. Out-Host schreibt direkt auf die Konsole.
        & ssh $Server $remote | Out-Host
        $code = $LASTEXITCODE
        $output = $null
    }

    if ($code -ne 0 -and -not $AllowFailure) {
        throw "Befehl auf $Server fehlgeschlagen (Exit-Code $code): $CommandLine"
    }

    return [pscustomobject]@{ ExitCode = $code; Output = $output }
}

function Test-RemoteRunning {
    $result = Invoke-Remote -CommandLine "$RunnerCall running" -Quiet -AllowFailure
    return ($result.ExitCode -eq 0)
}

# Liegt auf dem Server ueberhaupt schon ein Stand? Ohne diese Pruefung scheitern
# status/stop/log/fetch beim allerersten Mal an einem nackten "cd: No such file".
function Test-RemoteDeployed {
    & ssh $Server "test -f '$RemoteDir/scripts/sim-runner.sh'" 2>$null
    return ($LASTEXITCODE -eq 0)
}

function Assert-RemoteDeployed {
    if (Test-RemoteDeployed) { return }

    throw ("Auf $Server liegt unter '$RemoteDir' noch kein Stand. " +
           "Erst hochladen mit:  .\scripts\sim.ps1 deploy -Server $Server")
}

function Get-RemoteHash {
    $result = Invoke-Remote -CommandLine "$RunnerCall hash" -Quiet -AllowFailure
    if ($result.ExitCode -ne 0) { return 'unbekannt' }
    return ($result.Output | Select-Object -Last 1).ToString().Trim()
}

function Invoke-Runner {
    param([Parameter(Mandatory)][string]$Arguments)
    Invoke-Remote -CommandLine "SIM_PROJECT='$Project' $RunnerCall $Arguments" | Out-Null
}

# ---------------------------------------------------------------------------
# Deploy
# ---------------------------------------------------------------------------

function Push-Sources {
    $files = Get-DeployFiles
    if ($files.Count -eq 0) { throw "Keine zu uebertragenden Dateien gefunden - stimmt das Repo-Verzeichnis?" }

    $stamp = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $listFile = Join-Path $env:TEMP "sim-deploy-$stamp.txt"
    $tarFile = Join-Path $env:TEMP "sim-deploy-$stamp.tar.gz"

    try {
        # Dateiliste fuer tar -- identisch mit der Liste, ueber die der Hash geht.
        $relatives = $files | ForEach-Object { Get-RelativePath $_ }
        [System.IO.File]::WriteAllText($listFile, ($relatives -join "`n"), (New-Object System.Text.UTF8Encoding($false)))

        Write-Host "    $($files.Count) Dateien werden gepackt ..."
        & tar.exe -czf $tarFile -C $RepoRoot -T $listFile
        if ($LASTEXITCODE -ne 0) { throw "tar ist fehlgeschlagen (Exit-Code $LASTEXITCODE)." }

        $sizeKb = [math]::Round((Get-Item $tarFile).Length / 1KB, 1)
        Write-Host "    Archiv: $sizeKb KB - uebertrage nach ${Server}:$RemoteDir"

        & ssh $Server "mkdir -p '$RemoteDir'"
        if ($LASTEXITCODE -ne 0) { throw "Konnte $RemoteDir auf $Server nicht anlegen." }

        & scp -q $tarFile "${Server}:$RemoteDir/deploy.tar.gz"
        if ($LASTEXITCODE -ne 0) { throw "scp ist fehlgeschlagen (Exit-Code $LASTEXITCODE)." }

        # Entpacken, Zeilenenden der Shell-Skripte hart auf LF ziehen (falls sie
        # doch einmal mit CRLF aus dem git-Checkout kommen) und ausfuehrbar machen.
        $unpack = "tar -xzf deploy.tar.gz && rm -f deploy.tar.gz && sed -i 's/\r`$//' scripts/*.sh && chmod +x scripts/*.sh"
        Invoke-Remote -CommandLine $unpack | Out-Null
    }
    finally {
        Remove-Item $listFile, $tarFile -ErrorAction SilentlyContinue
    }
}

function Invoke-Deploy {
    param([switch]$ThenStart)

    Write-Step "Deploy nach $Server ($RemoteDir)"

    $localHash = Get-LocalHash
    $remoteHash = Get-RemoteHash
    $wasRunning = Test-RemoteRunning
    $changed = ($localHash -ne $remoteHash)

    Write-Host "    Lokaler Stand:  $localHash"
    Write-Host "    Stand Server:   $remoteHash"
    if ($wasRunning) { Write-Host "    Auf dem Server laeuft gerade eine Simulation." }

    if (-not $changed -and -not $Force) {
        Write-Host "    Code unveraendert - kein Upload noetig." -ForegroundColor Green

        if ($ThenStart -and -not $wasRunning) {
            Invoke-Start
        }
        elseif ($ThenStart) {
            Write-Host "    Der laufende Lauf bleibt unangetastet." -ForegroundColor Green
        }
        return
    }

    # Der vom Nutzer gewuenschte Fall: geaenderte Version bei laufender Simulation.
    if ($wasRunning -and $NoRestart) {
        Write-Warn "Geaenderter Code wird hochgeladen, aber NICHT gebaut (-NoRestart)."
        Write-Warn "Der laufende Lauf rechnet mit der alten Version weiter; ein Build wuerde"
        Write-Warn "ihm die DLL unter den Fuessen wegziehen. Gebaut wird beim naechsten Start."
        Push-Sources
        Invoke-Remote -CommandLine "printf '%s' '$localHash' > .deploy_hash" | Out-Null
        return
    }

    if ($wasRunning) {
        Write-Warn "Der laufende Lauf wird gestoppt und danach mit der neuen Version neu gestartet."
        Invoke-Runner -Arguments 'stop'
    }

    Push-Sources
    Invoke-Remote -CommandLine "printf '%s' '$localHash' > .deploy_hash" | Out-Null

    Write-Step "Bauen auf $Server"
    Invoke-Runner -Arguments 'build'

    if ($wasRunning -or $ThenStart) {
        Invoke-Start
    }
}

function Invoke-Start {
    Write-Step "Starte Lauf '$Project' auf $Server"
    Invoke-Runner -Arguments 'start'
    Write-Host ""
    Write-Host "    Der Lauf haengt jetzt in der tmux-Sitzung und ueberlebt das Schliessen" -ForegroundColor Green
    Write-Host "    dieser Konsole. Zuschauen mit:  .\scripts\sim.ps1 log" -ForegroundColor Green
}

function Invoke-Fetch {
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $target = Join-Path $RepoRoot (Join-Path $Out "${Server}_$stamp")
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    Write-Step "Hole Ergebnisse nach $target"

    # Ergebnisse liegen seit TODO-25 unter Ergebnisse/<Projekt>/ -- geholt wird also
    # gezielt das Projekt, nicht der gemeinsame Topf.
    $remoteResults = "$RemoteDir/Ergebnisse/$Project"

    if ($All) {
        & scp -q -r "${Server}:$remoteResults" $target
        if ($LASTEXITCODE -ne 0) { Write-Warn "Ergebnisse-Ordner von '$Project' konnte nicht geholt werden." }
    }
    else {
        & scp -q "${Server}:$remoteResults/Simulation_Results.csv" $target
        if ($LASTEXITCODE -ne 0) { Write-Warn "Simulation_Results.csv konnte nicht geholt werden (schon ein Lauf von '$Project' gemacht?)." }

        # Die effective-config.json ist klein und beantwortet spaeter die Frage, womit
        # dieser Datensatz entstanden ist -- sie gehoert zur CSV dazu.
        & scp -q "${Server}:$remoteResults/effective-config.json" $target
        if ($LASTEXITCODE -ne 0) { Write-Warn "effective-config.json konnte nicht geholt werden." }
    }

    & scp -q "${Server}:$RemoteDir/simulation.log" $target
    if ($LASTEXITCODE -ne 0) { Write-Warn "simulation.log konnte nicht geholt werden." }

    Get-ChildItem -Path $target -Recurse -File | ForEach-Object {
        Write-Host ("    {0,10:N1} KB  {1}" -f ($_.Length / 1KB), $_.Name)
    }
    Write-Host ""
    Write-Host "    Fertig: $target" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Ablauf
# ---------------------------------------------------------------------------

try {
    switch ($Command) {
        'deploy' { Invoke-Deploy }
        'run' { Invoke-Deploy -ThenStart }

        'build' {
            Assert-RemoteDeployed
            Write-Step "Bauen auf $Server"
            Invoke-Runner -Arguments 'build'
        }

        'doctor' {
            # doctor prueft unter anderem die wirksamen Programmpfade und braucht
            # deshalb einen Stand auf dem Server. Beim allerersten Aufruf laden wir
            # ihn hier hoch -- gebaut wird dabei bewusst nicht. Die Pfadpruefung
            # selbst funktioniert erst nach einem Build, weil doctor das Programm
            # mit --print-config danach fragt (TODO-24); das meldet er auch so.
            if (-not (Test-RemoteDeployed)) {
                Write-Step "Erster Kontakt mit $Server - lade den Code hoch"
                Push-Sources
                # Kein .deploy_hash schreiben: das naechste deploy soll regulaer
                # hochladen UND bauen, statt sich auf "unveraendert" zu verlassen.
            }
            Write-Step "Pruefe Umgebung auf $Server"
            Invoke-Runner -Arguments 'doctor'
        }

        'status' {
            Assert-RemoteDeployed
            Invoke-Runner -Arguments "status $Lines"
        }

        'stop' {
            Assert-RemoteDeployed
            Invoke-Runner -Arguments 'stop'
        }

        'fetch' {
            Assert-RemoteDeployed
            Invoke-Fetch
        }

        'log' {
            Assert-RemoteDeployed
            $follow = 'yes'
            if ($NoFollow) { $follow = 'no' }
            if ($follow -eq 'yes') {
                Write-Host "Strg+C beendet nur die Anzeige - der Lauf auf dem Server laeuft weiter." -ForegroundColor Yellow
            }
            # Direkt ohne Wrapper, damit Strg+C sauber durchschlaegt.
            & ssh $Server "cd '$RemoteDir' && $RunnerCall log $Lines $follow"
        }
    }
}
catch {
    # Klartext statt PowerShell-Stacktrace: die Ursache steht in der Meldung.
    Write-Host ""
    Write-Host "FEHLER: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
