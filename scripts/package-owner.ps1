<# Blank installer: each device imports its own link. Publishing is a separate step. #>
[CmdletBinding()]
param()
& (Join-Path $PSScriptRoot 'package-release.ps1') -Flavor Owner