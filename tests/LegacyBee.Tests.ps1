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
            # Linux prepares a private editor copy instead of changing the shared cache.
            $runArguments = if ($Os -eq 'windows') {
                $DockerRunArguments
            } else {
                @('--env', 'UNITY_CREDENTIALS_USR', '--env', 'UNITY_CREDENTIALS_PSW',
                    '--env', 'EMAIL_CREDENTIALS_USR', '--env', 'EMAIL_CREDENTIALS_PSW',
                    '--mount', 'type=volume,source=unity-binaries,target=/source-editors,readonly')
            }
            $arguments = @('run', '--detach', '--interactive', '--no-healthcheck', '--name', $container,
                '--env', 'UNITY_NO_GRAPHICS=0', '--env', 'UNITY_CALL_TIMEOUT=300', $Image, $keeper)
            Invoke-Docker -Context $Context -RunArguments $runArguments -Arguments $arguments
            if ($Os -eq 'linux') {
                Invoke-Docker -Context $Context -Arguments @('exec', $container, 'sh', '-c',
                    'set -eu; mkdir -p /root/Unity/Hub/Editor /work; cp -a /source-editors/Hub/Editor/2021.3.45f2 /root/Unity/Hub/Editor/')
                Invoke-Docker -Context $Context -Arguments @('cp',
                    (Join-Path $PSScriptRoot 'bee-repro/fixtures/GameJam.Minimal'), "${container}:$project")
            } else {
                $archive = Join-Path $TestDrive 'bee-project.zip'
                Compress-Archive -Path (Join-Path $PSScriptRoot 'bee-repro/fixtures/GameJam.Minimal/*') -DestinationPath $archive
                Invoke-Docker -Context $Context -Arguments @('cp', $archive, "${container}:C:/bee-project.zip")
                Invoke-Docker -Context $Context -Arguments @('exec', $container, 'pwsh', '-NoProfile', '-Command',
                    'Expand-Archive -LiteralPath C:/bee-project.zip -DestinationPath C:/bee-project -ErrorAction Stop')
            }
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
            Invoke-Docker -Context $Context -Arguments @('run', '--detach', '--interactive', '--init', '--no-healthcheck',
                '--name', $container, $Image, 'cat')
            Invoke-Docker -Context $Context -Arguments @('cp',
                (Join-Path $PSScriptRoot 'ProcessFixtures'), "${container}:/process-fixtures")
            $result = Get-DockerCommandResult -Context $Context -Arguments @(
                'exec', $container, 'python3', '-u', '/process-fixtures/cancellation.py')
            $result.ExitCode | Should -Be 0 -Because ($result.Output | Out-String)
        } finally {
            Get-DockerCommandResult -Context $Context -Arguments @('rm', '--force', '--volumes', $container) | Out-Null
        }
    }
}
