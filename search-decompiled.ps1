<#
.SYNOPSIS
    Searches Ghidra-decompiled Wreckfest sources for one or more patterns.

.DESCRIPTION
    The decompiled tree is ~42k single-function .c files (~60 MB), which is enough
    to time out naive recursive greps. This walks the tree in parallel and reports
    file, line number and matching text.

    Ghidra names each file after the function's absolute address, e.g.
    FUN_140f18b30_0x140F18B30.c. With an image base of 0x140000000 you can convert
    an RVA from the console hook straight to a filename:

        RVA 0x00F18B30  ->  FUN_140f18b30*        (command dispatcher)
        RVA 0x00F1A050  ->  FUN_140f1a050*        (console print)

    Use -Rva to look a function up that way instead of by pattern.

.EXAMPLE
    .\search-decompiled.ps1 -Pattern 'moderator','admin'

.EXAMPLE
    .\search-decompiled.ps1 -Pattern 'PRIVILEGES DROPPED' -Context 4

.EXAMPLE
    .\search-decompiled.ps1 -Rva 0x00F18B30
#>
[CmdletBinding(DefaultParameterSetName = 'Pattern')]
param(
    [Parameter(ParameterSetName = 'Pattern', Mandatory, Position = 0)]
    [string[]] $Pattern,

    [Parameter(ParameterSetName = 'Rva', Mandatory)]
    [long] $Rva,

    [string] $Root = 'F:\Ghidra\WF_Decompiled',

    [long] $ImageBase = 0x140000000,

    [int] $Context = 0,

    [switch] $CaseSensitive,

    [string] $OutFile,

    [int] $ThrottleLimit = 8
)

if (-not (Test-Path $Root)) {
    Write-Error "Decompiled root not found: $Root"
    return
}

# --- RVA lookup: no searching needed, the filename encodes the address ---------
if ($PSCmdlet.ParameterSetName -eq 'Rva') {
    $absolute = $ImageBase + $Rva
    $prefix = 'FUN_{0:x}' -f $absolute
    Write-Host "RVA 0x$('{0:X8}' -f $Rva) -> absolute 0x$('{0:X}' -f $absolute) -> $prefix*" -ForegroundColor Cyan

    $hits = Get-ChildItem $Root -Recurse -File -Filter "$prefix*" -ErrorAction SilentlyContinue
    if (-not $hits) {
        Write-Warning "No file matched $prefix* - check the image base (-ImageBase)."
        return
    }
    foreach ($h in $hits) { Write-Host $h.FullName -ForegroundColor Green }
    return
}

# --- pattern search -----------------------------------------------------------
$files = Get-ChildItem $Root -Recurse -File -Filter *.c -ErrorAction SilentlyContinue
Write-Host ("Searching {0:N0} files for: {1}" -f $files.Count, ($Pattern -join ', ')) -ForegroundColor Cyan

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$opts = if ($CaseSensitive) { [Text.RegularExpressions.RegexOptions]::None }
        else { [Text.RegularExpressions.RegexOptions]::IgnoreCase }

$results = $files.FullName | ForEach-Object -ThrottleLimit $ThrottleLimit -Parallel {
    $patterns = $using:Pattern
    $ctx      = $using:Context
    $opts     = $using:opts

    $lines = [System.IO.File]::ReadAllLines($_)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        foreach ($p in $patterns) {
            if ([Text.RegularExpressions.Regex]::IsMatch($lines[$i], $p, $opts)) {
                $from = [Math]::Max(0, $i - $ctx)
                $to   = [Math]::Min($lines.Length - 1, $i + $ctx)
                [pscustomobject]@{
                    File    = $_
                    Line    = $i + 1
                    Pattern = $p
                    Text    = $lines[$i].Trim()
                    Context = if ($ctx -gt 0) { ($lines[$from..$to] -join "`n") } else { $null }
                }
                break
            }
        }
    }
}

$sw.Stop()
$results = @($results)
Write-Host ("{0:N0} matches in {1:N1}s" -f $results.Count, $sw.Elapsed.TotalSeconds) -ForegroundColor Cyan

if ($results.Count -eq 0) { return }

# Group by file so a function with many hits reads as one block.
$grouped = $results | Group-Object File | Sort-Object { $_.Group.Count } -Descending

$render = foreach ($g in $grouped) {
    "=== {0}  ({1} match(es)) ===" -f (Split-Path $g.Name -Leaf), $g.Group.Count
    foreach ($m in $g.Group) {
        "  {0,6}: {1}" -f $m.Line, $m.Text
        if ($m.Context) { ($m.Context -split "`n" | ForEach-Object { "         | $_" }) }
    }
    ""
}

if ($OutFile) {
    $render | Set-Content -Path $OutFile -Encoding utf8
    Write-Host "Report written to $OutFile" -ForegroundColor Green
} else {
    $render
}
