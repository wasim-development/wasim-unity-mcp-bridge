param([Parameter(Mandatory=$true)][string]$ConfigPath)
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
New-Item -ItemType File -Path ([string]$config.stopFlagPath) -Force | Out-Null
Write-Host "Stop signal written to $($config.stopFlagPath)"
