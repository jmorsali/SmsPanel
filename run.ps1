[CmdletBinding()]
param(
    [int]$BackendPort = 5080,
    [int]$FrontendPort = 5173,
    [int]$StartupTimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$runtimeDirectory = Join-Path $projectRoot '.run'
$backendDirectory = Join-Path $projectRoot 'SmsPanel.Api'
$frontendDirectory = Join-Path $projectRoot 'SmsPanel.Web'

New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null

function Stop-ProjectProcess {
    param([Parameter(Mandatory)][string]$Name)

    $pidFile = Join-Path $runtimeDirectory "$Name.pid"
    if (-not (Test-Path -LiteralPath $pidFile)) { return }

    $savedPid = (Get-Content -LiteralPath $pidFile -Raw).Trim()
    if ($savedPid -notmatch '^\d+$') {
        Remove-Item -LiteralPath $pidFile -Force
        return
    }

    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $savedPid" -ErrorAction SilentlyContinue
    $isProjectProcess = $null -ne $process -and (
        $process.CommandLine -like "*$projectRoot*" -or
        $process.CommandLine -like '*SmsPanel.Api.csproj*' -or
        $process.CommandLine -like '*npm*run*dev*'
    )
    if ($isProjectProcess) {
        $descendants = [System.Collections.Generic.List[int]]::new()
        $queue = [System.Collections.Generic.Queue[int]]::new()
        $queue.Enqueue([int]$savedPid)
        while ($queue.Count -gt 0) {
            $parentId = $queue.Dequeue()
            Get-CimInstance Win32_Process -Filter "ParentProcessId = $parentId" -ErrorAction SilentlyContinue | ForEach-Object {
                $descendants.Add([int]$_.ProcessId)
                $queue.Enqueue([int]$_.ProcessId)
            }
        }
        $childIds = $descendants.ToArray()
        [array]::Reverse($childIds)
        foreach ($childId in $childIds) { Stop-Process -Id $childId -Force -ErrorAction SilentlyContinue }
        Stop-Process -Id ([int]$savedPid) -Force -ErrorAction SilentlyContinue
        Wait-Process -Id ([int]$savedPid) -Timeout 10 -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
}

function Stop-ProjectPortOwner {
    param([Parameter(Mandatory)][int]$Port)

    $connections = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
    foreach ($connection in $connections) {
        if ($connection.OwningProcess -eq 0) { continue }
        $process = Get-CimInstance Win32_Process -Filter "ProcessId = $($connection.OwningProcess)" -ErrorAction SilentlyContinue
        if ($null -ne $process -and $process.CommandLine -like "*$projectRoot*") {
            Stop-Process -Id $connection.OwningProcess -Force
        } else {
            throw "Port $Port is occupied by a process that does not belong to this project."
        }
    }
}

function Wait-HttpEndpoint {
    param([Parameter(Mandatory)][string]$Uri, [Parameter(Mandatory)][string]$Name)

    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) { return }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    } while ((Get-Date) -lt $deadline)

    throw "$Name did not become ready at $Uri within $StartupTimeoutSeconds seconds."
}

Stop-ProjectProcess -Name 'backend'
Stop-ProjectProcess -Name 'frontend'
Stop-ProjectPortOwner -Port $BackendPort
Stop-ProjectPortOwner -Port $FrontendPort

$backend = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', 'SmsPanel.Api.csproj', '--launch-profile', 'http') `
    -WorkingDirectory $backendDirectory -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $runtimeDirectory 'backend.log') `
    -RedirectStandardError (Join-Path $runtimeDirectory 'backend.error.log')
$backend.Id | Set-Content -LiteralPath (Join-Path $runtimeDirectory 'backend.pid')

$frontend = Start-Process -FilePath 'npm.cmd' `
    -ArgumentList @('run', 'dev', '--', '--host', '127.0.0.1', '--port', $FrontendPort, '--strictPort') `
    -WorkingDirectory $frontendDirectory -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $runtimeDirectory 'frontend.log') `
    -RedirectStandardError (Join-Path $runtimeDirectory 'frontend.error.log')
$frontend.Id | Set-Content -LiteralPath (Join-Path $runtimeDirectory 'frontend.pid')

try {
    Wait-HttpEndpoint -Uri "http://127.0.0.1:$BackendPort/health" -Name 'Backend'
    Wait-HttpEndpoint -Uri "http://127.0.0.1:$FrontendPort" -Name 'Frontend'
} catch {
    Stop-ProjectProcess -Name 'backend'
    Stop-ProjectProcess -Name 'frontend'
    throw
}

Write-Host "Backend: http://127.0.0.1:$BackendPort"
Write-Host "Frontend: http://127.0.0.1:$FrontendPort"
Write-Host "Logs: $runtimeDirectory"
