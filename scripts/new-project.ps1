<#
.SYNOPSIS
    Creates a new project by copying src/projects/_Template3D (or _Template2D) and substituting the project name.

.DESCRIPTION
    A project is a folder under src/projects/ (the folder name IS the project name).
    This script copies the selected template to the desired name and replaces the
    placeholder word "MyProject" (or "MyProject2D") in file names, class names,
    namespaces, and JSON file headers.

    No manual registration is required: ProjectRegistry discovers the new
    IProjectDefinition at startup via reflection.

    JSON files are intentionally copied as files rather than generated from code,
    preserving explanatory comment headers for new users.

.PARAMETER Name
    The project name and folder name. Must be a valid C# identifier because it also
    becomes the class name (<Name>Project) and namespace (FluidForge.Projects.<Name>).

.PARAMETER Template
    Template to copy: '3D' (uses _Template3D, default) or '2D' (uses _Template2D).

.EXAMPLE
    .\scripts\new-project.ps1 Propeller
    Creates src/projects/Propeller/ and prints the next steps.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Name,

    [Parameter(Position = 1)]
    [ValidateSet('3D', '2D')]
    [string]$Template = '3D'
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectsRoot = Join-Path $RepoRoot 'src\projects'
$TemplateDir = if ($Template -eq '2D') {
    Join-Path $ProjectsRoot '_Template2D'
} else {
    Join-Path $ProjectsRoot '_Template3D'
}

# Placeholder token used in the template files.
$Placeholder = 'MyProject'
$ActivePlaceholder = if ($Template -eq '2D') { 'MyProject2D' } else { 'MyProject' }

# File extensions in which the placeholder is replaced. Binary or STL files are left untouched.
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
# Validate before creating anything
# ---------------------------------------------------------------------------

# The name becomes a C# class name and namespace segment. Hyphens, spaces, or a leading
# digit would fail during the next build with a less obvious compiler error.
if ($Name -notmatch '^[A-Za-z][A-Za-z0-9_]*$') {
    throw "Invalid project name '$Name'. Must start with a letter followed by letters, digits, or underscores -- it also becomes the class name (${Name}Project) and namespace (FluidForge.Projects.${Name})."
}

if ($Name -eq $Placeholder -or $Name -eq 'MyProject2D') {
    throw "'$Placeholder' is the template placeholder and cannot be used as a project name. Choose the actual name for your project."
}

if (-not (Test-Path $TemplateDir)) {
    throw "Template directory missing: $TemplateDir. Cannot create project without it."
}

$TargetDir = Join-Path $ProjectsRoot $Name

if (Test-Path $TargetDir) {
    throw "Folder src/projects/$Name already exists. This script does not overwrite -- remove it manually or choose another name."
}

# Check case-insensitively: Linux is case-sensitive while Windows is not. Two folders
# differing only by case would diverge on the server and cause name collisions.
$existing = Get-ChildItem -Path $ProjectsRoot -Directory | Where-Object { $_.Name -ieq $Name }
if ($existing) {
    throw "Folder src/projects/$($existing.Name) already exists and differs only in letter case. Choose a different name."
}

# ---------------------------------------------------------------------------
# Copy
# ---------------------------------------------------------------------------

$templateName = if ($Template -eq '2D') { '_Template2D' } else { '_Template3D' }
Write-Step "Creating src/projects/$Name (copy of $templateName)"

Copy-Item -Path $TemplateDir -Destination $TargetDir -Recurse

# The template README describes the template itself and would be misleading in a new project.
$copiedReadme = Join-Path $TargetDir 'README.md'
if (Test-Path $copiedReadme) { Remove-Item $copiedReadme }

# ---------------------------------------------------------------------------
# Rename and substitute
# ---------------------------------------------------------------------------

Write-Step "Substituting name: $ActivePlaceholder -> $Name"

# Rename files first, then replace file contents so printed file names match final names.
foreach ($file in Get-ChildItem -Path $TargetDir -Recurse -File) {
    $matchWord = if ($file.Name -like "*$ActivePlaceholder*") { $ActivePlaceholder } else { $Placeholder }
    if ($file.Name -like "*$matchWord*") {
        $newName = $file.Name.Replace($matchWord, $Name)
        Rename-Item -LiteralPath $file.FullName -NewName $newName
        Write-Info "$($file.Name)  ->  $newName"
    }
}

$totalReplacements = 0

foreach ($file in Get-ChildItem -Path $TargetDir -Recurse -File) {
    if ($TextExtensions -notcontains $file.Extension.ToLowerInvariant()) { continue }

    # Read and write via .NET APIs to ensure UTF-8 without BOM under PowerShell 5.1.
    $text = [System.IO.File]::ReadAllText($file.FullName)

    $count = 0
    if ($text.Contains($ActivePlaceholder)) {
        $count += ([regex]::Matches($text, [regex]::Escape($ActivePlaceholder))).Count
        $text = $text.Replace($ActivePlaceholder, $Name)
    }
    if ($ActivePlaceholder -ne $Placeholder -and $text.Contains($Placeholder)) {
        $count += ([regex]::Matches($text, [regex]::Escape($Placeholder))).Count
        $text = $text.Replace($Placeholder, $Name)
    }

    if ($count -eq 0) { continue }

    [System.IO.File]::WriteAllText($file.FullName, $text, (New-Object System.Text.UTF8Encoding($false)))

    $totalReplacements += $count
    Write-Info "$($file.Name): $count occurrence(s)"
}

# Verify no placeholder tokens remain.
$leftovers = Get-ChildItem -Path $TargetDir -Recurse -File |
    Where-Object { $TextExtensions -contains $_.Extension.ToLowerInvariant() } |
    Where-Object {
        $c = [System.IO.File]::ReadAllText($_.FullName)
        $c.Contains($ActivePlaceholder) -or ($ActivePlaceholder -ne $Placeholder -and $c.Contains($Placeholder))
    }

if ($leftovers) {
    Write-Warn "Placeholder '$Placeholder' still found in: $(($leftovers | ForEach-Object { $_.Name }) -join ', ')"
    Write-Warn "Please update manually -- this should not happen."
}

Write-Info "$totalReplacements occurrence(s) replaced."

# ---------------------------------------------------------------------------
# Next steps
# ---------------------------------------------------------------------------

Write-Step "Done. src/projects/$Name/"

Write-Host @"
    Next steps:

    1. Geometry    src/projects/$Name/${Name}GeometryGenerator.cs
                   The default box is a placeholder -- replace BuildVoxelModel.

    2. Fitness     src/projects/$Name/${Name}FitnessCalculator.cs
                   Fitness = 1 / Drag. The framework MAXIMIZES fitness.

    3. Parameters  src/projects/$Name/project.json
                   Parameters, bounds, targets. Mirror the same defaults in
                   CreateDefaults() inside ${Name}Project.cs (code layer).

    4. Cleanup     simulation.json and solvers/*.json list EVERY key with its code
                   default as a reference: remove anything you do not override.
                   MaxIterations=5 and VariantsPerIteration=5 are intentionally small
                   (defaults 10 x 10), and MeshRefinementFactor is omitted because
                   auto mesh calibration is enabled.

    Verify the configuration chain locally before deploying:

        dotnet build FluidForge.sln
        dotnet run --project src\FluidForge --no-build -- $Name --print-config

    Then run on the server:

        .\scripts\sim.ps1 run -Project $Name

    $templateName and existing projects remain untouched; results are isolated under results/$Name/.
"@
