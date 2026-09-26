param(
    [Parameter(Mandatory)]
    [string] $Namespace,

    [Parameter(Mandatory)]
    [string] $Name,

    [Parameter(Mandatory)]
    [string] $Variant,

    [Parameter(Mandatory)]
    [string] $Context,

    [Parameter(Mandatory)]
    [string] $Image,

    [Parameter(Mandatory)]
    [string] $Os,

    [Parameter(Mandatory)]
    [bool] $Pull,

    [Parameter(Mandatory)]
    [AllowEmptyCollection()]
    [string[]] $DockerRunArguments
)

BeforeDiscovery {
    $unityVersions = @(
        '2019.4.41f2'
        '2020.3.49f1'
        '2021.3.45f2'
        '2022.3.62f3'
        '6000.0.81f1'
    )
}

BeforeAll {
    . (Join-Path $PSScriptRoot '../.jenkins/Docker.ps1')
}

Describe "Docker image command contract [$Os, $Image]" {
    BeforeAll {
        $imageConfig = Invoke-DockerOutput -Context $Context -Arguments @(
            'image', 'inspect', '--format', '{{json .Config}}', $Image
        ) | ConvertFrom-Json
        $entrypoint = @()
        $entrypointProperty = $imageConfig.PSObject.Properties['Entrypoint']
        if ($null -ne $entrypointProperty) {
            $entrypoint = @($entrypointProperty.Value)
        }
        $defaultCommand = @()
        $commandProperty = $imageConfig.PSObject.Properties['Cmd']
        if ($null -ne $commandProperty) {
            $defaultCommand = @($commandProperty.Value)
        }
        $keeperCommand = if ($Os -eq 'windows') { 'cmd.exe' } else { 'cat' }
        $foreignCommand = if ($Os -eq 'windows') {
            @('cmd.exe', '/S', '/C', 'exit 0')
        } else {
            @('sh', '-c', 'exit 0')
        }
    }

    It 'does not declare an entrypoint' {
        $entrypoint.Count | Should -Be 0
    }

    It 'declares the exact default sidecar command' {
        $defaultCommand.Count | Should -Be 1
        $defaultCommand[0] | Should -BeExactly 'unity-sidecar'
    }

    It 'allows an arbitrary command to replace the default command' {
        Invoke-Docker -Context $Context -RunArguments $DockerRunArguments -Arguments (@(
            'run', '--rm', $Image
        ) + $foreignCommand)
    }

    It 'keeps the Docker Pipeline keeper running' {
        $containerName = "unity-keeper-$([guid]::NewGuid().ToString('N'))"

        try {
            Invoke-Docker -Context $Context -RunArguments $DockerRunArguments -Arguments @(
                'run', '--detach', '--tty', '--name', $containerName, $Image, $keeperCommand
            )

            $running = Invoke-DockerOutput -Context $Context -Arguments @(
                'container', 'inspect', '--format', '{{.State.Running}}', $containerName
            )
            $running | Should -BeExactly 'true'
        } finally {
            Get-DockerCommandResult -Context $Context -Arguments @(
                'container', 'rm', '--force', $containerName
            ) | Out-Null
        }
    }

    It 'starts the sidecar when no command is supplied' {
        $containerName = "unity-sidecar-$([guid]::NewGuid().ToString('N'))"

        try {
            Invoke-Docker -Context $Context -RunArguments $DockerRunArguments -Arguments @(
                'run', '--detach', '--name', $containerName, $Image
            )

            $healthResult = $null
            for ($attempt = 1; $attempt -le 30; $attempt++) {
                $healthResult = Get-DockerCommandResult -Context $Context -Arguments @(
                    'container', 'exec', $containerName, 'unity-sidecar', 'health'
                )
                if ($healthResult.ExitCode -eq 0) {
                    break
                }
                Start-Sleep -Seconds 1
            }

            $healthResult.ExitCode | Should -Be 0 -Because (
                "the default sidecar must become healthy; output: $($healthResult.Output | Out-String)"
            )
        } finally {
            Get-DockerCommandResult -Context $Context -Arguments @(
                'container', 'rm', '--force', $containerName
            ) | Out-Null
        }
    }
}

Describe "Unity image identity and contents [$Os, $Image]" {
    It 'provides PHP 8.4' {
        $result = Get-DockerCommandResult -Context $Context -RunArguments $DockerRunArguments -Arguments @(
            'run', '--rm', $Image,
            'php', '-r', 'echo PHP_MAJOR_VERSION, ".", PHP_MINOR_VERSION;'
        )
        $result.ExitCode | Should -Be 0 -Because ($result.Output | Out-String)
        ($result.Output | Out-String).Trim() | Should -BeExactly '8.4'
    }

    It 'provides the public Unity commands' {
        $command = if ($Os -eq 'windows') {
            @('cmd.exe', '/S', '/C', 'where unity.exe && where unity-sidecar.exe')
        } else {
            @('sh', '-c', 'command -v unity && command -v unity-sidecar')
        }

        Invoke-Docker -Context $Context -RunArguments $DockerRunArguments -Arguments (@(
            'run', '--rm', $Image
        ) + $command)
    }

    It 'does not expose compatibility-line commands or removed tools' {
        $command = if ($Os -eq 'windows') {
            @(
                'pwsh', '-NoLogo', '-NoProfile', '-Command',
                '$names = @("compose-unity", "compose-unity-sidecar", "butler", "steamcmd", "node", "npm"); foreach ($name in $names) { if (Get-Command $name -ErrorAction SilentlyContinue) { throw "$name must not be installed" } }'
            )
        } else {
            @('sh', '-c', 'for name in compose-unity compose-unity-sidecar butler steamcmd node npm; do ! command -v "$name" || exit 1; done')
        }

        Invoke-Docker -Context $Context -RunArguments $DockerRunArguments -Arguments (@(
            'run', '--rm', $Image
        ) + $command)
    }

    It 'does not publish compatibility-line configuration or Steam volumes' {
        $imageConfig = Invoke-DockerOutput -Context $Context -Arguments @(
            'image', 'inspect', '--format', '{{json .Config}}', $Image
        ) | ConvertFrom-Json
        @($imageConfig.Env | Where-Object { $_ -like 'COMPOSE_UNITY_*' }).Count | Should -Be 0
        @($imageConfig.Volumes.PSObject.Properties.Name | Where-Object { $_ -match '(?i)steam' }).Count | Should -Be 0
        $imageConfig.WorkingDir | Should -Not -Match '(?i)compose-unity'
    }
}

Describe "Unity behavior [$Os, $Image]" {
    Context 'with Unity <UnityVersion>' -ForEach @(
        $unityVersions | ForEach-Object { @{ UnityVersion = $_ } }
    ) {
        It 'runs the empty project tests' {
            Invoke-Docker -Context $Context -RunArguments $DockerRunArguments -Arguments @(
                'run', '--rm', $Image,
                'unity', 'empty-project', 'test', $UnityVersion
            )
        }

        It 'runs the empty project tests with GPU acceleration' -Skip:($Os -ne 'windows') {
            $gpuArguments = @(
                '--isolation', 'process'
                '--device', 'class/5B45201D-F2F2-4F3B-85BB-30FF1F953599'
                '--env', 'UNITY_NO_GRAPHICS=0'
            )

            Invoke-Docker -Context $Context -RunArguments ($DockerRunArguments + $gpuArguments) -Arguments @(
                'run', '--rm', $Image,
                'unity', 'empty-project', 'test', $UnityVersion
            )
        }
    }
}
