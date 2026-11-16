[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester binds shared image data in separate scopes.')]
param(
    [string] $Namespace, [string] $Name, [string] $Variant,
    [string] $Context, [string] $Image, [string] $Os,
    [AllowEmptyCollection()] [string[]] $DockerRunArguments
)

BeforeAll {
    . (Join-Path $PSScriptRoot '../.jenkins/Docker.ps1')
}

Describe "Legacy Bee cold build [$Os, $Image]" {
    It 'builds a 2021.3 project with a C# assembly from a cold Library' {
        $container = "unity-bee-$([guid]::NewGuid().ToString('N'))"
        $project = if ($Os -eq 'windows') { 'C:/bee-project' } else { '/work/project' }
        $output = if ($Os -eq 'windows') { 'C:/bee-output' } else { '/work/output' }
        $keeper = if ($Os -eq 'windows') { 'cmd.exe' } else { 'cat' }
        try {
            # A private writable editor copy prevents compatibility preparation from
            # changing the shared CI editor cache during published/candidate comparisons.
            $editorSource = if ($Os -eq 'windows') { 'C:/source-editors' } else { '/source-editors' }
            $arguments = @('run', '--detach', '--interactive', '--no-healthcheck', '--name', $container,
                '--env', 'UNITY_CREDENTIALS_USR', '--env', 'UNITY_CREDENTIALS_PSW',
                '--env', 'EMAIL_CREDENTIALS_USR', '--env', 'EMAIL_CREDENTIALS_PSW',
                '--env', 'UNITY_NO_GRAPHICS=0', '--env', 'UNITY_CALL_TIMEOUT=300',
                '--mount', "type=volume,source=unity-binaries,target=$editorSource,readonly", $Image, $keeper)
            Invoke-Docker -Context $Context -Arguments $arguments
            if ($Os -eq 'windows') {
                Invoke-Docker -Context $Context -Arguments @('exec', $container, 'pwsh', '-NoProfile', '-Command',
                    '$ErrorActionPreference="Stop"; Copy-Item C:/source-editors/2021.3.45f2 "C:/Program Files/Unity/Hub/Editor/2021.3.45f2" -Recurse')
            } else {
                Invoke-Docker -Context $Context -Arguments @('exec', $container, 'sh', '-c',
                    'set -eu; mkdir -p /root/Unity/Hub/Editor /work; cp -a /source-editors/Hub/Editor/2021.3.45f2 /root/Unity/Hub/Editor/')
            }
            Invoke-Docker -Context $Context -Arguments @('cp',
                (Join-Path $PSScriptRoot 'bee-repro/fixtures/GameJam.Minimal'), "${container}:$project")
            $result = Get-DockerCommandResult -Context $Context -Arguments @(
                'exec', $container, 'unity', 'build', '--junit', '-', $project, $output, 'windows')
            $result.ExitCode | Should -Be 0 -Because ($result.Output | Out-String)
            $check = if ($Os -eq 'windows') {
                @('pwsh', '-NoProfile', '-Command', 'if (!(Test-Path C:/bee-output/BeeRepro.exe)) { exit 1 }')
            } else { @('test', '-s', '/work/output/BeeRepro.exe') }
            Invoke-Docker -Context $Context -Arguments (@('exec', $container) + $check)
        } finally {
            Get-DockerCommandResult -Context $Context -Arguments @('rm', '--force', '--volumes', $container) | Out-Null
        }
    }
}

Describe "Linux invocation cleanup [$Image]" -Skip:($Os -ne 'linux') {
    It 'kills detached compiler children after the root exits without touching another call' {
        $container = "unity-tree-$([guid]::NewGuid().ToString('N'))"
        try {
            Invoke-Docker -Context $Context -Arguments @('run', '--detach', '--interactive', '--no-healthcheck',
                '--name', $container, $Image, 'cat')
            Invoke-Docker -Context $Context -Arguments @('cp',
                (Join-Path $PSScriptRoot 'ProcessFixtures'), "${container}:/process-fixtures")
            $result = Get-DockerCommandResult -Context $Context -Arguments @(
                'exec', $container, 'python3', '/process-fixtures/cancellation.py')
            $result.ExitCode | Should -Be 0 -Because ($result.Output | Out-String)
        } finally {
            Get-DockerCommandResult -Context $Context -Arguments @('rm', '--force', '--volumes', $container) | Out-Null
        }
    }
}
