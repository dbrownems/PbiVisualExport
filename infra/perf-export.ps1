<#
.SYNOPSIS
Perf test for large exports: POST /export/csv against a read-only pass over the same XMLA
query (POST /export/bench), sampling the server process's memory and CPU throughout.

.DESCRIPTION
Starts the Release build of the web app on -Port with Export:EnableBenchmark=true (builds
it first unless -NoBuild), warms it up, then for each repeat x row count x format runs:

  read    POST /export/bench  executes the export's DAX over XMLA, reads every value, writes nothing
  export  POST /export/csv    the real export, streamed to a file by curl

The pair is interleaved so capacity/network drift affects both equally. A row count N is
applied as an advanced filter <RowIdTable>[<RowIdColumn>] <= N (Orders[Row Id] in the
LargeExportTest model); 0 = no filter. The server process is sampled every -SampleMs:
working set, private bytes and CPU time; per-run timelines go to <OutDir>\samples-*.csv and
the summary to <OutDir>\summary.csv. The server is stopped at the end.

Uses the XMLA device-code sign-in the app has cached; sign in once through the UI first.

.EXAMPLE
pwsh infra/perf-export.ps1 -Request detail1-request.json

.EXAMPLE
pwsh infra/perf-export.ps1 -Request detail1-request.json -Rows 250000,1000000,0 -Formats formatted -Modes export
#>
[CmdletBinding()]
param(
    # A VisualExportRequest JSON, e.g. copied from the debug panel.
    [Parameter(Mandatory)] [string] $Request,
    # Lists also accept one comma-separated string (pwsh -File passes "a,b" unsplit).
    [string[]] $Rows = @('0'),
    [string[]] $Formats = @('formatted', 'raw'),
    [string[]] $Modes = @('read', 'export'),
    # Apply the request's sort (ORDER BY). Off by default, as recommended for huge exports.
    [switch] $Sort,
    [int] $Repeat = 1,
    [int] $Port = 5098,
    [int] $SampleMs = 500,
    [string] $RowIdTable = 'Orders',
    [string] $RowIdColumn = 'Row Id',
    [string] $OutDir = (Join-Path ([IO.Path]::GetTempPath()) 'pbiexport-perf'),
    [switch] $NoBuild,
    # Keep the exported CSV files (each full detail1 export is ~630 MB).
    [switch] $KeepCsv
)
$ErrorActionPreference = 'Stop'
function Split-List([string[]] $v) { @($v | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$Rows = @(Split-List $Rows | ForEach-Object { [long]$_ })
$Formats = Split-List $Formats
$Modes = Split-List $Modes
if ($Formats | Where-Object { $_ -notin 'formatted', 'raw' }) { throw "-Formats: use formatted and/or raw." }
if ($Modes | Where-Object { $_ -notin 'read', 'export' }) { throw "-Modes: use read and/or export." }
$proj = (Resolve-Path (Join-Path $PSScriptRoot '..\src\PbiVisualExport.Web')).Path
$exe = Join-Path $proj 'bin\Release\net10.0\PbiVisualExport.Web.exe'
$base = "http://localhost:$Port"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$serverLog = Join-Path $OutDir 'server.log'

if (-not $NoBuild) {
    dotnet build $proj -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE) { throw 'Release build failed.' }
}

$baseRequest = Get-Content $Request -Raw | ConvertFrom-Json -AsHashtable

function New-RunRequest([long] $n, [string] $format) {
    $r = @{} + $baseRequest
    $r.applyFormatStrings = $format -eq 'formatted'
    $r.applySort = [bool]$Sort
    $filters = [System.Collections.Generic.List[object]]::new()
    if ($baseRequest.filters) { foreach ($f in $baseRequest.filters) { $filters.Add($f) } }
    if ($n -gt 0) {
        $filters.Add(@{
                '$schema'       = 'http://powerbi.com/product/schema#advanced'
                target          = @{ table = $RowIdTable; column = $RowIdColumn }
                filterType      = 0
                logicalOperator = 'And'
                conditions      = @(@{ operator = 'LessThanOrEqual'; value = $n })
            })
    }
    $r.filters = $filters.ToArray()
    $path = Join-Path $OutDir "request-$n-$format.json"
    $r | ConvertTo-Json -Depth 32 | Set-Content $path -Encoding utf8NoBOM
    $path
}

function Get-NewLog([int] $fromLine) {
    $all = @(Get-Content $serverLog)
    if ($all.Count -le $fromLine) { return '' }
    ($all[$fromLine..($all.Count - 1)]) -join "`n"
}

# One sampled request. Returns a summary row; the timeline is written to samples-<label>.csv.
function Invoke-Run([string] $mode, [string] $requestPath, [string] $label, [switch] $Quiet) {
    $isExport = $mode -eq 'export'
    $url = if ($isExport) { "$base/export/csv" } else { "$base/export/bench" }
    $body = Join-Path $OutDir ("out-$label." + $(if ($isExport) { 'csv' } else { 'json' }))
    $curlOut = Join-Path $OutDir "curl-$label.txt"
    Remove-Item $body, $curlOut -ErrorAction SilentlyContinue
    $curlArgs = "-s -S -o `"$body`" -w `"%{http_code} %{time_starttransfer} %{time_total} %{size_download}`" " +
    "-H `"Content-Type: application/json`" --data-binary `"@$requestPath`" $url"

    $script:server.Refresh()
    $cpu0 = $script:server.TotalProcessorTime
    $ws0 = $script:server.WorkingSet64
    $pb0 = $script:server.PrivateMemorySize64
    $logFrom = @(Get-Content $serverLog).Count
    $samples = [System.Collections.Generic.List[object]]::new()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $curl = Start-Process curl.exe -ArgumentList $curlArgs -PassThru -NoNewWindow -RedirectStandardOutput $curlOut
    $null = $curl.Handle   # keeps ExitCode available after exit
    do {
        $script:server.Refresh()
        $samples.Add([pscustomobject]@{
                Seconds   = [math]::Round($sw.Elapsed.TotalSeconds, 2)
                WorkingMB = [math]::Round($script:server.WorkingSet64 / 1MB)
                PrivateMB = [math]::Round($script:server.PrivateMemorySize64 / 1MB)
                CpuSec    = [math]::Round(($script:server.TotalProcessorTime - $cpu0).TotalSeconds, 1)
                OutputMB  = if (Test-Path $body) { [math]::Round((Get-Item $body).Length / 1MB, 1) } else { 0 }
            })
        if ($curl.HasExited) { break }
        Start-Sleep -Milliseconds $SampleMs
    } while ($true)
    $curl.WaitForExit()
    $samples | Export-Csv (Join-Path $OutDir "samples-$label.csv") -NoTypeInformation

    $code, $ttfb, $total, $bytes = ((Get-Content $curlOut -Raw).Trim() -split ' ')
    if ($code -ne '200') {
        $err = if (Test-Path $body) { Get-Content $body -Raw } else { '' }
        throw "$mode $label failed: HTTP $code $err"
    }

    $rows = $null; $allocMB = $null; $gen2 = $null
    $firstByte = [double]$ttfb
    if ($isExport) {
        # Row count and GC deltas come from the server log (Debug level).
        $deadline = (Get-Date).AddSeconds(10)
        do {
            $text = Get-NewLog $logFrom
            if ($text -match 'Export GC') { break }
            Start-Sleep -Milliseconds 250
        } while ((Get-Date) -lt $deadline)
        if ($text -match 'Export complete: ([\d,]+) rows') { $rows = [long]($Matches[1] -replace ',', '') }
        if ($text -match 'allocated ([\d,]+) MB, collections gen0 \d+ / gen1 \d+ / gen2 (\d+)') {
            $allocMB = [long]($Matches[1] -replace ',', ''); $gen2 = [int]$Matches[2]
        }
        if (-not $KeepCsv) { Remove-Item $body -ErrorAction SilentlyContinue }
    }
    else {
        $b = Get-Content $body -Raw | ConvertFrom-Json
        $rows = $b.rows; $allocMB = [math]::Round($b.allocatedBytes / 1MB); $gen2 = $b.gen2Collections
        # The bench replies once at the end; report when its first row arrived instead.
        $firstByte = $b.firstDataSeconds
    }

    $wall = [double]$total
    $cpu = ($samples[-1]).CpuSec
    $row = [pscustomobject]@{
        Mode        = $mode
        Label       = $label
        Rows        = $rows
        WallSec     = [math]::Round($wall, 1)
        FirstByte   = [math]::Round($firstByte, 1)
        OutMB       = if ($isExport) { [math]::Round([double]$bytes / 1MB) } else { 0 }
        RowsPerSec  = if ($rows) { [math]::Round($rows / $wall) } else { $null }
        CpuSec      = $cpu
        AvgCores    = [math]::Round($cpu / [math]::Max($wall, 0.001), 2)
        WsStartMB   = [math]::Round($ws0 / 1MB)
        WsPeakMB    = ($samples | Measure-Object WorkingMB -Maximum).Maximum
        PrivStartMB = [math]::Round($pb0 / 1MB)
        PrivPeakMB  = ($samples | Measure-Object PrivateMB -Maximum).Maximum
        AllocMB     = $allocMB
        Gen2        = $gen2
    }
    if (-not $Quiet) { $row | Format-Table Mode, Label, Rows, WallSec, FirstByte, OutMB, CpuSec, AvgCores, WsPeakMB, PrivPeakMB, AllocMB, Gen2 -AutoSize | Out-String -Width 300 | Write-Host }
    $row
}

$savedEnv = @{
    Export__EnableBenchmark              = $env:Export__EnableBenchmark
    Logging__LogLevel__PbiVisualExport   = $env:Logging__LogLevel__PbiVisualExport
}
$script:server = $null
try {
    $env:Export__EnableBenchmark = 'true'
    $env:Logging__LogLevel__PbiVisualExport = 'Debug'
    $script:server = Start-Process $exe -ArgumentList "--urls $base" -WorkingDirectory $proj -PassThru -NoNewWindow `
        -RedirectStandardOutput $serverLog -RedirectStandardError (Join-Path $OutDir 'server.err.log')
    $null = $script:server.Handle
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        try { Invoke-WebRequest "$base/" -UseBasicParsing -TimeoutSec 5 | Out-Null; break }
        catch {
            if ($script:server.HasExited) { throw "Server exited; see $serverLog" }
            if ((Get-Date) -gt $deadline) { throw 'Server did not start within 60 s.' }
            Start-Sleep 1
        }
    }
    Write-Host "Server PID $($script:server.Id) on $base (Release, logs in $OutDir)"

    # Warm-up: token, cached metadata, JIT of both code paths.
    $warm = New-RunRequest 1000 'formatted'
    foreach ($m in $Modes) { $null = Invoke-Run $m $warm "warmup-$m" -Quiet }

    $results = [System.Collections.Generic.List[object]]::new()
    for ($i = 1; $i -le $Repeat; $i++) {
        foreach ($n in $Rows) {
            foreach ($format in $Formats) {
                $req = New-RunRequest $n $format
                foreach ($m in $Modes) {
                    $label = "$m-$(if ($n -gt 0) { $n } else { 'all' })-$format-$i"
                    $results.Add((Invoke-Run $m $req $label))
                }
            }
        }
    }

    $results | Export-Csv (Join-Path $OutDir 'summary.csv') -NoTypeInformation
    Write-Host "`n=== Summary ($OutDir\summary.csv) ==="
    $results | Format-Table Label, Rows, WallSec, FirstByte, OutMB, RowsPerSec, CpuSec, AvgCores, WsStartMB, WsPeakMB, PrivStartMB, PrivPeakMB, AllocMB, Gen2 -AutoSize | Out-String -Width 300 | Write-Host

    if ($Modes.Count -eq 2) {
        Write-Host '=== Export vs read-only (mean wall time) ==='
        $results | Group-Object { $_.Label -replace '^(read|export)-', '' -replace '-\d+$', '' } |
        Sort-Object { [long]$_.Group[0].Rows }, Name | ForEach-Object {
            $r = ($_.Group | Where-Object Mode -EQ 'read' | Measure-Object WallSec -Average).Average
            $e = ($_.Group | Where-Object Mode -EQ 'export' | Measure-Object WallSec -Average).Average
            [pscustomobject]@{ Case = $_.Name; ReadSec = [math]::Round($r, 1); ExportSec = [math]::Round($e, 1); Overhead = '{0:+0.0;-0.0}%' -f (100 * ($e - $r) / $r) }
        } | Format-Table -AutoSize | Out-String -Width 300 | Write-Host
    }
}
finally {
    if ($script:server -and -not $script:server.HasExited) { Stop-Process -Id $script:server.Id }
    foreach ($k in $savedEnv.Keys) { Set-Item "env:$k" -Value $savedEnv[$k] -ErrorAction SilentlyContinue; if ($null -eq $savedEnv[$k]) { Remove-Item "env:$k" -ErrorAction SilentlyContinue } }
}
