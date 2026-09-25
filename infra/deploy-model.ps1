param(
  [Parameter(Mandatory=$true)][string]$Folder,
  [string]$DisplayName,
  [string]$Kind = "semanticModels",
  [string]$Workspace = "00000000-0000-0000-0000-000000000022",
  # When set, update the existing item's definition in place (keeps its ID) instead of creating one.
  [string]$ItemId,
  # With -ItemId: also apply .platform metadata (displayName/description) via updateMetadata=true.
  [switch]$UpdateMetadata
)
$ErrorActionPreference = "Stop"
if(-not $ItemId -and -not $DisplayName){ throw "Pass -DisplayName (create) or -ItemId (update)." }
$tok = az account get-access-token --resource "https://api.fabric.microsoft.com" --query accessToken -o tsv
$H = @{ Authorization = "Bearer $tok"; "Content-Type" = "application/json" }

$root = (Resolve-Path $Folder).Path
$files = Get-ChildItem -Path $root -Recurse -File | Where-Object { -not ($ItemId -and -not $UpdateMetadata -and $_.Name -eq ".platform") }
$parts = @()
foreach($f in $files){
  $rel = $f.FullName.Substring($root.Length+1) -replace '\\','/'
  $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($f.FullName))
  $parts += @{ path = $rel; payload = $b64; payloadType = "InlineBase64" }
}
"Parts: " + ($parts.path -join ", ")
if($ItemId){
  $body = @{ definition = @{ parts = $parts } } | ConvertTo-Json -Depth 8
  $uri = "https://api.fabric.microsoft.com/v1/workspaces/$Workspace/$Kind/$ItemId/updateDefinition" + $(if($UpdateMetadata){"?updateMetadata=true"}else{""})
} else {
  $body = @{ displayName = $DisplayName; definition = @{ parts = $parts } } | ConvertTo-Json -Depth 8
  $uri = "https://api.fabric.microsoft.com/v1/workspaces/$Workspace/$Kind"
}
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