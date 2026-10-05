[CmdletBinding()]
param([Parameter(Mandatory)][string]$NotesFile, [string]$Version='', [switch]$RefreshFeeds)
$ErrorActionPreference='Stop'
$repository=Split-Path $PSScriptRoot -Parent
if(!$Version){$Version=([xml](Get-Content -LiteralPath (Join-Path $repository 'WorkTunnel.csproj') -Raw)).Project.PropertyGroup.Version|Where-Object {$_}}
if($Version -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}$'){throw 'Invalid release version'}
$publicRepo='Zykoraa/conduit';$tag='v'+$Version
$info=(& gh repo view $publicRepo --json nameWithOwner,isPrivate|ConvertFrom-Json)
if($LASTEXITCODE -ne 0 -or $info.nameWithOwner -ne $publicRepo -or $info.isPrivate){throw 'Expected the authorized public Conduit repository'}
$notes=[IO.Path]::GetFullPath($NotesFile)
if(!(Test-Path -LiteralPath $notes -PathType Leaf)){throw 'Release notes file required'}
& (Join-Path $PSScriptRoot 'check-public-source.ps1')
& (Join-Path $PSScriptRoot 'check-secrets.ps1')
$changes=& git -C $repository status --porcelain
if($LASTEXITCODE -ne 0 -or $changes){throw 'Commit and review the source before publishing'}
$head=& git -C $repository rev-parse HEAD
$publishedHead=& gh api "repos/$publicRepo/branches/main" --jq .commit.sha
if($LASTEXITCODE -ne 0 -or $head -ne $publishedHead){throw 'The reviewed source must match the pushed main branch'}
if($RefreshFeeds){
    foreach($flavor in 'Owner','Client'){
        & dotnet run --project (Join-Path $repository 'tools/ReleaseTool/ReleaseTool.csproj') -c Release -- feed (Join-Path $repository "dist/Conduit-$flavor-$Version.wtupdate") (Join-Path $repository "dist/Conduit-$flavor-stable.json")
        if($LASTEXITCODE -ne 0){throw 'Feed renewal failed'}
    }
}
& (Join-Path $PSScriptRoot 'prepare-public-release.ps1') -Version $Version
$assets=Join-Path $repository "artifacts/private/public-release/$Version/assets"
$null=& gh release view $tag --repo $publicRepo --json tagName 2>$null
$exists=$LASTEXITCODE -eq 0
if($RefreshFeeds){
    if(!$exists){throw 'Renewal requires an existing published version'}
    & gh release upload $tag --repo $publicRepo --clobber (Join-Path $assets 'Conduit-Owner-stable.json') (Join-Path $assets 'Conduit-Client-stable.json') (Join-Path $assets 'SHA256SUMS.txt')
    if($LASTEXITCODE -ne 0){throw 'Signed metadata renewal failed'}
    & gh workflow run pages.yml --repo $publicRepo -f "release_tag=$tag"
    if($LASTEXITCODE -ne 0){throw 'Pages renewal dispatch failed'}
    Write-Output "Signed metadata renewed for $tag."
    return
}
if($exists){throw 'This published version already exists. Bump the version; never replace application bytes.'}
$files=@(Get-ChildItem -LiteralPath $assets -File|Select-Object -ExpandProperty FullName)
& gh release create $tag --repo $publicRepo --target $head --title "Conduit $Version" --notes-file $notes @files
if($LASTEXITCODE -ne 0){throw 'Publication failed. Inspect the release before retrying.'}
Write-Output "Published $tag. The Pages workflow verifies and deploys the signed files."
