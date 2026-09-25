<#
.SYNOPSIS
  Deploys a TMDL/PBIR folder as a Fabric item: create (-DisplayName) or update in place (-ItemId).
.DESCRIPTION
  -Workspace defaults to the PowerBI:WorkspaceId setting (env var PowerBI__WorkspaceId >
  user secrets > appsettings.json; see infra/settings.ps1), so no workspace ID is committed.

  Text parts can contain deploy-time tokens, resolved against the target workspace just
  before upload (the files on disk are never modified):
    {{SemanticModelId:<displayName>}}  -> id of that semantic model (report definition.pbir binding)
    {{WarehouseServer:<displayName>}}  -> SQL connection host of that Warehouse (M parameters)
  An unresolvable token stops the deploy. -WhatIf resolves and prints the tokens but skips the POST.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [Parameter(Mandatory=$true)][string]$Folder,
  [string]$DisplayName,
  [string]$Kind = "semanticModels",
  [string]$Workspace,
  # When set, update the existing item's definition in place (keeps its ID) instead of creating one.
  [string]$ItemId,
  # With -ItemId: also apply .platform metadata (displayName/description) via updateMetadata=true.
  [switch]$UpdateMetadata
)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\settings.ps1"
if(-not $ItemId -and -not $DisplayName){ throw "Pass -DisplayName (create) or -ItemId (update)." }
if(-not $Workspace){ $Workspace = Get-PbiSetting 'PowerBI:WorkspaceId' -Required }
"Workspace: $Workspace"
$tok = az account get-access-token --resource "https://api.fabric.microsoft.com" --query accessToken -o tsv
if(-not $tok){ throw "Could not get a Fabric token from Azure CLI. Run 'az login' first." }
$H = @{ Authorization = "Bearer $tok"; "Content-Type" = "application/json" }

# ---- deploy-time tokens ----
$script:itemLists = @{}
function Get-WorkspaceItems([string]$Collection){
  if(-not $script:itemLists.ContainsKey($Collection)){
    $items = @(); $url = "https://api.fabric.microsoft.com/v1/workspaces/$Workspace/$Collection"
    while($url){
      $page = Invoke-RestMethod -Uri $url -Headers $H
      $items += @($page.value)
      $url = if($page.continuationUri){ $page.continuationUri } else { $null }
    }
    $script:itemLists[$Collection] = $items
  }
  return $script:itemLists[$Collection]
}
function Resolve-DeployToken([string]$TokenKind, [string]$Name){
  $collection = switch($TokenKind){ 'SemanticModelId' { 'semanticModels' } 'WarehouseServer' { 'warehouses' } }
  $hits = @(Get-WorkspaceItems $collection | Where-Object { $_.displayName -eq $Name })
  if($hits.Count -ne 1){ throw "Token {{${TokenKind}:$Name}}: expected exactly one $collection item named '$Name' in workspace $Workspace, found $($hits.Count)." }
  if($TokenKind -eq 'SemanticModelId'){ return $hits[0].id }
  $v = $hits[0].properties.connectionString
  if(-not $v){ throw "Token {{${TokenKind}:$Name}}: warehouse '$Name' has no properties.connectionString yet." }
  return $v
}
$tokenPattern = '\{\{(SemanticModelId|WarehouseServer):([^{}]+)\}\}'
$textExtensions = '.json','.pbir','.pbism','.tmdl','.platform','.pq','.m'

$root = (Resolve-Path $Folder).Path
$files = Get-ChildItem -Path $root -Recurse -File | Where-Object { -not ($ItemId -and -not $UpdateMetadata -and $_.Name -eq ".platform") }
$parts = @()
foreach($f in $files){
  $rel = $f.FullName.Substring($root.Length+1) -replace '\\','/'
  $bytes = [IO.File]::ReadAllBytes($f.FullName)
  if($textExtensions -contains $f.Extension.ToLowerInvariant()){
    # UTF-8 decode/encode round-trips a BOM (as U+FEFF), so untouched bytes stay identical.
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    if($text -match $tokenPattern){
      $text = [regex]::Replace($text, $tokenPattern, {
        param($m)
        $v = Resolve-DeployToken $m.Groups[1].Value $m.Groups[2].Value
        Write-Host "  token $($m.Value) -> $v  ($rel)"
        $v
      })
      $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    }
  }
  $parts += @{ path = $rel; payload = [Convert]::ToBase64String($bytes); payloadType = "InlineBase64" }
}
"Parts: " + ($parts.path -join ", ")
if($ItemId){
  $body = @{ definition = @{ parts = $parts } } | ConvertTo-Json -Depth 8
  $uri = "https://api.fabric.microsoft.com/v1/workspaces/$Workspace/$Kind/$ItemId/updateDefinition" + $(if($UpdateMetadata){"?updateMetadata=true"}else{""})
} else {
  $body = @{ displayName = $DisplayName; definition = @{ parts = $parts } } | ConvertTo-Json -Depth 8
  $uri = "https://api.fabric.microsoft.com/v1/workspaces/$Workspace/$Kind"
}
$target = if($ItemId){ "$Kind/$ItemId in workspace $Workspace" } else { "new $Kind '$DisplayName' in workspace $Workspace" }
if(-not $PSCmdlet.ShouldProcess($target, $(if($ItemId){ "updateDefinition" } else { "createItemWithDefinition" }))){ return }
try {
  $resp = Invoke-WebRequest -Method Post -Uri $uri -Headers $H -Body $body
  "POST status: $($resp.StatusCode)"
  if($resp.StatusCode -eq 201){
    ($resp.Content | ConvertFrom-Json) | Select-Object id,displayName | Format-List
  } elseif($resp.StatusCode -eq 202) {
    $op = "$($resp.Headers['x-ms-operation-id'])"
    if(-not $op -and $resp.Headers['Location']){ $op = ("$($resp.Headers['Location'])" -split '/operations/')[1] -replace '[/?].*$','' }
    "LRO op: $op"
    do { Start-Sleep 3; $s = Invoke-RestMethod -Uri "https://api.fabric.microsoft.com/v1/operations/$op" -Headers @{Authorization="Bearer $tok"} } while($s.status -in "Running","NotStarted")
    "LRO status: $($s.status)"
    if($s.status -eq "Succeeded" -and -not $ItemId){
      $r = Invoke-RestMethod -Uri "https://api.fabric.microsoft.com/v1/operations/$op/result" -Headers @{Authorization="Bearer $tok"}
      $r | Select-Object id,displayName | Format-List
    } elseif($s.status -ne "Succeeded") { $s | ConvertTo-Json -Depth 8 }
  }
} catch {
  "ERR: $($_.Exception.Message)"
  $_.ErrorDetails.Message
}