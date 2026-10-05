[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Assembly,
    [Parameter(Mandatory=$true)][string]$Output,
    [Parameter(Mandatory=$true)][string]$Dependencies,
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$OwnerBuild = 'false'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$Assembly = [IO.Path]::GetFullPath($Assembly)
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$xml = New-Object Xml.XmlDocument
$obfuscator = $xml.CreateElement('Obfuscator'); [void]$xml.AppendChild($obfuscator)
function Add-Rule($parent, [string]$name, [hashtable]$attributes) {
    $node = $xml.CreateElement($name)
    foreach ($key in $attributes.Keys) { $node.SetAttribute($key, [string]$attributes[$key]) }
    [void]$parent.AppendChild($node)
    return $node
}
$variables = @{
    InPath = [IO.Path]::GetDirectoryName($Assembly); OutPath = $Output
    KeepPublicApi = 'false'; HidePrivateApi = 'true'; HideStrings = 'true'
    RenameProperties = 'false'; KeepProperties = 'true'; RenameEvents = 'false'
    RenameFields = 'true'; RegenerateDebugInfo = 'false'; XmlMapping = 'true'
    ReuseNames = 'false'; UseUnicodeNames = 'false'; SuppressIldasm = 'false'
}
foreach ($key in ($variables.Keys | Sort-Object)) { [void](Add-Rule $obfuscator 'Var' @{name=$key; value=$variables[$key]}) }
$searchPaths = Get-Content -LiteralPath $Dependencies | ForEach-Object { [IO.Path]::GetDirectoryName($_) } | Sort-Object -Unique
foreach ($path in $searchPaths) { [void](Add-Rule $obfuscator 'AssemblySearchPath' @{path=$path}) }
$module = Add-Rule $obfuscator 'Module' @{file=$Assembly}
# JSON binds record constructor parameters to properties; keep those contracts and enum text.
[void](Add-Rule $module 'SkipMethod' @{type='*'; name='.ctor'})
[void](Add-Rule $module 'SkipEnums' @{value='true'})
# Generated Win32 interop is a native ABI, not product logic.
[void](Add-Rule $module 'SkipNamespace' @{name='Windows.Win32*'})
$config = Join-Path $Output 'obfuscar.xml'; $xml.Save($config)
Push-Location $root
try {
    & dotnet tool run obfuscar.console -- $config
    if ($LASTEXITCODE -ne 0) { throw 'Obfuscation failed. No unobfuscated fallback is permitted.' }
} finally { Pop-Location }
$result = Join-Path $Output ([IO.Path]::GetFileName($Assembly))
$mapping = Join-Path $Output 'Mapping.xml'
if (!(Test-Path -LiteralPath $result) -or !(Test-Path -LiteralPath $mapping)) { throw 'Obfuscator output is missing.' }
function Hash-File([string]$path) {
    $sha = [Security.Cryptography.SHA256]::Create(); $stream = [IO.File]::OpenRead($path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}
$hash = Hash-File $result
if ($hash -eq (Hash-File $Assembly)) { throw 'Obfuscator left the assembly unchanged.' }
$map = New-Object Xml.XmlDocument; $map.Load($mapping)
$renamedTypes = $map.SelectNodes('//renamedClass').Count
$renamedMethods = $map.SelectNodes('//renamedMethod').Count
if ($renamedTypes -lt 50 -or $renamedMethods -lt 100) { throw 'Unexpectedly low obfuscation coverage.' }
# Retain private reverse maps by binary hash. Never put these in the publish directory.
$flavor = if ($OwnerBuild -eq 'true') { 'Owner' } else { 'Client' }
$archive = Join-Path $root "artifacts\private\obfuscation\$Version\$flavor\$([IO.Path]::GetFileNameWithoutExtension($Assembly))-$hash"
New-Item -ItemType Directory -Force -Path $archive | Out-Null
Copy-Item -LiteralPath $mapping -Destination $archive
[IO.File]::WriteAllText((Join-Path $archive 'assembly.sha256'), $hash)
Write-Host "Obfuscated $([IO.Path]::GetFileName($Assembly)): $renamedTypes types, $renamedMethods methods; private mapping archived."
