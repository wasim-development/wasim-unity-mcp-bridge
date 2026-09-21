#requires -Version 5.1
param(
    [Parameter(Mandatory=$true)][string]$UnityEditor,
    [Parameter(Mandatory=$true)][string]$ProjectPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'Results')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $UnityEditor -PathType Leaf)) { throw 'Unity Editor executable not found.' }
if (-not (Test-Path -LiteralPath (Join-Path $ProjectPath 'ProjectSettings/ProjectVersion.txt'))) { throw 'Not a Unity project.' }
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$results = Join-Path $OutputDirectory 'editmode-results.xml'
$log = Join-Path $OutputDirectory 'unity-tests.log'
# Close the same project in the interactive Editor before batchmode. Unity exits after runTests.
& $UnityEditor -batchmode -nographics -projectPath $ProjectPath -runTests -testPlatform EditMode -testFilter WasimDevelopment.UnityMcpBridge.Tests -testResults $results -logFile $log
if ($LASTEXITCODE -ne 0) { throw ('Unity failed with exit code ' + $LASTEXITCODE + '. Read ' + $log) }
if (-not (Test-Path -LiteralPath $results)) { throw 'Unity did not produce a test-results file. Check package testables and compilation errors.' }
[xml]$report = Get-Content -LiteralPath $results -Raw
$run = $report.SelectSingleNode('/test-run')
if ($null -eq $run -or [int]$run.GetAttribute('failed') -gt 0 -or [int]$run.GetAttribute('passed') -lt 6) {
    throw 'Not all six bridge integration tests executed successfully. Read the XML and Unity log.'
}
Write-Host ('PASS: bridge compiled and six integration tests passed. Results: ' + $results)
