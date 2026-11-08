#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AccountId,
    [Parameter(Mandatory)][string]$ExpectedSender,
    [Parameter(Mandatory)][string]$ConfigPath,
    [Parameter(Mandatory)][string]$ConnectorPath,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
try {
    if (![IO.Path]::IsPathFullyQualified($ConfigPath) -or ![IO.Path]::IsPathFullyQualified($ConnectorPath)) { throw 'Invalid paths.' }
    $cleanupConfig = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -AsHashtable
    $accounts = @($cleanupConfig.Connector.Accounts | Where-Object { $_.Id -ceq $AccountId })
    if ($accounts.Count -ne 1 -or $cleanupConfig.Connector.EnableInboxCleanup -ne $true -or
        $accounts[0].InboxCleanup.Enabled -ne $true -or
        ![string]::Equals($accounts[0].InboxCleanup.Sender, $ExpectedSender, [StringComparison]::OrdinalIgnoreCase) -or
        $accounts[0].InboxCleanup.TrashFolder -cne 'Trash') { throw 'Cleanup policy mismatch.' }
    $cleanupArguments = @('accounts', 'cleanup-inbox', $AccountId, '--config', $ConfigPath)
    if ($Apply) { $cleanupArguments += '--apply' }
    & $ConnectorPath @cleanupArguments
    exit $LASTEXITCODE
}
catch {
    # Configuration and credentials may contain private values. Do not print exception details.
    [Console]::Error.WriteLine('Cleanup could not start. Check the executable, configuration and expected sender rule. No messages were intentionally moved by this invocation before connector startup.')
    exit 1
}
