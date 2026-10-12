[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('linux', 'windows')]
    [string] $Context,

    [string] $RepositoryHostPath,

    [ValidateSet('process', 'hyperv')]
    [string] $WindowsIsolation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Docker.ps1')

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[void](& docker context inspect $Context 2>&1)
if ($LASTEXITCODE -ne 0) {
    $endpoint = if ($Context -eq 'windows') { 'npipe:////./pipe/docker_engine' } else { 'unix:///var/run/docker.sock' }
    & docker context create $Context --docker "host=$endpoint"
    if ($LASTEXITCODE -ne 0) { throw "Could not create local Docker context $Context" }
}
$os = Invoke-DockerOutput -Context $Context -Arguments @('version', '--format', '{{.Server.Os}}')
if ($os -ne $Context) {
    throw "Docker context $Context targets $os instead of $Context"
}

# Jenkins workspaces may be named volumes inside the agent. The controller needs
# the daemon's source path, while the SDK runner sees the checkout at /solution.
if ([string]::IsNullOrWhiteSpace($RepositoryHostPath)) {
    $RepositoryHostPath = $repository
    $hostname = [Net.Dns]::GetHostName()
    $agent = Get-DockerCommandResult -Context $Context -Arguments @('inspect', $hostname)
    if ($agent.ExitCode -ne 0) {
        $containers = @((Invoke-DockerOutput -Context $Context -Arguments @('ps', '--quiet', '--no-trunc')) -split '\r?\n' | Where-Object { $_ })
        if ($containers.Count -gt 0) {
            $inspections = Invoke-DockerOutput -Context $Context -Arguments (@('inspect') + $containers) | ConvertFrom-Json
            $matchingAgent = @($inspections | Where-Object { $_.Config.Hostname -eq $hostname })
            if ($matchingAgent.Count -eq 1) {
                $agent = [pscustomobject] @{ ExitCode = 0; Output = ConvertTo-Json -InputObject $matchingAgent -Depth 20 }
            }
        }
    }
    if ($agent.ExitCode -eq 0) {
        $agentConfiguration = $agent.Output | Out-String | ConvertFrom-Json
        $mount = $agentConfiguration[0].Mounts |
            Where-Object {
                $destination = $_.Destination.TrimEnd('/', '\')
                $repository.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
            } |
            Sort-Object { $_.Destination.Length } -Descending |
            Select-Object -First 1
        if ($null -eq $mount) {
            throw 'The Jenkins workspace must be on a Docker-visible mount; pass -RepositoryHostPath explicitly.'
        }
        $relativeRepository = $repository.Substring($mount.Destination.TrimEnd('/', '\').Length).TrimStart('/', '\')
        $RepositoryHostPath = $mount.Source.TrimEnd('/', '\') + '/' + $relativeRepository.Replace('\', '/')
    }
}

$contextDirectory = Join-Path $repository '.reports/daemon-runner'
New-Item -ItemType Directory -Path $contextDirectory -Force | Out-Null
$image = "tmp/unity-daemon-runner:$os"
$buildArguments = @('build')
$runArguments = @('run', '--rm')
$containerRepository = if ($os -eq 'windows') { 'C:\solution' } else { '/solution' }
$resultsDirectory = Join-Path $repository ".reports/daemon-$os"
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$resultsPath = Join-Path $resultsDirectory "TestResults-$([guid]::NewGuid().ToString('N')).xml"
$containerResultsPath = $containerRepository + '/.reports/daemon-' + $os + '/' + [IO.Path]::GetFileName($resultsPath)
$fixtureCategory = if ($os -eq 'windows') { 'DockerWindows' } else { 'DockerLinux' }
$testCommand = 'dotnet test common/Unity.DaemonTests/Unity.DaemonTests.csproj --configuration Release --artifacts-path "' + $containerRepository + '/.reports/daemon-' + $os + '/artifacts" --logger "junit;LogFilePath=' + $containerResultsPath + '" -- NUnit.Where=cat==' + $fixtureCategory
$fixtureRoot = $RepositoryHostPath.TrimEnd('/', '\') + '/common/Unity.Tests/test-files/ValidProject'

if ($os -eq 'windows') {
    if ([string]::IsNullOrWhiteSpace($WindowsIsolation)) {
        $kernel = Invoke-DockerOutput -Context $Context -Arguments @('info', '--format', '{{.KernelVersion}}')
        $WindowsIsolation = if ($kernel -match '17763') { 'process' } else { 'hyperv' }
    }
    $dockerExecutable = (Get-Command docker.exe -ErrorAction Stop).Source
    if (-not [string]::IsNullOrWhiteSpace($env:ChocolateyInstall)) {
        $shimDirectory = Join-Path $env:ChocolateyInstall 'bin'
        if ([IO.Path]::GetDirectoryName($dockerExecutable) -eq $shimDirectory) {
            # A Chocolatey shim points outside the runner; copy its actual payload.
            $dockerExecutable = Join-Path $env:ChocolateyInstall 'lib/docker-cli/bin/docker.exe'
        }
    }
    Copy-Item -LiteralPath $dockerExecutable -Destination (Join-Path $contextDirectory 'docker.exe') -Force
    $buildArguments += @('--isolation', $WindowsIsolation)
    $engineEndpoint = Invoke-DockerOutput -Context $Context -Arguments @('context', 'inspect', $Context, '--format', '{{.Endpoints.docker.Host}}')
    $pipeMatch = [regex]::Match($engineEndpoint, '^npipe:/{4}\./pipe/(?<name>.+)$')
    if (-not $pipeMatch.Success) { throw "Daemon tests require a local named-pipe endpoint, found $engineEndpoint" }
    $pipeSource = '\\.\pipe\' + $pipeMatch.Groups['name'].Value
    $runArguments += @('--isolation', $WindowsIsolation, '--mount', "type=npipe,source=$pipeSource,target=$pipeSource")
    $runArguments += @('--env', "UNITY_DAEMON_TEST_ISOLATION=$WindowsIsolation")
    $fixtureRoot = $fixtureRoot.Replace('/', '\')
    $command = @('cmd.exe', '/S', '/C', ("docker context create windows --docker host=$engineEndpoint && " + $testCommand.Replace('"', '')))
} else {
    # The fixture publishes its HTTP port on the daemon host's loopback.
    $runArguments += @('--network', 'host', '--mount', 'type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock')
    $command = @('sh', '-c', ('docker context create linux --docker host=unix:///var/run/docker.sock && ' + $testCommand))
}

$dockerfile = Join-Path $PSScriptRoot "DaemonTests/Dockerfile.$os"
$completeBuildArguments = @('--context', $Context) + $buildArguments + @('--file', $dockerfile, '--tag', $image, $contextDirectory)
& docker @completeBuildArguments
if ($LASTEXITCODE -ne 0) { throw 'Daemon-test SDK runner build failed' }
$runArguments += @(
    '--mount', "type=bind,source=$RepositoryHostPath,target=$containerRepository",
    '--workdir', $containerRepository,
    '--env', "UNITY_DAEMON_TEST_REQUIRED_OS=$os",
    '--env', "UNITY_DAEMON_TEST_PROJECT_ROOT=$fixtureRoot",
    $image
)
$completeRunArguments = @('--context', $Context) + $runArguments + $command
& docker @completeRunArguments
if ($LASTEXITCODE -ne 0) { throw "Required $os daemon coverage failed" }
if (-not (Test-Path -LiteralPath $resultsPath)) { throw "Daemon tests produced no fresh JUnit report: $resultsPath" }
[xml] $report = Get-Content -LiteralPath $resultsPath -Raw
$requiredCases = @($report.SelectNodes('//testcase') | Where-Object { $_.classname -like "*DockerDaemonTests(*$os*$os*)" })
if ($requiredCases.Count -eq 0) { throw "No $os daemon tests were reported" }
foreach ($testCase in $requiredCases) {
    if ($null -ne $testCase.SelectSingleNode('skipped|failure|error')) {
        throw "Required $os daemon test did not pass: $($testCase.name)"
    }
}
Write-Output "All $($requiredCases.Count) required $os daemon tests passed."
