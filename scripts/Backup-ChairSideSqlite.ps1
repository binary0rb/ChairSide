param(
    [string]$DatabasePath = "C:\ChairSide\Data\chairside.db",
    [string]$BackupDirectory = "C:\ChairSide\Backups",
    [string]$SqliteExe = "sqlite3",
    [switch]$AllowFileSetCopy
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "[ChairSide Backup] $Message"
}

function Copy-IfPresent {
    param(
        [string]$SourcePath,
        [string]$DestinationPath
    )

    if (Test-Path -LiteralPath $SourcePath) {
        Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -ErrorAction Stop
        Write-Step "Copied $(Split-Path -Leaf $SourcePath)."
    }
}

try {
    $resolvedDatabasePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DatabasePath)
    if (-not (Test-Path -LiteralPath $resolvedDatabasePath)) {
        throw "Database file not found: $resolvedDatabasePath"
    }

    if (-not (Test-Path -LiteralPath $BackupDirectory)) {
        New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
        Write-Step "Created backup directory: $BackupDirectory"
    }

    $resolvedBackupDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BackupDirectory)
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $databaseName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedDatabasePath)
    $sqliteBackupPath = Join-Path $resolvedBackupDirectory "$databaseName-$timestamp.db"

    if (Test-Path -LiteralPath $sqliteBackupPath) {
        throw "Backup target already exists: $sqliteBackupPath"
    }

    $sqliteCommand = Get-Command $SqliteExe -ErrorAction SilentlyContinue
    if ($sqliteCommand) {
        Write-Step "Using sqlite3 .backup for a consistent SQLite backup."
        $backupCommand = ".timeout 10000`n.backup '$sqliteBackupPath'`n"
        $backupCommand | & $sqliteCommand.Source $resolvedDatabasePath
        if ($LASTEXITCODE -ne 0) {
            throw "sqlite3 backup failed with exit code $LASTEXITCODE."
        }

        Write-Step "Backup created: $sqliteBackupPath"
        Write-Step "WAL contents are included through SQLite backup semantics; sidecar files do not need separate restore."
        exit 0
    }

    if (-not $AllowFileSetCopy) {
        throw "sqlite3 was not found. Install sqlite3.exe and use SQLite .backup for online backups. File-set copy of .db/.db-wal/.db-shm requires the ChairSide IIS app pool to be stopped and must be requested explicitly with -AllowFileSetCopy."
    }

    Write-Warning "sqlite3 was not found and -AllowFileSetCopy was provided. Falling back to file-set copy."
    Write-Warning "Use file-set copy only after stopping the ChairSide IIS app pool so WAL sidecar files are stable."

    $backupSetDirectory = Join-Path $resolvedBackupDirectory "$databaseName-$timestamp-file-set"
    if (Test-Path -LiteralPath $backupSetDirectory) {
        throw "Backup target already exists: $backupSetDirectory"
    }

    New-Item -ItemType Directory -Path $backupSetDirectory | Out-Null
    Copy-IfPresent -SourcePath $resolvedDatabasePath -DestinationPath (Join-Path $backupSetDirectory ([System.IO.Path]::GetFileName($resolvedDatabasePath)))
    Copy-IfPresent -SourcePath "$resolvedDatabasePath-wal" -DestinationPath (Join-Path $backupSetDirectory ([System.IO.Path]::GetFileName("$resolvedDatabasePath-wal")))
    Copy-IfPresent -SourcePath "$resolvedDatabasePath-shm" -DestinationPath (Join-Path $backupSetDirectory ([System.IO.Path]::GetFileName("$resolvedDatabasePath-shm")))

    Write-Step "File-set backup created: $backupSetDirectory"
    exit 0
}
catch {
    Write-Error "[ChairSide Backup] Failed: $($_.Exception.Message)"
    exit 1
}
