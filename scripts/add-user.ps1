<# Add a server-side device using explicitly supplied server and SSH settings.
   The resulting UUID is sensitive: keep command output private. Distribute a
   complete personal connection link separately from the blank Client installer. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9 _.-]{1,64}$')][string]$Name,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9.-]{1,253}$')][string]$Server,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{1,32}$')][string]$SshUser,
    [Parameter(Mandatory)][string]$Key,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $Key -PathType Leaf)) { throw 'Supply the path to your SSH private key.' }
$flag = if ($DryRun) { '--dry-run ' } else { '' }
& ssh -i $Key -o StrictHostKeyChecking=accept-new -- "$SshUser@$Server" "sudo /usr/local/bin/add-user.sh $flag'$Name'"
if ($LASTEXITCODE -ne 0) { throw 'The server-side device operation failed.' }
