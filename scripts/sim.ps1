<#
.SYNOPSIS
    Controls the optimization run on the remote Linux server from Windows.

.DESCRIPTION
    Windows side of the remote control. Packages the source code, uploads it to the
    server via SSH, and invokes scripts/sim-runner.sh there to handle the Linux-side
    execution (tmux, xvfb-run, paths, start/stop).

    Transfer uses tar + scp rather than git so that uncommitted local changes are also
    deployed and no GitHub token is required on the remote machine. rsync is not
    available natively on Windows.

    If a simulation is already running during deploy and the code has changed, it is
    stopped and restarted with the new version ("-NoRestart" prevents this).
    If the code is unchanged, a running simulation is left untouched.

.PARAMETER Command
    deploy     Upload code and build (restarts if needed, see above)
    run        deploy + start -- the everyday command
    resume     Continue an interrupted run (after server reboot, crash, or stop).
               Does not upload or build: continues with the code on the server.
    status     Is anything running? How long? Recent log lines
    log        Tail the log file live (Ctrl+C only stops tailing, not the run)
    stop       Stop the run, tmux session, and lingering child processes
    fetch      Download results from the server to Windows (standard, -Champion, or -All)
    doctor     Check tools and paths on the server without starting anything
    build      Build only, do not start
    projects   List projects known to the server and existing results
    delete     Clean slate: delete results of ALL projects and all logs on the server.
               Code, build, and *.local.json remain intact. Prompts for confirmation.
    calibrate  Deploy and start isolated mesh calibration on the server
    recheck    Re-evaluate a single variant on a finer mesh (or unsteady URANS)

.EXAMPLE
    .\scripts\sim.ps1 doctor
    Before the first run: checks SU2, OpenFOAM (ESI v2506 / openfoam2506, isolated OpenMPI),
    MPICH vs OpenMPI isolation, gmsh, dotnet, tmux, libpicogk.so, and optional opt-py.

.EXAMPLE
    .\scripts\sim.ps1 run
    Upload code, build, and start in tmux. The terminal window can be closed afterwards.

.EXAMPLE
    .\scripts\sim.ps1 log
    Follow the log live. Ctrl+C only exits the viewer.

.EXAMPLE
    .\scripts\sim.ps1 delete
    Before a completely fresh run: shows what is on the server and deletes it after
    confirmation. Use fetch beforehand to keep anything you need.

.EXAMPLE
    .\scripts\sim.ps1 fetch
    Downloads standard results (CSV, effective-config.json, log, histories) to .\server-results\

.EXAMPLE
    .\scripts\sim.ps1 fetch -Champion
    Downloads standard results plus VTU files of the champion to .\server-results\

.EXAMPLE
    .\scripts\sim.ps1 fetch -All
    Downloads the entire results folder (including all 3D files) to .\server-results\
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('deploy', 'run', 'resume', 'status', 'log', 'stop', 'fetch', 'doctor', 'build', 'projects', 'delete', 'calibrate', 'recheck')]
    [string]$Command = 'status',

    # Host alias from ~/.ssh/config
    [string]$Server = 'BIC_12',

    # Target directory on the server, relative to the home directory.
    [string]$RemoteDir = 'Documents/FluidForge',

    # Project selector. The name matches the folder name under src/projects/
    # and is forwarded as SIM_PROJECT to sim-runner.sh.
    #
    # The default stays here rather than on the server side: on the server it would
    # be invisible, whereas here it is explicit and reported whenever a project-scoped
    # command uses it. List available projects with: .\scripts\sim.ps1 projects
    [string]$Project = 'MantaAuv',

    # Keep a running simulation alive even when code has changed
    [switch]$NoRestart,

    # deploy: force upload even if nothing changed
    # delete: delete without confirmation prompt
    [switch]$Force,

    # fetch: download entire results folder instead of only CSV and log
    [switch]$All,

    # fetch: include champion and top-N VTU files
    [switch]$Champion,

    # log/status: number of lines to display
    [int]$Lines = 40,

    # log: print once instead of following live
    [switch]$NoFollow,

    # fetch: local target folder on Windows
    [string]$Out = 'server-results',

    # recheck: variant name (e.g. Gen3_Var2)
    [string]$Variant = '',

    # recheck: mesh refinement steps relative to the effective factor (default 1)
    [int]$Finer = 1,

    # recheck: time-accurate URANS simulation (dual-time stepping) for single-variant check
    [switch]$Unsteady
)

$ErrorActionPreference = 'Stop'

# Force UTF-8 for console and PowerShell pipelines so Unicode box characters and symbols
# from ssh/Linux do not render as garbled codepage-850 text in the VS Code terminal.
[Console]::InputEncoding  = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding           = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$RunnerCall = "bash scripts/sim-runner.sh"

# SSH timeouts for EVERY ssh and scp call in this script -- intentionally generous:
#
#   ConnectTimeout=60       Connection setup and key exchange may take up to a full
#                           minute -- even when the server is under full CPU load
#                           and sshd responds slowly.
#   ServerAliveInterval=30  After 30 s of silence, ssh sends a keepalive packet ...
#   ServerAliveCountMax=6   ... and gives up only after 6 unanswered probes, i.e.,
#                           3 minutes of complete connection loss.
#
# Without these values, a dead VPN tunnel waits INDEFINITELY without any message.
# sshd answers keepalive probes itself even when the remote command produces no output
# for a long time (build, stop) -- so a quiet but healthy command is not aborted.
# It also keeps the VPN tunnel active during "log" when otherwise idle.
#
# If a connection drops here, it ONLY affects the link from this workstation. A run
# inside the tmux session on the server continues undisturbed.
$SshOptions = @('-o', 'ConnectTimeout=60', '-o', 'ServerAliveInterval=30', '-o', 'ServerAliveCountMax=6')

# Directories deployed to the server. tests/ stays local: the server should compute,
# not run unit tests -- and without tests/ the .sln would not build there, so we build
# the csproj directly on the server.
$DeployRoots = @('src', 'config', 'scripts', 'third_party')

# ---------------------------------------------------------------------------
# Helper functions
# ---------------------------------------------------------------------------

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Write-Warn([string]$Text) {
    Write-Host "    $Text" -ForegroundColor Yellow
}

# All files included in deployment:
#   - bin/ and obj/     Build artifacts, regenerated on the server
#   - *.local.json      Machine-specific overrides. A simulation.local.json on the
#                       server survives every deploy.
#   - *.md, *.ps1, *.pdf Documentation, reports, and the Windows client itself.
#                       The server needs none of these -- and because the hash is
#                       computed over this exact list, editing a README will not
#                       restart a running simulation.
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
        $_.Extension -notin @('.md', '.ps1', '.pdf')
    } | Sort-Object FullName
}

function Get-RelativePath([System.IO.FileInfo]$File) {
    $relative = $File.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
    return $relative.Replace('\', '/')
}

# Fingerprint over paths AND contents so deploy can detect whether anything actually
# changed -- only then is a running simulation touched.
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
        # 16 hex characters are sufficient for comparison and stay readable in output.
        return $digest.Substring(0, 16).ToLower()
    }
    finally { $sha.Dispose() }
}

# Read commit and branch for deploy-info.json directly from .git files:
# locally, git may only exist inside GitHub Desktop and not on PATH.
function Get-GitState {
    $state = [ordered]@{ Commit = $null; Branch = $null }
    $gitDir = Join-Path $RepoRoot '.git'
    $headFile = Join-Path $gitDir 'HEAD'
    if (-not (Test-Path -LiteralPath $headFile)) { return $state }

    $head = (Get-Content -LiteralPath $headFile -Raw).Trim()
    if ($head -match '^ref:\s*(.+)$') {
        $ref = $Matches[1].Trim()
        $state.Branch = $ref -replace '^refs/heads/', ''

        $loose = Join-Path $gitDir ($ref -replace '/', '\')
        if (Test-Path -LiteralPath $loose) {
            $state.Commit = (Get-Content -LiteralPath $loose -Raw).Trim()
        }
        else {
            $packed = Join-Path $gitDir 'packed-refs'
            if (Test-Path -LiteralPath $packed) {
                foreach ($line in Get-Content -LiteralPath $packed) {
                    $parts = $line.Split(' ')
                    if ($parts.Count -eq 2 -and $parts[1] -eq $ref) { $state.Commit = $parts[0]; break }
                }
            }
        }
    }
    elseif ($head -match '^[0-9a-f]{40}$') {
        $state.Commit = $head
    }

    return $state
}

# Returns "true"/"false" when git is found (PATH or GitHub Desktop), otherwise "unknown".
function Get-UncommittedChanges {
    try {
        $git = $null
        $command = Get-Command git -ErrorAction SilentlyContinue
        if ($command) { $git = $command.Source }
        elseif ($env:LOCALAPPDATA -and (Test-Path (Join-Path $env:LOCALAPPDATA 'GitHubDesktop'))) {
            $found = Get-ChildItem "$env:LOCALAPPDATA\GitHubDesktop" -Filter git.exe -Recurse -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | Select-Object -First 1
            if ($found) { $git = $found.FullName }
        }
        if (-not $git) { return 'unknown' }

        $changes = & $git -C $RepoRoot status --porcelain 2>$null
        if ($LASTEXITCODE -ne 0) { return 'unknown' }
        if ($changes) { return 'true' }
        return 'false'
    }
    catch { return 'unknown' }
}

# Writes deploy-info.json next to .deploy_hash. The application reads code provenance
# for run-info.json from it because there is no .git on the server. Payload is sent
# as Base64 because PowerShell 5.1 does not forward double quotes cleanly to ssh.
function Write-RemoteDeployInfo([string]$LocalHash) {
    $git = Get-GitState
    $info = [ordered]@{
        Commit             = $git.Commit
        Branch             = $git.Branch
        DeployHash         = $LocalHash
        DeployedAt         = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
        UncommittedChanges = Get-UncommittedChanges
    }
    $json = $info | ConvertTo-Json -Compress
    $base64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))
    Invoke-Remote -CommandLine "printf '%s' '$base64' | base64 -d > deploy-info.json" | Out-Null
}

function Invoke-Remote {
    param(
        [Parameter(Mandatory)][string]$CommandLine,
        [switch]$Quiet,
        [switch]$AllowFailure
    )

    $remote = "cd '$RemoteDir' && $CommandLine"

    if ($Quiet) {
        # Temporarily relax ErrorActionPreference: in PowerShell 5.1 native stderr
        # triggers a terminating ErrorRecord under 'Stop' even with 2>$null.
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = & ssh @SshOptions $Server $remote 2>$null
            $code = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $prevEap
        }
    }
    else {
        # Out-Host instead of plain "& ssh ...": native output would otherwise enter
        # the function's success stream and get swallowed by "| Out-Null" at call sites.
        & ssh @SshOptions $Server $remote | Out-Host
        $code = $LASTEXITCODE
        $output = $null
    }

    if ($code -ne 0 -and -not $AllowFailure) {
        throw "Command on $Server failed (exit code $code): $CommandLine"
    }

    return [pscustomobject]@{ ExitCode = $code; Output = $output }
}

function Test-RemoteRunning {
    $result = Invoke-Remote -CommandLine "$RunnerCall running" -Quiet -AllowFailure
    return ($result.ExitCode -eq 0)
}

# Check whether any code has been deployed to the server yet. Without this check,
# status/stop/log/fetch would fail on first use with a bare "cd: No such file".
function Test-RemoteDeployed {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & ssh @SshOptions $Server "test -f '$RemoteDir/scripts/sim-runner.sh'" 2>$null
        return ($LASTEXITCODE -eq 0)
    }
    finally {
        $ErrorActionPreference = $prevEap
    }
}

function Assert-RemoteDeployed {
    if (Test-RemoteDeployed) { return }

    throw ("No deployment found on $Server under '$RemoteDir'. " +
           "Upload first with:  .\scripts\sim.ps1 deploy -Server $Server")
}

function Get-RemoteLastProject {
    $result = Invoke-Remote -CommandLine "$RunnerCall lastproject" -Quiet -AllowFailure
    if ($result.ExitCode -ne 0 -or -not $result.Output) { return $null }
    $last = ($result.Output | Select-Object -Last 1).ToString().Trim()
    if ($last) { return $last }
    return $null
}

function Get-RemoteHash {
    $result = Invoke-Remote -CommandLine "$RunnerCall hash" -Quiet -AllowFailure
    if ($result.ExitCode -ne 0) { return 'unknown' }
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
    if ($files.Count -eq 0) { throw "No files found to deploy - is the repository directory correct?" }

    $stamp = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $listFile = Join-Path $env:TEMP "sim-deploy-$stamp.txt"
    $tarFile = Join-Path $env:TEMP "sim-deploy-$stamp.tar.gz"

    try {
        # File list for tar -- identical to the list used for hashing.
        $relatives = $files | ForEach-Object { Get-RelativePath $_ }
        [System.IO.File]::WriteAllText($listFile, ($relatives -join "`n"), (New-Object System.Text.UTF8Encoding($false)))

        Write-Host "    Packing $($files.Count) files ..."
        & tar.exe -czf $tarFile -C $RepoRoot -T $listFile
        if ($LASTEXITCODE -ne 0) { throw "tar failed (exit code $LASTEXITCODE)." }

        $sizeKb = [math]::Round((Get-Item $tarFile).Length / 1KB, 1)
        Write-Host "    Archive: $sizeKb KB - uploading to ${Server}:$RemoteDir"

        & ssh @SshOptions $Server "mkdir -p '$RemoteDir'"
        if ($LASTEXITCODE -ne 0) { throw "Could not create $RemoteDir on $Server." }

        & scp @SshOptions -q $tarFile "${Server}:$RemoteDir/deploy.tar.gz"
        if ($LASTEXITCODE -ne 0) { throw "scp failed (exit code $LASTEXITCODE)." }

        # Remove orphaned files BEFORE extracting.
        #
        # A deploy only overwrites; it never deletes. If a file is renamed or moved,
        # the old copy would remain on the server -- for C# files that means duplicate
        # types and build failure (CS0101).
        #
        # Excluded: *.local.json (meant to survive deploys) and bin/ / obj/ so builds
        # stay incremental and libpicogk.so remains in place.
        #
        # At this point the tarball is already on the server: if unpacking fails,
        # running "tar -xzf deploy.tar.gz" in the repo directory recovers it.
        $prune = "find src scripts config third_party -type f ! -name '*.local.json' " +
                 "-not -path '*/bin/*' -not -path '*/obj/*' -delete 2>/dev/null; " +
                 "find src scripts config third_party -type d -empty -delete 2>/dev/null; true"

        # Extract, normalize shell script line endings to LF (in case they were checked
        # out with CRLF on Windows), and make them executable.
        $unpack = "$prune && tar -xzf deploy.tar.gz && rm -f deploy.tar.gz && sed -i 's/\r`$//' scripts/*.sh && chmod +x scripts/*.sh"
        Invoke-Remote -CommandLine $unpack | Out-Null
    }
    finally {
        Remove-Item $listFile, $tarFile -ErrorAction SilentlyContinue
    }
}

function Invoke-Deploy {
    param([switch]$ThenStart, [switch]$ThenCalibrate)

    Write-Step "Deploy to $Server ($RemoteDir)"

    $localHash = Get-LocalHash
    if (Test-RemoteDeployed) {
        $remoteHash = Get-RemoteHash
        $wasRunning = Test-RemoteRunning
    }
    else {
        $remoteHash = 'none (initial deploy)'
        $wasRunning = $false
    }
    $changed = ($localHash -ne $remoteHash)

    Write-Host "    Local hash:  $localHash"
    Write-Host "    Server hash: $remoteHash"
    if ($wasRunning) { Write-Host "    A simulation is currently running on the server." }

    if (-not $changed -and -not $Force) {
        Write-Host "    Code unchanged - no upload needed." -ForegroundColor Green

        if ($ThenStart -and -not $wasRunning) {
            Invoke-Start
        }
        elseif ($ThenCalibrate -and -not $wasRunning) {
            Invoke-Calibrate
        }
        elseif ($ThenStart -or $ThenCalibrate) {
            Write-Host "    Keeping the running simulation untouched." -ForegroundColor Green
        }
        return
    }

    if ($wasRunning -and $NoRestart) {
        Write-Warn "Changed code will be uploaded, but NOT built (-NoRestart)."
        Write-Warn "The active run continues with the old version; building would"
        Write-Warn "pull the DLL out from under the running process."
        Push-Sources
        # Intentionally do NOT write .deploy_hash: the code is only considered deployed
        # once it is built. Otherwise the next deploy would report "unchanged" and
        # skip the build indefinitely.
        Write-Warn "Changes remain pending; the next deploy will build them."
        return
    }

    if ($wasRunning) {
        Write-Warn "Stopping the active run and restarting with the new version."
        Invoke-Runner -Arguments 'stop'
    }

    Push-Sources

    Write-Step "Building on $Server"
    Invoke-Runner -Arguments 'build'

    # Write hash ONLY after a successful build. If written before a failed build,
    # subsequent deploys would report "code unchanged" and never rebuild.
    Invoke-Remote -CommandLine "printf '%s' '$localHash' > .deploy_hash" | Out-Null

    # Record commit, branch, and deploy timestamp for run-info.json.
    # Informational only -- failure here must not fail the deploy.
    try { Write-RemoteDeployInfo -LocalHash $localHash }
    catch { Write-Warn "Could not write deploy-info.json: $($_.Exception.Message)" }

    if ($wasRunning -or $ThenStart) {
        Invoke-Start
    }
    elseif ($ThenCalibrate) {
        Invoke-Calibrate
    }
}

function Invoke-Start {
    Write-Step "Starting run '$Project' on $Server"
    Invoke-Runner -Arguments 'start'
    Write-Host ""
    Write-Host "    The run is now detached in a tmux session and survives closing" -ForegroundColor Green
    Write-Host "    this console. Follow live with:  .\scripts\sim.ps1 log" -ForegroundColor Green
}

# Resume an interrupted run. Intentionally NO push and NO build: new code requires
# a new run. Warns if local code differs from the server.
function Invoke-Resume {
    Write-Step "Resuming interrupted run '$Project' on $Server"

    $localHash = Get-LocalHash
    $remoteHash = Get-RemoteHash
    if ($localHash -ne $remoteHash) {
        Write-Warn "Local code differs from server. resume continues with the server's version;"
        Write-Warn "new code requires a fresh run (run)."
    }

    Invoke-Runner -Arguments 'resume'
    Write-Host ""
    Write-Host "    The run is now detached in a tmux session and survives closing" -ForegroundColor Green
    Write-Host "    this console. Follow live with:  .\scripts\sim.ps1 log" -ForegroundColor Green
}

function Invoke-Calibrate {
    Write-Step "Starting mesh calibration for '$Project' on $Server"
    Invoke-Runner -Arguments 'calibrate'
    Write-Host ""
    Write-Host "    Calibration is now detached in a tmux session and survives closing" -ForegroundColor Green
    Write-Host "    this console. Follow live with:  .\scripts\sim.ps1 log" -ForegroundColor Green
}

function Invoke-Recheck {
    Write-Step "Starting recheck for '$Project' on $Server"
    $rArgs = "recheck"
    if ($Variant) { $rArgs += " '$Variant'" }
    if ($Finer) { $rArgs += " $Finer" }
    if ($Unsteady) { $rArgs += " --unsteady" }
    Invoke-Runner -Arguments $rArgs
    Write-Host ""
    Write-Host "    Recheck is now detached in a tmux session and survives closing" -ForegroundColor Green
    Write-Host "    this console. Follow live with:  .\scripts\sim.ps1 log" -ForegroundColor Green
}

function Invoke-Fetch {
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $scope = if ($All) { 'all' } elseif ($Champion) { 'champion' } else { 'standard' }
    $target = Join-Path $RepoRoot (Join-Path $Out "${Server}_${Project}_$stamp")
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    Write-Step "Packing results for '$Project' on $Server (scope: $scope)"

    $packResult = Invoke-Remote -CommandLine "$RunnerCall pack '$Project' '$scope'" -Quiet -AllowFailure
    if ($packResult.ExitCode -ne 0 -or -not $packResult.Output) {
        throw "Packing on $Server failed (exit code $($packResult.ExitCode))."
    }

    $remoteArchive = ($packResult.Output | Select-Object -Last 1).ToString().Trim()
    if (-not $remoteArchive) {
        throw "Server did not return an archive path."
    }

    $localArchive = Join-Path $env:TEMP "sim-fetch-${Project}-$stamp.tar.gz"

    try {
        Write-Step "Downloading archive from $Server"
        & scp @SshOptions -q "${Server}:$remoteArchive" $localArchive
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to download archive from $Server."
        }

        Write-Step "Extracting results to $target"
        & tar.exe -xzf $localArchive -C $target
        if ($LASTEXITCODE -ne 0) {
            throw "Extraction with tar.exe failed (exit code $LASTEXITCODE)."
        }
    }
    finally {
        Remove-Item -LiteralPath $localArchive -Force -ErrorAction SilentlyContinue
        & ssh @SshOptions $Server "rm -f '$remoteArchive'" 2>$null
    }

    $logPath = Join-Path $target 'simulation.log'
    if (Test-Path -LiteralPath $logPath) {
        $logLines = Get-Content -LiteralPath $logPath -TotalCount 30 -ErrorAction SilentlyContinue
        $match = $logLines | Select-String -Pattern '^\s*(?:Project|Projekt):\s*(.+)$' | Select-Object -First 1
        if ($match) {
            $logProject = $match.Matches[0].Groups[1].Value.Trim()
            if ($logProject -and $logProject -ne $Project) {
                Write-Warn "Fetched log header reports project '$logProject', not selected '$Project'."
            }
        }
    }

    $csvPath = Join-Path $target 'results.csv'
    if (-not (Test-Path -LiteralPath $csvPath)) {
        Write-Host ""
        Write-Host "ERROR: results.csv for project '$Project' is missing after extraction." -ForegroundColor Red
        Write-Host "Results available on $Server for:" -ForegroundColor Red
        $projectsResult = Invoke-Remote -CommandLine "$RunnerCall projects" -Quiet -AllowFailure
        if ($projectsResult.Output) {
            $projectsResult.Output | ForEach-Object { Write-Host "    $_" }
        }
        exit 1
    }

    Get-ChildItem -Path $target -Recurse -File | ForEach-Object {
        Write-Host ("    {0,10:N1} KB  {1}" -f ($_.Length / 1KB), $_.Name)
    }
    Write-Host ""
    Write-Host "    Done: $target" -ForegroundColor Green
}

function Invoke-Delete {
    Write-Step "Run artifacts on $Server"

    # List first before deleting across all projects so the user can see what will be removed.
    $listing = Invoke-Remote -CommandLine "$RunnerCall delete" -Quiet -AllowFailure
    $listing.Output | ForEach-Object { Write-Host "    $_" }

    switch ($listing.ExitCode) {
        0 { }
        3 { return }
        1 { throw "Stop the active run first:  .\scripts\sim.ps1 stop" }
        # 2 = "Unknown command": older sim-runner.sh on the server.
        2 { throw "Server does not support 'delete' yet (outdated deployment). Deploy first:  .\scripts\sim.ps1 deploy" }
        default { throw "Listing on $Server failed (exit code $($listing.ExitCode))." }
    }

    if (-not $Force) {
        Write-Host ""
        Write-Warn "This permanently deletes results of ALL projects on the server."
        Write-Warn "Fetch anything you want to keep first:  .\scripts\sim.ps1 fetch -All -Project <Name>"
        $answer = Read-Host "    Really delete? Type 'yes' to confirm"
        if ($answer -ne 'yes') {
            Write-Host "    Aborted - nothing deleted." -ForegroundColor Green
            return
        }
    }

    Invoke-Remote -CommandLine "$RunnerCall delete yes" | Out-Null
}

# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

try {
    # Announce which project will be used when -Project is omitted.
    # For fetch and status, query the last project run on the server;
    # for deploy, run, resume, doctor, calibrate, and recheck, fall back to $Project.
    if (-not $PSBoundParameters.ContainsKey('Project')) {
        if (@('status', 'fetch') -contains $Command) {
            Assert-RemoteDeployed
            $remoteLast = Get-RemoteLastProject
            if ($remoteLast) {
                $Project = $remoteLast
                Write-Host "No -Project specified - last run on $Server was '$Project', using it." -ForegroundColor Yellow
            }
            else {
                Write-Warn "No -Project specified and no previous run found on $Server; using default '$Project'."
                Write-Warn "Available projects:  .\scripts\sim.ps1 projects"
            }
        }
        elseif (@('deploy', 'run', 'resume', 'doctor', 'calibrate', 'recheck') -contains $Command) {
            Write-Warn "No -Project specified, using default '$Project'."
            Write-Warn "Available projects:  .\scripts\sim.ps1 projects"
        }
    }

    switch ($Command) {
        'deploy' { Invoke-Deploy }
        'run' { Invoke-Deploy -ThenStart }
        'calibrate' { Invoke-Deploy -ThenCalibrate }
        'resume' {
            Assert-RemoteDeployed
            Invoke-Resume
        }
        'recheck' {
            Assert-RemoteDeployed
            Invoke-Recheck
        }

        'build' {
            Assert-RemoteDeployed
            Write-Step "Building on $Server"
            Invoke-Runner -Arguments 'build'
        }

        'doctor' {
            # doctor inspects effective project paths and therefore requires code on the server.
            # On the very first call, upload without building; path checks succeed after a build
            # because doctor queries the application via --print-config.
            if (-not (Test-RemoteDeployed)) {
                Write-Step "First contact with $Server - uploading code"
                Push-Sources
                # Do not write .deploy_hash: the next deploy should upload AND build normally.
            }
            Write-Step "Checking environment on $Server"
            Invoke-Runner -Arguments 'doctor'
        }

        'projects' {
            Assert-RemoteDeployed
            Write-Step "Projects on $Server"
            # Use Invoke-Remote instead of Invoke-Runner: this command intentionally sets
            # NO SIM_PROJECT so the server lists projects without requiring one.
            Invoke-Remote -CommandLine "$RunnerCall projects" | Out-Null
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

        'delete' {
            Assert-RemoteDeployed
            Invoke-Delete
        }

        'log' {
            Assert-RemoteDeployed
            $follow = 'yes'
            if ($NoFollow) { $follow = 'no' }
            if ($follow -eq 'yes') {
                Write-Host "Ctrl+C only stops the log viewer - the simulation on the server keeps running." -ForegroundColor Yellow
            }
            # Direct ssh call without wrapper so Ctrl+C passes through cleanly.
            & ssh @SshOptions $Server "cd '$RemoteDir' && $RunnerCall log $Lines $follow"
        }
    }
}
catch {
    # Plain message instead of a PowerShell stack trace.
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
