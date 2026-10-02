<#
.SYNOPSIS
  Smoke-tests a running sample host: exports StoreSales Page1 'table1' through the
  module's endpoints and checks the CSV.
.DESCRIPTION
  Calls POST {BaseUrl}{Prefix}/prepare, then POST {BaseUrl}{Prefix}/csv, with the same
  request the JS client builds for StoreSales Page1 'table1' (Chain, Store Type,
  TotalSales, TotalUnits; no filters). Checks the CSV has 4 columns and -ExpectedRows
  data rows, and prints it.

  The first export of a dev host (XMLA in device-code mode) answers 503 with a sign-in
  code; the script prints it, waits until {Prefix}/xmla-auth reports signed in, then
  retries.

  Start the sample first (for the same workspace):
    dotnet run --project samples\PbiVisualExport.SampleHost
  -Workspace defaults to the PowerBI:WorkspaceId setting (see infra/settings.ps1). The
  StoreSales report ID is looked up by name with an Azure CLI token ('az login').
.EXAMPLE
  .\infra\smoke-export.ps1
.EXAMPLE
  .\infra\smoke-export.ps1 -Workspace <workspace-guid> -BaseUrl http://localhost:5099
#>
param(
  [string]$Workspace,
  [string]$ReportId,
  [string]$BaseUrl = 'http://localhost:5099',
  [string]$Prefix = '/pbi-export',
  [int]$ExpectedRows = 4,
  [int]$SignInTimeoutMinutes = 15
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\settings.ps1"
if(-not $Workspace){ $Workspace = Get-PbiSetting 'PowerBI:WorkspaceId' -Required }
$api = $BaseUrl.TrimEnd('/') + $Prefix

if(-not $ReportId){
  $token = az account get-access-token --resource 'https://analysis.windows.net/powerbi/api' --query accessToken -o tsv
  if($LASTEXITCODE -ne 0 -or -not $token){ throw "Could not get a Power BI token from Azure CLI. Run 'az login' first." }
  $reports = (Invoke-RestMethod -Uri "https://api.powerbi.com/v1.0/myorg/groups/$Workspace/reports" -Headers @{ Authorization = "Bearer $token" }).value
  $report = @($reports | Where-Object { $_.name -eq 'StoreSales' })
  if($report.Count -ne 1){ throw "Found $($report.Count) reports named 'StoreSales' in workspace $Workspace. Deploy the samples (infra\deploy-all.ps1) or pass -ReportId." }
  $ReportId = $report[0].id
}

$request = [ordered]@{
  workspaceId = $Workspace
  reportId = $ReportId
  pageName = 'Page1'
  visualName = 'table1'
  visualTitle = 'table1'
  fields = @(
    @{ table = 'Store'; column = 'Chain'; role = 'Values' },
    @{ table = 'Store'; column = 'Store Type'; role = 'Values' },
    @{ table = 'Sales'; measure = 'TotalSales'; role = 'Values' },
    @{ table = 'Sales'; measure = 'TotalUnits'; role = 'Values' }
  )
  filters = @()
  sort = @()
  applySort = $false
  applyFormatStrings = $true
}
$body = $request | ConvertTo-Json -Depth 5

# POSTs JSON; returns @{ Status; Json } for any status code (PowerShell 5.1 and 7).
function Invoke-Post([string]$Path, [string]$OutFile){
  $p = @{ Method = 'Post'; Uri = "$api/$Path"; Body = [Text.Encoding]::UTF8.GetBytes($body); ContentType = 'application/json'; UseBasicParsing = $true }
  if($OutFile){ $p.OutFile = $OutFile }
  try {
    $r = Invoke-WebRequest @p
    $json = $null
    if(-not $OutFile){ $json = $r.Content | ConvertFrom-Json }
    return @{ Status = 200; Json = $json }
  } catch {
    $resp = $_.Exception.Response
    if(-not $resp){ throw "Can't reach $api ($($_.Exception.Message)). Start the sample: dotnet run --project samples\PbiVisualExport.SampleHost" }
    $text = $_.ErrorDetails.Message
    $json = $null
    if($text){ try { $json = $text | ConvertFrom-Json } catch { } }
    return @{ Status = [int]$resp.StatusCode; Json = $json; Text = $text }
  }
}

function Wait-SignIn($challenge){
  Write-Host ''
  Write-Host "XMLA sign-in required: open $($challenge.verificationUri) and enter code $($challenge.userCode)" -ForegroundColor Yellow
  $deadline = (Get-Date).AddMinutes($SignInTimeoutMinutes)
  while((Get-Date) -lt $deadline){
    Start-Sleep -Seconds 5
    $s = Invoke-RestMethod -Uri "$api/xmla-auth" -UseBasicParsing
    if($s.signedIn){ Write-Host 'Signed in.' -ForegroundColor Green; return }
  }
  throw "Not signed in after $SignInTimeoutMinutes minutes."
}

Write-Host "Workspace $Workspace, report $ReportId (StoreSales), host $api"

$prep = Invoke-Post 'prepare'
if($prep.Status -eq 503 -and $prep.Json.signInRequired){
  Wait-SignIn $prep.Json
  $prep = Invoke-Post 'prepare'
}
if($prep.Status -ne 200){ throw "prepare failed: HTTP $($prep.Status) $($prep.Text)" }
$plan = $prep.Json
Write-Host "prepare: report '$($plan.reportName)', model '$($plan.datasetName)', $($plan.columns) columns"
foreach($w in @($plan.warnings)){ if($w){ Write-Host "  warning: $w" -ForegroundColor Yellow } }

$file = Join-Path ([IO.Path]::GetTempPath()) 'pbivisualexport-smoke.csv'
$csv = Invoke-Post 'csv' $file
if($csv.Status -ne 200){ throw "csv failed: HTTP $($csv.Status) $($csv.Text)" }

$rows = @(Import-Csv -Path $file -Encoding UTF8)
$header = (Get-Content -Path $file -TotalCount 1 -Encoding UTF8).TrimStart([char]0xFEFF)
$columnCount = @($rows[0].PSObject.Properties).Count
$rows | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "CSV: $file"

$problems = @()
if($columnCount -ne 4){ $problems += "expected 4 columns, got $columnCount ($header)" }
if($ExpectedRows -gt 0 -and $rows.Count -ne $ExpectedRows){ $problems += "expected $ExpectedRows rows, got $($rows.Count)" }
if($rows.Count -eq 0){ $problems += 'no data rows' }
if($problems){
  Write-Host "SMOKE TEST FAILED: $($problems -join '; ')" -ForegroundColor Red
  exit 1
}
Write-Host "SMOKE TEST PASSED: $($rows.Count) rows x $columnCount columns." -ForegroundColor Green
