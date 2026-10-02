<#
.SYNOPSIS
  Deploys every sample item to a workspace: the Warehouse (created and seeded), the
  semantic models, the reports, then refreshes the models.
.DESCRIPTION
  Idempotent: items are matched by display name (the .platform displayName) and updated
  in place when they exist (their IDs, and so embed URLs and bookmarks, are kept), or
  created when they don't.

  Steps:
    1. Warehouse ExportTestWH: created if missing, then seeded with tools/WhSql/seed.sql
       (drops and recreates its tables). -SkipSeed skips the seeding.
    2. StoreSales: its model is a PBIX import (infra/samples/StoreSales.pbix, data
       included; the source is not reachable, so it is never refreshed). Imported only
       when the StoreSales model is missing; -SkipStoreSalesImport skips it.
    3. Semantic models (infra/models/*), then reports (infra/reports/*), via
       deploy-model.ps1, which resolves the {{SemanticModelId:..}} and
       {{WarehouseServer:..}} tokens against this workspace.
    4. Refreshes FieldParamTest, DqParamReport and LargeExportTest (slow: a 2M-row
       calculated table). -SkipRefresh skips it.

  -Workspace defaults to the PowerBI:WorkspaceId setting (see infra/settings.ps1). The
  workspace needs a Fabric/Premium capacity. Tokens come from Azure CLI ('az login').
  -WhatIf lists what would be created/updated and resolves the deploy tokens of items
  whose dependencies already exist, without changing anything.

  A refresh that fails on credentials (DqParamReport reads the Warehouse over DirectQuery)
  needs the data source credentials set once in the Power BI service: semantic model
  Settings > Data source credentials (OAuth2). Then rerun with -SkipSeed.
.EXAMPLE
  .\infra\deploy-all.ps1 -WhatIf
.EXAMPLE
  .\infra\deploy-all.ps1 -Workspace <workspace-guid>
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [string]$Workspace,
  [switch]$SkipSeed,
  [switch]$SkipStoreSalesImport,
  [switch]$SkipRefresh
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\settings.ps1"
if(-not $Workspace){ $Workspace = Get-PbiSetting 'PowerBI:WorkspaceId' -Required }
$whatIf = [bool]$WhatIfPreference
$root = $script:PbiRepoRoot

$WarehouseName = 'ExportTestWH'   # also hard-coded as WhDatabase in models/DqParamTest/definition/expressions.tmdl
$ModelFolders  = 'DqParamTest', 'FieldParamTest', 'LargeExportTest'
$ReportFolders = 'DqParamReport', 'FieldParamReport', 'LargeExportReport', 'StoreSales'
$RefreshModels = 'FieldParamTest', 'DqParamReport', 'LargeExportTest'

$fab = 'https://api.fabric.microsoft.com/v1'
$pbi = 'https://api.powerbi.com/v1.0/myorg'
$FabricResource = 'https://api.fabric.microsoft.com'
$PbiResource    = 'https://analysis.windows.net/powerbi/api'
$SqlResource    = 'https://database.windows.net/'

# ---- helpers ----
$script:tokens = @{}
function Get-Token([string]$Resource){
  $c = $script:tokens[$Resource]
  if(-not $c -or $c.At -lt (Get-Date).AddMinutes(-30)){
    $t = az account get-access-token --resource $Resource --query accessToken -o tsv
    if($LASTEXITCODE -ne 0 -or -not $t){ throw "Could not get a token for $Resource from Azure CLI. Run 'az login' first." }
    $script:tokens[$Resource] = @{ Value = $t; At = Get-Date }
  }
  return $script:tokens[$Resource].Value
}
function Invoke-Api([string]$Resource, [string]$Method, [string]$Uri, $Body, [switch]$Raw){
  $p = @{ Method = $Method; Uri = $Uri; Headers = @{ Authorization = "Bearer $(Get-Token $Resource)" } }
  if($null -ne $Body){ $p.Body = ($Body | ConvertTo-Json -Depth 10); $p.ContentType = 'application/json' }
  if($Raw){ return Invoke-WebRequest @p -UseBasicParsing }
  return Invoke-RestMethod @p
}
function Get-Header($Response, [string]$Name){
  $v = $Response.Headers[$Name]
  if($null -eq $v){ return $null }
  return [string]($v | Select-Object -First 1)
}
function Get-Items([string]$Collection){
  $items = @(); $url = "$fab/workspaces/$Workspace/$Collection"
  while($url){
    $page = Invoke-Api $FabricResource GET $url
    $items += @($page.value)
    $url = $page.continuationUri
  }
  return $items
}
function Find-Item([string]$Collection, [string]$Name){
  $hits = @(@(Get-Items $Collection) | Where-Object { $_.displayName -eq $Name })
  if($hits.Count -gt 1){ throw "Workspace $Workspace has $($hits.Count) $Collection items named '$Name'; rename or delete the extras." }
  if($hits.Count -eq 1){ return $hits[0] }
  return $null
}
function Wait-FabricOperation([string]$Location){
  while($true){
    Start-Sleep -Seconds 5
    $s = Invoke-Api $FabricResource GET $Location
    if($s.status -eq 'Succeeded'){ return }
    if($s.status -eq 'Failed' -or $s.status -eq 'Cancelled'){
      throw "Operation $Location ended with status $($s.status): $($s.error | ConvertTo-Json -Depth 5 -Compress)"
    }
  }
}
function Get-PlatformName([string]$Folder){
  return (Get-Content -Raw -Path (Join-Path $Folder '.platform') | ConvertFrom-Json).metadata.displayName
}
# The deploy tokens in a folder whose item doesn't exist yet (so the deploy can't resolve them).
function Get-MissingDependencies([string]$Folder){
  $missing = @()
  $texts = Get-ChildItem -Path $Folder -Recurse -File | Where-Object { $_.Extension -in '.json', '.pbir', '.pbism', '.tmdl', '.platform', '.pq', '.m' }
  $found = $texts | Select-String -Pattern '\{\{(SemanticModelId|WarehouseServer):([^{}]+)\}\}' -AllMatches |
    ForEach-Object { $_.Matches } | ForEach-Object { "$($_.Groups[1].Value):$($_.Groups[2].Value)" } | Sort-Object -Unique
  foreach($t in $found){
    $kind, $name = $t -split ':', 2
    $collection = if($kind -eq 'SemanticModelId'){ 'semanticModels' } else { 'warehouses' }
    $item = Find-Item $collection $name
    if(-not $item -or ($kind -eq 'WarehouseServer' -and -not $item.properties.connectionString)){ $missing += "$collection '$name'" }
  }
  return $missing
}

$summary = New-Object System.Collections.Generic.List[object]
function Add-Summary([string]$Type, [string]$Name, $Item, [string]$Action){
  $id = if($Item){ $Item.id } else { '' }
  $summary.Add([pscustomobject]@{ Type = $Type; Name = $Name; Id = $id; Action = $Action })
}

# Upserts one folder through deploy-model.ps1 (update in place when the item exists).
function Publish-Folder([string]$Folder, [string]$Kind){
  $name = Get-PlatformName $Folder
  $type = if($Kind -eq 'reports'){ 'Report' } else { 'SemanticModel' }
  $existing = Find-Item $Kind $name
  $verb = if($existing){ 'update' } else { 'create' }
  $missing = @(Get-MissingDependencies $Folder)
  if($missing.Count -gt 0){
    if(-not $whatIf){ throw "$type '$name' depends on $($missing -join ', '), which doesn't exist in workspace $Workspace." }
    Write-Host "Skipping $type '$name' ($verb): depends on $($missing -join ', '), which this run would create." -ForegroundColor Yellow
    Add-Summary $type $name $existing "would $verb (after its dependencies)"
    return
  }
  Write-Host "`n== $type '$name' ($verb) ==" -ForegroundColor Cyan
  $a = @{ Folder = $Folder; Kind = $Kind; Workspace = $Workspace; WhatIf = $whatIf }
  if($existing){ $a.ItemId = $existing.id; $a.UpdateMetadata = $true } else { $a.DisplayName = $name }
  & "$PSScriptRoot\deploy-model.ps1" @a | Out-Host
  if($whatIf){ Add-Summary $type $name $existing "would $verb"; return }
  $action = "${verb}d"
  # The PBIX import creates the StoreSales report too; the PBIR here then replaces it.
  if($Kind -eq 'reports' -and $name -eq 'StoreSales' -and $script:storeSalesImported){ $action = 'imported (PBIX), then updated from PBIR' }
  Add-Summary $type $name (Find-Item $Kind $name) $action
}

function Import-Pbix([string]$Path, [string]$Name){
  $bytes = [IO.File]::ReadAllBytes($Path)
  $boundary = [guid]::NewGuid().ToString('N')
  $enc = [Text.Encoding]::UTF8
  $head = $enc.GetBytes("--$boundary`r`nContent-Disposition: form-data; name=`"file`"; filename=`"$Name.pbix`"`r`nContent-Type: application/octet-stream`r`n`r`n")
  $tail = $enc.GetBytes("`r`n--$boundary--`r`n")
  $body = New-Object byte[] ($head.Length + $bytes.Length + $tail.Length)
  [Buffer]::BlockCopy($head, 0, $body, 0, $head.Length)
  [Buffer]::BlockCopy($bytes, 0, $body, $head.Length, $bytes.Length)
  [Buffer]::BlockCopy($tail, 0, $body, $head.Length + $bytes.Length, $tail.Length)
  $url = "$pbi/groups/$Workspace/imports?datasetDisplayName=$([uri]::EscapeDataString("$Name.pbix"))&nameConflict=CreateOrOverwrite"
  $imp = Invoke-RestMethod -Method Post -Uri $url -Headers @{ Authorization = "Bearer $(Get-Token $PbiResource)" } `
    -ContentType "multipart/form-data; boundary=$boundary" -Body $body
  $deadline = (Get-Date).AddMinutes(15)
  while((Get-Date) -lt $deadline){
    Start-Sleep -Seconds 5
    $s = Invoke-Api $PbiResource GET "$pbi/groups/$Workspace/imports/$($imp.id)"
    if($s.importState -eq 'Succeeded'){ return }
    if($s.importState -eq 'Failed'){ throw "Import of $Path failed: $($s | ConvertTo-Json -Depth 5 -Compress)" }
  }
  throw "Import of $Path did not finish within 15 minutes (import id $($imp.id))."
}

# Starts a refresh and waits for it; returns $null on success, else the error text.
function Invoke-Refresh($Model){
  $base = "$pbi/groups/$Workspace/datasets/$($Model.id)/refreshes"
  $r = Invoke-Api $PbiResource POST $base @{ notifyOption = 'NoNotification' } -Raw
  $requestId = Get-Header $r 'RequestId'
  $deadline = (Get-Date).AddMinutes(60)
  while((Get-Date) -lt $deadline){
    Start-Sleep -Seconds 10
    $history = @((Invoke-Api $PbiResource GET "$base`?`$top=5").value)
    $run = if($requestId){ $history | Where-Object { $_.requestId -eq $requestId } | Select-Object -First 1 } else { $history | Select-Object -First 1 }
    if(-not $run -or $run.status -eq 'Unknown'){ continue }
    if($run.status -eq 'Completed'){ return $null }
    $err = if($run.serviceExceptionJson){ $run.serviceExceptionJson } else { "status $($run.status)" }
    return [string]$err
  }
  return 'did not finish within 60 minutes'
}

# ---- 0. workspace ----
$ws = Invoke-Api $FabricResource GET "$fab/workspaces/$Workspace"
Write-Host "Workspace: $($ws.displayName) ($Workspace)"
if(-not $ws.capacityId){ throw "Workspace $Workspace has no capacity assigned. The Warehouse and the XMLA endpoint need a Fabric or Premium capacity." }

# ---- 1. Warehouse ----
Write-Host "`n== Warehouse '$WarehouseName' ==" -ForegroundColor Cyan
$wh = Find-Item 'warehouses' $WarehouseName
$whAction = 'exists'
if(-not $wh){
  $whAction = 'would create'
  if($PSCmdlet.ShouldProcess("warehouse '$WarehouseName' in workspace $Workspace", 'Create')){
    $r = Invoke-Api $FabricResource POST "$fab/workspaces/$Workspace/warehouses" @{ displayName = $WarehouseName } -Raw
    if($r.StatusCode -eq 202){ Wait-FabricOperation (Get-Header $r 'Location') }
    $whAction = 'created'
  }
}
if(-not $whatIf -or $wh){
  $deadline = (Get-Date).AddMinutes(10)
  while(-not ($wh -and $wh.properties.connectionString)){
    if((Get-Date) -gt $deadline){ throw "Warehouse '$WarehouseName' has no SQL connection string after 10 minutes." }
    Start-Sleep -Seconds 5
    $wh = Find-Item 'warehouses' $WarehouseName
  }
  $server = $wh.properties.connectionString
  Write-Host "SQL endpoint: $server"
  if($SkipSeed){
    Write-Host 'Seeding skipped (-SkipSeed).'
  } elseif($PSCmdlet.ShouldProcess("$WarehouseName ($server)", 'Seed with tools/WhSql/seed.sql (drops and recreates its tables)')){
    $env:SQLTOKEN = Get-Token $SqlResource
    try {
      & dotnet run --project (Join-Path $root 'tools\WhSql') -- $server $WarehouseName (Join-Path $root 'tools\WhSql\seed.sql') | Out-Host
      if($LASTEXITCODE -ne 0){ throw "Seeding $WarehouseName failed (WhSql exit code $LASTEXITCODE)." }
    } finally { Remove-Item Env:SQLTOKEN -ErrorAction SilentlyContinue }
    $whAction += ', seeded'
  }
}
Add-Summary 'Warehouse' $WarehouseName $wh $whAction

# ---- 2. StoreSales model (PBIX import) ----
$storeSales = Find-Item 'semanticModels' 'StoreSales'
if($storeSales){
  Add-Summary 'SemanticModel' 'StoreSales' $storeSales 'exists (PBIX import, not redeployed)'
} elseif($SkipStoreSalesImport){
  Write-Host 'StoreSales model is missing; import skipped (-SkipStoreSalesImport), so the StoreSales report will fail to deploy.' -ForegroundColor Yellow
} else {
  Write-Host "`n== SemanticModel 'StoreSales' (import StoreSales.pbix) ==" -ForegroundColor Cyan
  if($PSCmdlet.ShouldProcess('infra/samples/StoreSales.pbix', 'Import')){
    Import-Pbix (Join-Path $root 'infra\samples\StoreSales.pbix') 'StoreSales'
    $script:storeSalesImported = $true
    Add-Summary 'SemanticModel' 'StoreSales' (Find-Item 'semanticModels' 'StoreSales') 'imported'
  } else {
    Add-Summary 'SemanticModel' 'StoreSales' $null 'would import'
  }
}

# ---- 3. semantic models, then reports ----
foreach($f in $ModelFolders){ Publish-Folder (Join-Path $root "infra\models\$f") 'semanticModels' }
foreach($f in $ReportFolders){ Publish-Folder (Join-Path $root "infra\reports\$f") 'reports' }

# ---- 4. refresh ----
$failures = @()
if($SkipRefresh){
  Write-Host "`nRefresh skipped (-SkipRefresh)."
} else {
  foreach($n in $RefreshModels){
    $m = Find-Item 'semanticModels' $n
    if(-not $m){ continue }   # only under -WhatIf: the model would be created
    if(-not $PSCmdlet.ShouldProcess("semantic model '$n'", 'Refresh')){ continue }
    Write-Host "`n== Refresh '$n' ==" -ForegroundColor Cyan
    $started = Get-Date
    $err = Invoke-Refresh $m
    if($err){
      Write-Host "Refresh of '$n' failed: $err" -ForegroundColor Red
      $failures += $n
      ($summary | Where-Object { $_.Type -eq 'SemanticModel' -and $_.Name -eq $n }) | ForEach-Object { $_.Action += ', REFRESH FAILED' }
    } else {
      Write-Host ("Refreshed in {0:N0} s." -f ((Get-Date) - $started).TotalSeconds)
      ($summary | Where-Object { $_.Type -eq 'SemanticModel' -and $_.Name -eq $n }) | ForEach-Object { $_.Action += ', refreshed' }
    }
  }
}

Write-Host "`n== Summary ==" -ForegroundColor Cyan
$summary | Format-Table -AutoSize | Out-Host
if($failures.Count -gt 0){
  throw ("Refresh failed for: $($failures -join ', '). If the error is about credentials, set them once in the Power BI service " +
    "(semantic model > Settings > Data source credentials > Edit credentials, OAuth2), then rerun with -SkipSeed.")
}
