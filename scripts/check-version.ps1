param(
    [string]$ReleaseTag = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$declaredVersion = (Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'VERSION')).Trim()
[xml]$project = Get-Content -Raw -LiteralPath (
    Join-Path $repoRoot 'src\GenesisMesh.Sdk\GenesisMesh.Sdk.csproj'
)
$packageVersion = [string]$project.Project.PropertyGroup.Version

if ($declaredVersion -ne $packageVersion) {
    throw "VERSION ($declaredVersion) does not match project version ($packageVersion)"
}

if ($ReleaseTag) {
    $tagVersion = $ReleaseTag -replace '^v', ''
    if ($tagVersion -ne $declaredVersion) {
        throw "Release tag $ReleaseTag does not match project version $declaredVersion"
    }
}

Write-Output "Genesis Mesh .NET SDK release version verified: $declaredVersion"
