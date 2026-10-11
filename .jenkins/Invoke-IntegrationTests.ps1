[CmdletBinding()]
param(
    [string] $Namespace,
    
    [string] $Name,

    [string] $Variant = 'latest',
    
    [switch] $Pull = $false,
    
    [string] $Context = 'default',
    
    [string] $TestsPath = 'tests',

    [string] $ResultsPath = '.reports/report.xml'
)

function ConvertFrom-DockerArgumentString {
    param(
        [string] $Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    $parseErrors = $null
    $tokens = [Management.Automation.PSParser]::Tokenize("docker $Value", [ref] $parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "Invalid Docker arguments '$Value': $($parseErrors.Message -join '; ')"
    }

    $tokens |
        Select-Object -Skip 1 |
        ForEach-Object { $_.Content }
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$requiredModulesPath = Join-Path $PSScriptRoot 'RequiredModules.psd1'
Install-PSResource `
    -RequiredResourceFile $requiredModulesPath `
    -Scope CurrentUser `
    -TrustRepository `
    -AcceptLicense `
    -Quiet `
    -WarningAction SilentlyContinue

Import-Module Pester -ErrorAction Stop
Import-Module pwsh-dotenv -ErrorAction Stop
. (Join-Path $PSScriptRoot 'Docker.ps1')

$environmentPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.env'))
$environment = Read-Dotenv -Path $environmentPath -AllowClobber

if ([string]::IsNullOrWhiteSpace($Namespace)) {
    $Namespace = $environment.DOCKER_NAMESPACE
}
if ([string]::IsNullOrWhiteSpace($Name)) {
    $Name = $environment.DOCKER_IMAGE
}
if ([string]::IsNullOrWhiteSpace($Namespace)) {
    throw "Docker namespace is missing; pass -Namespace or set DOCKER_NAMESPACE in $environmentPath"
}
if ([string]::IsNullOrWhiteSpace($Name)) {
    throw "Docker image name is missing; pass -Name or set DOCKER_IMAGE in $environmentPath"
}

$resolvedResultsPath = [IO.Path]::GetFullPath(
    $ResultsPath,
    (Get-Location).Path
)
$resultsDirectory = Split-Path -Parent $resolvedResultsPath
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null

$Image = $Namespace + "/" + $Name + ":" + $Variant

if ($Pull) {
    Invoke-Docker -Context $Context -Arguments @('pull', $Image)
}

$os = Invoke-DockerOutput -Context $Context -Arguments @('version', '--format', '{{.Server.Os}}')
$dockerArgumentsVariable = "DOCKER_ARGS_$($os.ToUpperInvariant())"
$dockerRunArguments = @(
    ConvertFrom-DockerArgumentString -Value $environment[$dockerArgumentsVariable]
)

$testData = @{
    Context = $Context
    Namespace = $Namespace
    Name = $Name
    Variant = $Variant
    Image = $Image
    Os = $os
    DockerRunArguments = $dockerRunArguments
}

$testsPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' $TestsPath))
$testFiles = @(
    Get-ChildItem -LiteralPath $testsPath -Filter '*.Tests.ps1' -File |
        Sort-Object FullName
)
if ($testFiles.Count -eq 0) {
    throw "No Pester test files were found in $testsPath"
}

$containers = @(
    $testFiles | ForEach-Object {
        New-PesterContainer -Path $_.FullName -Data $testData
    }
)

$configuration = New-PesterConfiguration
$configuration.Run.Container = $containers
$configuration.Run.Exit = $false
$configuration.Run.PassThru = $true
$configuration.Output.Verbosity = 'Detailed'
$configuration.TestResult.Enabled = $true
$configuration.TestResult.OutputFormat = 'JUnitXml'
$configuration.TestResult.OutputPath = $resolvedResultsPath
$configuration.TestResult.TestSuiteName = "Docker $($testData.Image) [$($testData.Context), $($testData.Variant)]"

$result = Invoke-Pester -Configuration $configuration

if ($null -eq $result) {
    throw 'Pester returned no result'
}
if ($result.TotalCount -eq 0) {
    throw 'Pester discovered no tests'
}
if ($result.FailedContainersCount -gt 0 -or $result.FailedBlocksCount -gt 0) {
    throw "Pester infrastructure failed: $($result.FailedContainersCount) container(s), $($result.FailedBlocksCount) block(s)"
}

if ($result.FailedCount -gt 0) {
    exit 1
}

exit 0
