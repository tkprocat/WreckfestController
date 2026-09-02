<#
.SYNOPSIS
    Minimal client for the MCPx64dbg HTTP plugin.

.DESCRIPTION
    IMPORTANT: use the ENDPOINT PATH, not the Python wrapper's function name.
    They differ for most calls - MemoryRead is 'Memory/Read', IsDebugging is
    'Is_Debugging', MiscParseExpression is 'Misc/ParseExpression'. Calling a
    non-existent path returns a connection reset or a 500, which looks like the
    plugin being broken when it is really a 404 in disguise. Run with
    -ListEndpoints to see the mapping.

    The plugin can also wedge and stop answering entirely; restarting x64dbg
    clears it. Connection: close is used here as a precaution, not because
    keep-alive is known to break it.

    Ghidra addresses need rebasing to the live image, because ASLR moves it:

        RVA  = ghidraAddress - 0x140000000
        live = moduleBase    + RVA

    Use -Rva to have that done for you.

.EXAMPLE
    .\x64dbg-api.ps1 -Endpoint IsDebugActive

.EXAMPLE
    .\x64dbg-api.ps1 -Endpoint MemoryRead -Params @{ addr='0x7ff6b4880000'; size='16' }

.EXAMPLE
    # resolve a Ghidra address to its live counterpart
    .\x64dbg-api.ps1 -Rva 0x004477A0
#>
[CmdletBinding(DefaultParameterSetName = 'Call')]
param(
    [Parameter(ParameterSetName = 'Call', Mandatory, Position = 0)]
    [string] $Endpoint,

    [Parameter(ParameterSetName = 'Call')]
    [hashtable] $Params,

    [Parameter(ParameterSetName = 'Rva', Mandatory)]
    [long] $Rva,

    [Parameter(ParameterSetName = 'List', Mandatory)]
    [switch] $ListEndpoints,

    [string] $BaseUrl = 'http://127.0.0.1:8888',

    [int] $TimeoutSeconds = 15
)

Add-Type -AssemblyName System.Net.Http

function Invoke-X64Dbg {
    param([string] $Ep, [hashtable] $P, [string] $Base, [int] $Timeout)

    $url = "$Base/$Ep"
    if ($P -and $P.Count) {
        $qs = ($P.GetEnumerator() | ForEach-Object {
            "$($_.Key)=$([uri]::EscapeDataString([string]$_.Value))"
        }) -join '&'
        $url += "?$qs"
    }

    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds($Timeout)
    # Critical: the plugin wedges on a reused connection.
    $client.DefaultRequestHeaders.ConnectionClose = $true
    try {
        return $client.GetStringAsync($url).GetAwaiter().GetResult()
    }
    catch {
        $inner = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        return "ERR: $inner"
    }
    finally { $client.Dispose() }
}

function Get-ModuleBase {
    param([string] $Base, [int] $Timeout)
    $json = Invoke-X64Dbg -Ep 'GetModuleList' -P $null -Base $Base -Timeout $Timeout
    if ($json -like 'ERR:*') { return $null }
    # NOTE: the plugin emits invalid JSON - Windows paths are embedded unescaped,
    # so "path":"C:\Program Files..." contains \P, \S etc. and ConvertFrom-Json
    # throws. Pull the base out with a regex instead of parsing.
    $m = [regex]::Match($json, '"name"\s*:\s*"[^"]*wreckfest[^"]*"\s*,\s*"base"\s*:\s*"0x([0-9a-fA-F]+)"')
    if (-not $m.Success) {
        $m = [regex]::Match($json, '"base"\s*:\s*"0x([0-9a-fA-F]+)"')
    }
    if (-not $m.Success) { return $null }
    try { return [Convert]::ToUInt64($m.Groups[1].Value, 16) } catch { return $null }
}

if ($PSCmdlet.ParameterSetName -eq 'List') {
    # Endpoint paths as used by the plugin, recovered from x64dbg.py.
    @'
IsDebugActive              IsDebugActive
IsDebugging                Is_Debugging
GetModuleList              GetModuleList
GetCallStack               GetCallStack
GetThreadList              GetThreadList
GetRegisterDump            RegisterDump
GetMemoryMap               MemoryMap
RegisterGet/Set            Register/Get , Register/Set
MemoryRead/Write           Memory/Read , Memory/Write
MemoryIsValidPtr           Memory/IsValidPtr
StringGetAt                String/GetAt
DisasmGetInstructionRange  Disasm/GetInstructionRange
DebugRun/Pause/Stop        Debug/Run , Debug/Pause , Debug/Stop
DebugStepIn/Over/Out       Debug/StepIn , Debug/StepOver , Debug/StepOut
DebugSetBreakpoint         Debug/SetBreakpoint
DebugDeleteBreakpoint      Debug/DeleteBreakpoint
SetHardwareBreakpoint      Debug/SetHardwareBreakpoint
GetBreakpointList          Breakpoint/List
PatternFindMem             Pattern/FindMem
XrefGet / XrefCount        Xref/Get , Xref/Count
QuerySymbols               SymbolEnum
ExecCommand                ExecCommand
'@
    return
}

if ($PSCmdlet.ParameterSetName -eq 'Rva') {
    $base = Get-ModuleBase -Base $BaseUrl -Timeout $TimeoutSeconds
    if (-not $base) { Write-Error "Could not read module base - is x64dbg attached?"; return }
    $live = $base + $Rva
    [pscustomobject]@{
        Rva           = '0x{0:X8}' -f $Rva
        GhidraAddress = '0x{0:X}'  -f (0x140000000 + $Rva)
        ModuleBase    = '0x{0:X}'  -f $base
        LiveAddress   = '0x{0:X}'  -f $live
    }
    return
}

Invoke-X64Dbg -Ep $Endpoint -P $Params -Base $BaseUrl -Timeout $TimeoutSeconds
