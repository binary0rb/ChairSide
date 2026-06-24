<#
.SYNOPSIS
    Operator-run maintenance for ChairSide: backup, then reset training data (and optionally seed
    clean synthetic data) or reset to an empty database for official beta go-live.

.DESCRIPTION
    Destructive but backup-first. Stops the IIS app pool, takes a timestamped backup of the SQLite
    database (.db plus .db-wal / .db-shm sidecars when present), runs the in-app maintenance CLI
    (which uses the app's own repository logic, not raw SQL), then restarts the app pool.

    Two modes:
      TrainingSeed - clears all data and seeds deterministic synthetic training data.
      EmptyBeta    - clears all data and leaves an empty board (no synthetic data).

    There is no production web endpoint and no UI button for this; it is operator-only.

.EXAMPLE
    .\Reset-ChairSideTrainingData.ps1 -Mode TrainingSeed -Confirm RESET_TRAINING_DATA

.EXAMPLE
    .\Reset-ChairSideTrainingData.ps1 -Mode EmptyBeta -Confirm RESET_EMPTY_BETA
#>
param(
    [string]$AppPath = "C:\ChairSide\App",
    [string]$DatabasePath = "C:\ChairSide\Data\chairside.db",
    [string]$BackupRoot = "C:\ChairSide\Backups",
    [string]$AppPoolName = "ChairSideBoard",

    [Parameter(Mandatory = $true)]
    [ValidateSet("TrainingSeed", "EmptyBeta")]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$Confirm
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "[ChairSide Reset] $Message"
}

function Copy-IfPresent {
    param([string]$SourcePath, [string]$DestinationPath)
    if (Test-Path -LiteralPath $SourcePath) {
        Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force -ErrorAction Stop
        Write-Step "Backed up $(Split-Path -Leaf $SourcePath)."
    }
}

function Set-AppPoolState {
    param([string]$Name, [string]$State)
    if ([string]::IsNullOrWhiteSpace($Name)) {
        Write-Step "No app pool name supplied; skipping IIS $State."
        return
    }

    Import-Module WebAdministration -ErrorAction Stop
    if ($State -eq "Stopped") {
        Write-Step "Stopping IIS app pool '$Name'."
        Stop-WebAppPool -Name $Name
        return
    }

    Write-Step "Starting IIS app pool '$Name'."
    Start-WebAppPool -Name $Name
}

# Map the selected mode to its CLI command and required confirmation token.
if ($Mode -eq "TrainingSeed") {
    $cliCommand = "reset-training-data"
    $requiredToken = "RESET_TRAINING_DATA"
}
else {
    $cliCommand = "reset-empty"
    $requiredToken = "RESET_EMPTY_BETA"
}

if ($Confirm -ne $requiredToken) {
    Write-Error "[ChairSide Reset] Confirmation token does not match. Mode '$Mode' requires -Confirm $requiredToken. No data was changed."
    exit 1
}

try {
    $resolvedAppPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($AppPath)
    $resolvedDatabasePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DatabasePath)
    $resolvedBackupRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BackupRoot)
    $appDll = Join-Path $resolvedAppPath "ChairSide.Board.dll"

    if (-not (Test-Path -LiteralPath $appDll)) {
        throw "ChairSide.Board.dll not found at: $appDll"
    }

    Write-Step "Mode:          $Mode"
    Write-Step "App DLL:       $appDll"
    Write-Step "Database:      $resolvedDatabasePath"
    Write-Step "Backup root:   $resolvedBackupRoot"
    Write-Step "App pool:      $AppPoolName"
    Write-Step "CLI command:   $cliCommand"

    $appPoolStopped = $false
    try {
        # Stop the app first so the single writer releases the DB and WAL sidecars are stable.
        Set-AppPoolState -Name $AppPoolName -State "Stopped"
        $appPoolStopped = $true

        # Timestamped backup of the full SQLite file set (db + wal + shm) before any mutation.
        if (Test-Path -LiteralPath $resolvedDatabasePath) {
            if (-not (Test-Path -LiteralPath $resolvedBackupRoot)) {
                New-Item -ItemType Directory -Path $resolvedBackupRoot -Force | Out-Null
            }

            $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
            $databaseName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedDatabasePath)
            $backupDir = Join-Path $resolvedBackupRoot "$databaseName-pre-$Mode-$timestamp"
            New-Item -ItemType Directory -Path $backupDir | Out-Null

            Copy-IfPresent -SourcePath $resolvedDatabasePath -DestinationPath (Join-Path $backupDir ([System.IO.Path]::GetFileName($resolvedDatabasePath)))
            Copy-IfPresent -SourcePath "$resolvedDatabasePath-wal" -DestinationPath (Join-Path $backupDir ([System.IO.Path]::GetFileName("$resolvedDatabasePath-wal")))
            Copy-IfPresent -SourcePath "$resolvedDatabasePath-shm" -DestinationPath (Join-Path $backupDir ([System.IO.Path]::GetFileName("$resolvedDatabasePath-shm")))
            Write-Step "Backup created: $backupDir"
        }
        else {
            Write-Step "No existing database at $resolvedDatabasePath; nothing to back up. The app will create a fresh DB."
        }

        # Run the in-app maintenance command under Production so it targets the production DB path.
        $previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
        $env:ASPNETCORE_ENVIRONMENT = "Production"
        try {
            Write-Step "Running maintenance CLI..."
            & dotnet $appDll --maintenance $cliCommand --confirm $requiredToken
            if ($LASTEXITCODE -ne 0) {
                throw "Maintenance CLI exited with code $LASTEXITCODE."
            }
        }
        finally {
            $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
        }

        Write-Step "Maintenance command completed."
    }
    finally {
        # Always attempt to bring the app back up, even if backup/CLI failed midway.
        if ($appPoolStopped) {
            Set-AppPoolState -Name $AppPoolName -State "Started"
        }
    }

    Write-Step "Done. Verify /reports.html before training/go-live."
    exit 0
}
catch {
    Write-Error "[ChairSide Reset] Failed: $($_.Exception.Message)"
    exit 1
}
