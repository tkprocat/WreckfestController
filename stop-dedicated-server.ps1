<#
.SYNOPSIS
    Stops only the Wreckfest dedicated server, never a game client.

.DESCRIPTION
    The dedicated server and the game client are both Wreckfest_x64.exe, so
    matching on process name alone kills whichever you were playing on. They are
    told apart by their command line:

        server : ...\Wreckfest Dedicated Server\Wreckfest_x64.exe -s server_config=...
        client : ...\SteamLibrary\...\Wreckfest\Wreckfest_x64.exe -setup

    Prefers the controller's API, which only ever kills the PID it is tracking.
    Falls back to the command-line match when the controller is not running.

.EXAMPLE
    .\stop-dedicated-server.ps1

.EXAMPLE
    .\stop-dedicated-server.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $ControllerUrl = 'http://localhost:5100',
    [switch] $SkipApi
)

function Get-DedicatedServerProcesses {
    Get-CimInstance Win32_Process -Filter "Name='Wreckfest_x64.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '-s\s+server_config=' } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } |
        Where-Object { $_ }
}

$targets = @(Get-DedicatedServerProcesses)
if (-not $targets) { Write-Host "No dedicated server running."; return }

foreach ($t in $targets) {
    Write-Host ("dedicated server: PID {0}  '{1}'" -f $t.Id, $t.MainWindowTitle)
}

# The API stops only the tracked PID, so try it first.
if (-not $SkipApi) {
    try {
        $null = Invoke-WebRequest "$ControllerUrl/api/server/status" -TimeoutSec 3 -UseBasicParsing
        if ($PSCmdlet.ShouldProcess("dedicated server", "stop via controller API")) {
            $r = Invoke-WebRequest "$ControllerUrl/api/server/stop" -Method POST -TimeoutSec 20 -UseBasicParsing
            Write-Host "stopped via API: $($r.Content)"
            Start-Sleep -Seconds 2
            if (-not (Get-DedicatedServerProcesses)) { return }
            Write-Host "still running after API stop; falling back to process kill."
        }
    }
    catch { Write-Host "controller not reachable; using process kill." }
}

foreach ($t in @(Get-DedicatedServerProcesses)) {
    if ($PSCmdlet.ShouldProcess("PID $($t.Id)", "Stop-Process")) {
        Stop-Process -Id $t.Id -Force
        Write-Host "killed PID $($t.Id)"
    }
}

$clients = Get-CimInstance Win32_Process -Filter "Name='Wreckfest_x64.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -notmatch '-s\s+server_config=' }
if ($clients) {
    Write-Host ("left {0} game client(s) running." -f @($clients).Count)
}
