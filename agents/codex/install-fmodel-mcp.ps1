[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Executable,

    [string] $ServerName = "fmodel"
)

$ErrorActionPreference = "Stop"

$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
if (-not $resolvedExecutable.EndsWith("FModel.Mcp.exe", [StringComparison]::OrdinalIgnoreCase))
{
    throw "Executable must point to the dedicated FModel.Mcp.exe server."
}

$codexCommand = Get-Command codex -ErrorAction Stop

& $codexCommand.Source mcp remove $ServerName 2>$null
& $codexCommand.Source mcp add $ServerName -- $resolvedExecutable
& $codexCommand.Source mcp list
