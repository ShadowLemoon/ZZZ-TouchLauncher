<#
.SYNOPSIS
Repin the ZZZ-TouchRuntime release consumed by the Launcher workflow.

.DESCRIPTION
Downloads the requested ZZZ-TouchRuntime release asset, verifies that it only
contains the two expected DLLs, measures the real SHA-256 values, and rewrites
runtime-pin.json, which the build workflow reads at run time.

Hashes always come from the downloaded artifact, never from the GitHub API.

.EXAMPLE
pwsh -File tools\update_runtime_pin.ps1 -Version v1.0.1

.EXAMPLE
pwsh -File tools\update_runtime_pin.ps1 -Version v1.0.1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pinFile = Join-Path $repositoryRoot 'runtime-pin.json'

if (-not (Test-Path -LiteralPath $pinFile)) {
    throw "Pin file not found: $pinFile"
}

$assetName = "ZZZTouchRuntime-$Version-win-x64.zip"
$assetUrl = "https://github.com/ShadowLemoon/ZZZ-TouchRuntime/releases/download/$Version/$assetName"
$expectedFiles = @('ZZZTouchCore.dll', 'ZZZTouchRuntime.dll')

$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("runtime-pin-" + [Guid]::NewGuid().ToString('N'))
$extractDirectory = Join-Path $workDirectory 'extracted'
New-Item -ItemType Directory -Path $extractDirectory -Force -Confirm:$false -WhatIf:$false | Out-Null

try {
    $archivePath = Join-Path $workDirectory $assetName

    Write-Host "Downloading $assetUrl"
    try {
        Invoke-WebRequest -Uri $assetUrl -OutFile $archivePath
    }
    catch {
        throw "Could not download $assetName. Verify that release $Version exists. $($_.Exception.Message)"
    }

    Expand-Archive -Path $archivePath -DestinationPath $extractDirectory -WhatIf:$false

    $actualFiles = @(
        Get-ChildItem -Path $extractDirectory -Recurse -File |
            ForEach-Object {
                [System.IO.Path]::GetRelativePath($extractDirectory, $_.FullName).Replace('\', '/')
            } |
            Sort-Object
    )
    if (($actualFiles -join '|') -ne ($expectedFiles -join '|')) {
        throw "Unexpected archive contents: $($actualFiles -join ', ')"
    }

    $archiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
    $coreHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $extractDirectory 'ZZZTouchCore.dll')).Hash.ToLowerInvariant()
    $runtimeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $extractDirectory 'ZZZTouchRuntime.dll')).Hash.ToLowerInvariant()

    Write-Host ''
    Write-Host "Version               : $Version"
    Write-Host "Asset                 : $assetName"
    Write-Host "Archive SHA-256       : $archiveHash"
    Write-Host "ZZZTouchCore.dll      : $coreHash"
    Write-Host "ZZZTouchRuntime.dll   : $runtimeHash"
    Write-Host ''

    $pin = [ordered]@{
        version       = $Version
        asset         = $assetName
        archiveSha256 = $archiveHash
        files         = [ordered]@{
            'ZZZTouchCore.dll'    = $coreHash
            'ZZZTouchRuntime.dll' = $runtimeHash
        }
    }

    $originalText = [System.IO.File]::ReadAllText($pinFile)
    $newline = if ($originalText.Contains("`r`n")) { "`r`n" } else { "`n" }
    $updatedText = ($pin | ConvertTo-Json -Depth 5).Replace("`r`n", "`n").Replace("`n", $newline) + $newline

    if ($updatedText -eq $originalText) {
        Write-Host "Already up to date: $pinFile"
    }
    elseif ($PSCmdlet.ShouldProcess($pinFile, "Repin Runtime to $Version")) {
        [System.IO.File]::WriteAllText($pinFile, $updatedText, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Updated: $pinFile"
    }

    Write-Host ''
    Write-Host 'Next steps:'
    Write-Host '  1. git diff runtime-pin.json'
    Write-Host '  2. Run the offline smoke test: tests\test_restore_pc.bat'
    Write-Host '  3. Commit, then push a new Launcher v* tag to publish.'
}
finally {
    Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
