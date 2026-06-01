param(
    [Parameter(Mandatory = $true)]
    [string]$BackupSourcePath,

    [string]$DatabasePath = "C:\ChairSide\Data\chairside.db",
    [string]$BackupDirectory = "C:\ChairSide\Backups",
    [string]$AppPoolName = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "[ChairSide Restore] $Message"
}

function Copy-IfPresent {
    param(
        [string]$SourcePath,
        [string]$DestinationPath
    )

    if (Test-Path -LiteralPath $SourcePath) {
        Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force -ErrorAction Stop
        Write-Step "Restored $(Split-Path -Leaf $SourcePath)."
    }
}

function Backup-CurrentDatabase {
    param(
        [string]$CurrentDatabasePath,
        [string]$DestinationRoot
    )

    if (-not (Test-Path -LiteralPath $DestinationRoot)) {
        New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null
    }

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $databaseName = [System.IO.Path]::GetFileNameWithoutExtension($CurrentDatabasePath)
    $safetyDirectory = Join-Path $DestinationRoot "$databaseName-pre-restore-$timestamp"
    New-Item -ItemType Directory -Path $safetyDirectory | Out-Null

    Copy-IfPresent -SourcePath $CurrentDatabasePath -DestinationPath (Join-Path $safetyDirectory ([System.IO.Path]::GetFileName($CurrentDatabasePath)))
    Copy-IfPresent -SourcePath "$CurrentDatabasePath-wal" -DestinationPath (Join-Path $safetyDirectory ([System.IO.Path]::GetFileName("$CurrentDatabasePath-wal")))
    Copy-IfPresent -SourcePath "$CurrentDatabasePath-shm" -DestinationPath (Join-Path $safetyDirectory ([System.IO.Path]::GetFileName("$CurrentDatabasePath-shm")))

    return $safetyDirectory
}

function Set-AppPoolState {
    param(
        [string]$Name,
        [string]$State
    )

    if ([string]::IsNullOrWhiteSpace($Name)) {
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

try {
    $resolvedBackupSourcePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BackupSourcePath)
    if (-not (Test-Path -LiteralPath $resolvedBackupSourcePath)) {
        throw "Backup source not found: $resolvedBackupSourcePath"
    }

    $resolvedDatabasePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DatabasePath)
    $databaseDirectory = Split-Path -Parent $resolvedDatabasePath
    if (-not (Test-Path -LiteralPath $databaseDirectory)) {
        New-Item -ItemType Directory -Path $databaseDirectory -Force | Out-Null
        Write-Step "Created database directory: $databaseDirectory"
    }

    if (-not $Force) {
        Write-Warning "Stop the ChairSide IIS app pool before restore. Restoring while the app is running can corrupt or lose WAL data."
        $confirmation = Read-Host "Type RESTORE to replace $resolvedDatabasePath"
        if ($confirmation -ne "RESTORE") {
            Write-Step "Restore canceled."
            exit 0
        }
    }

    $resolvedBackupDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BackupDirectory)
    $appPoolStopped = $false
    try {
        if (-not [string]::IsNullOrWhiteSpace($AppPoolName)) {
            Set-AppPoolState -Name $AppPoolName -State "Stopped"
            $appPoolStopped = $true
        }

        $safetyBackup = Backup-CurrentDatabase -CurrentDatabasePath $resolvedDatabasePath -DestinationRoot $resolvedBackupDirectory
        Write-Step "Pre-restore safety backup created: $safetyBackup"

        $backupItem = Get-Item -LiteralPath $resolvedBackupSourcePath
        if ($backupItem.PSIsContainer) {
            $sourceDatabase = Get-ChildItem -LiteralPath $resolvedBackupSourcePath -Filter "*.db" -File | Select-Object -First 1
            if (-not $sourceDatabase) {
                throw "Backup directory does not contain a .db file: $resolvedBackupSourcePath"
            }

            Copy-IfPresent -SourcePath $sourceDatabase.FullName -DestinationPath $resolvedDatabasePath
            Copy-IfPresent -SourcePath "$($sourceDatabase.FullName)-wal" -DestinationPath "$resolvedDatabasePath-wal"
            Copy-IfPresent -SourcePath "$($sourceDatabase.FullName)-shm" -DestinationPath "$resolvedDatabasePath-shm"

            if (-not (Test-Path -LiteralPath "$($sourceDatabase.FullName)-wal") -and (Test-Path -LiteralPath "$resolvedDatabasePath-wal")) {
                Remove-Item -LiteralPath "$resolvedDatabasePath-wal" -Force
                Write-Step "Removed existing WAL sidecar because backup source did not include one."
            }

            if (-not (Test-Path -LiteralPath "$($sourceDatabase.FullName)-shm") -and (Test-Path -LiteralPath "$resolvedDatabasePath-shm")) {
                Remove-Item -LiteralPath "$resolvedDatabasePath-shm" -Force
                Write-Step "Removed existing SHM sidecar because backup source did not include one."
            }
        }
        else {
            Copy-IfPresent -SourcePath $resolvedBackupSourcePath -DestinationPath $resolvedDatabasePath
            if (Test-Path -LiteralPath "$resolvedDatabasePath-wal") {
                Remove-Item -LiteralPath "$resolvedDatabasePath-wal" -Force
                Write-Step "Removed existing WAL sidecar after restoring single-file SQLite backup."
            }

            if (Test-Path -LiteralPath "$resolvedDatabasePath-shm") {
                Remove-Item -LiteralPath "$resolvedDatabasePath-shm" -Force
                Write-Step "Removed existing SHM sidecar after restoring single-file SQLite backup."
            }
        }

        Write-Step "Restore completed: $resolvedDatabasePath"
    }
    finally {
        if ($appPoolStopped) {
            Set-AppPoolState -Name $AppPoolName -State "Started"
        }
    }

    exit 0
}
catch {
    Write-Error "[ChairSide Restore] Failed: $($_.Exception.Message)"
    exit 1
}
