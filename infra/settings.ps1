# Shared settings lookup for infra scripts. Dot-source it:
#
#     . "$PSScriptRoot\settings.ps1"
#     $ws = Get-PbiSetting 'PowerBI:WorkspaceId' -Required
#
# It mirrors the web app's cascade, highest precedence first. Explicit script
# parameters beat all of these; callers only use Get-PbiSetting when a
# parameter wasn't passed.
#   1. Environment variable   (PowerBI__WorkspaceId, ':' -> '__')
#   2. .NET user secrets      (the web project's <UserSecretsId>; dotnet user-secrets set ...)
#   3. src/PbiVisualExport.Web/appsettings.json (committed; IDs are empty there)
# Works in Windows PowerShell 5.1 and PowerShell 7+.

$script:PbiRepoRoot = Split-Path $PSScriptRoot -Parent
$script:PbiWebProject = Join-Path $script:PbiRepoRoot 'src\PbiVisualExport.Web'

function ConvertTo-PbiFlatSettings {
    # Flattens parsed JSON into 'Section:Key' pairs (the .NET configuration key shape).
    param($Node, [string]$Prefix = '', [hashtable]$Into = @{})
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($p in $Node.PSObject.Properties) {
            $k = if ($Prefix) { "${Prefix}:$($p.Name)" } else { $p.Name }
            ConvertTo-PbiFlatSettings -Node $p.Value -Prefix $k -Into $Into | Out-Null
        }
    } elseif ($Node -is [System.Array]) {
        for ($i = 0; $i -lt $Node.Count; $i++) { ConvertTo-PbiFlatSettings -Node $Node[$i] -Prefix "${Prefix}:$i" -Into $Into | Out-Null }
    } elseif ($Prefix) {
        $Into[$Prefix] = if ($null -eq $Node) { $null } else { "$Node" }
    }
    return $Into
}

function Read-PbiJsonSettings([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @{} }
    $text = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($text)) { return @{} }
    return ConvertTo-PbiFlatSettings -Node ($text | ConvertFrom-Json)
}

function Get-PbiUserSecretsPath {
    $csproj = Get-ChildItem -LiteralPath $script:PbiWebProject -Filter *.csproj | Select-Object -First 1
    if (-not $csproj) { return $null }
    $id = ([xml](Get-Content -LiteralPath $csproj.FullName -Raw)).Project.PropertyGroup.UserSecretsId | Where-Object { $_ } | Select-Object -First 1
    if (-not $id) { return $null }
    if ($env:APPDATA) { return Join-Path $env:APPDATA "Microsoft\UserSecrets\$id\secrets.json" }
    return Join-Path $HOME ".microsoft/usersecrets/$id/secrets.json"
}

function Resolve-PbiSetting {
    <# Returns @{ Key; Value; Source } for the highest-precedence non-empty value (Value $null if none). #>
    param([Parameter(Mandatory = $true)][string]$Key)
    $envName = $Key -replace ':', '__'
    $v = [Environment]::GetEnvironmentVariable($envName)
    if (-not [string]::IsNullOrWhiteSpace($v)) { return [pscustomobject]@{ Key = $Key; Value = $v; Source = "env:$envName" } }

    $layers = @(
        @{ Name = 'user secrets'; Path = (Get-PbiUserSecretsPath) },
        @{ Name = 'appsettings.json'; Path = (Join-Path $script:PbiWebProject 'appsettings.json') })
    foreach ($layer in $layers) {
        if (-not $layer.Path) { continue }
        $flat = Read-PbiJsonSettings $layer.Path
        # .NET configuration keys are case-insensitive.
        $match = $flat.Keys | Where-Object { $_ -ieq $Key } | Select-Object -First 1
        if ($match -and -not [string]::IsNullOrWhiteSpace($flat[$match])) {
            return [pscustomobject]@{ Key = $Key; Value = $flat[$match]; Source = $layer.Name }
        }
    }
    return [pscustomobject]@{ Key = $Key; Value = $null; Source = $null }
}

function Get-PbiSetting {
    param([Parameter(Mandatory = $true)][string]$Key, [switch]$Required)
    $r = Resolve-PbiSetting $Key
    if ($r.Value) {
        Write-Verbose "$Key = $($r.Value) (from $($r.Source))"
        return $r.Value
    }
    if ($Required) {
        throw ("$Key is not configured. Pass it as a script parameter, or set it once with one of:`n" +
            "  dotnet user-secrets set $Key <value> --project src/PbiVisualExport.Web`n" +
            "  `$env:$($Key -replace ':', '__') = '<value>'")
    }
    return $null
}
