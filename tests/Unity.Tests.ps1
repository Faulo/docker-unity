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

    It 'does not expose shared MCP credential mutation' {
        $containerName = "unity-mcp-contract-$([guid]::NewGuid().ToString('N'))"
        $dockerMount = if ($Os -eq 'windows') {
            'type=npipe,source=\\.\pipe\docker_engine,target=\\.\pipe\docker_engine'
        } else {
            'type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock'
        }

        try {
            Invoke-Docker -Context $Context -RunArguments $DockerRunArguments -Arguments @(
                'run', '--detach', '--name', $containerName,
                '--env', 'UNITY_MCP=1',
                '--mount', $dockerMount,
                $Image
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
                "the MCP sidecar must become healthy; output: $($healthResult.Output | Out-String)"
            )

            $response = Invoke-DockerOutput -Context $Context -Arguments @(
                'container', 'exec', $containerName,
                'curl', '--silent', '--show-error', '--fail',
                '--request', 'POST',
                '--header', 'Content-Type: application/json',
                '--header', 'Accept: application/json, text/event-stream',
                '--data', '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}',
                'http://127.0.0.1:8080/mcp'
            )
            $response | Should -Match '"tools"'
            $response | Should -Not -Match 'configure_credentials'
        } finally {
            Get-DockerCommandResult -Context $Context -Arguments @(
                'container', 'rm', '--force', $containerName
            ) | Out-Null
        }
    }

    It 'reads a project through a real Docker daemon probe' {
        $id = [guid]::NewGuid().ToString('N')
        $seed = "unity-project-seed-$id"
        $controller = "unity-project-controller-$id"
        $volume = "unity-project-$id"
        $fixtureTarget = if ($Os -eq 'windows') { 'C:\fixture' } else { '/fixture' }
        $dockerMount = if ($Os -eq 'windows') {
            'type=npipe,source=\\.\pipe\docker_engine,target=\\.\pipe\docker_engine'
        } else {
            'type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock'
        }

        try {
            Invoke-Docker -Context $Context -RunArguments $DockerRunArguments -Arguments @(
                'run', '--detach', '--tty', '--name', $seed,
                '--mount', "type=volume,source=$volume,target=$fixtureTarget", $Image, $keeperCommand
            )
            $fixture = Join-Path $PSScriptRoot '../common/Unity.Tests/test-files/ValidProject'
            if ($Os -eq 'windows') {
                # Docker's Windows archive copy writes into the container layer;
                # extract inside the running container to populate its volume.
                $archive = Join-Path $TestDrive 'project.zip'
                Compress-Archive -Path (Join-Path $fixture '*') -DestinationPath $archive
                Invoke-Docker -Context $Context -Arguments @('cp', $archive, "${seed}:C:\Windows\Temp\project.zip")
                Invoke-Docker -Context $Context -Arguments @(
                    'exec', $seed, 'pwsh', '-NoProfile', '-Command',
                    '[IO.Compression.ZipFile]::ExtractToDirectory("C:\Windows\Temp\project.zip", "C:\fixture")'
                )
            } else {
                Invoke-Docker -Context $Context -Arguments @('cp', "$fixture/.", "${seed}:$fixtureTarget")
            }
            $seedConfig = Invoke-DockerOutput -Context $Context -Arguments @('inspect', $seed) | ConvertFrom-Json
            $projectRoot = @($seedConfig[0].Mounts | Where-Object { $_.Name -eq $volume })[0].Source

            Invoke-Docker -Context $Context -RunArguments $DockerRunArguments -Arguments @(
                'run', '--detach', '--name', $controller,
                '--env', 'UNITY_MCP=1', '--mount', $dockerMount, $Image
            )
            $healthResult = $null
            for ($attempt = 1; $attempt -le 30; $attempt++) {
                $healthResult = Get-DockerCommandResult -Context $Context -Arguments @(
                    'exec', $controller, 'unity-sidecar', 'health'
                )
                if ($healthResult.ExitCode -eq 0) { break }
                Start-Sleep -Seconds 1
            }
            $healthResult.ExitCode | Should -Be 0 -Because ($healthResult.Output | Out-String)

            $request = @{
                jsonrpc = '2.0'
                id = 1
                method = 'tools/call'
                params = @{
                    name = 'get_project_info'
                    arguments = @{ projectRoot = $projectRoot }
                }
            } | ConvertTo-Json -Depth 5 -Compress
            $response = Invoke-DockerOutput -Context $Context -Arguments @(
                'exec', $controller, 'curl', '--silent', '--show-error', '--fail',
                '--max-time', '120', '--request', 'POST',
                '--header', 'Content-Type: application/json',
                '--header', 'Accept: application/json, text/event-stream',
                '--data', $request, 'http://127.0.0.1:8080/mcp'
            )
            $events = @($response -split '\r?\n' | Where-Object { $_.StartsWith('data: ') })
            $events.Count | Should -Be 1 -Because $response
            $message = $events[0].Substring(6) | ConvertFrom-Json
            $message.result.PSObject.Properties['isError']?.Value | Should -Not -BeTrue -Because $response
            $project = $message.result.structuredContent.result
            $project.projectName | Should -BeExactly 'Example Game'
            $project.editor.version | Should -BeExactly '6000.3.13f1'
            $project.rendering.renderPipeline | Should -BeExactly 'Universal'
            $project.packages.custom | Should -BeExactly '1.2.3'
        } finally {
            Get-DockerCommandResult -Context $Context -Arguments @('rm', '--force', '--volumes', $controller, $seed) | Out-Null
            Get-DockerCommandResult -Context $Context -Arguments @('volume', 'rm', $volume) | Out-Null
        }
    }
}

Describe "Unity image identity and contents [$Os, $Image]" {
    It 'compiles and runs a .NET SDK application' {
        $command = if ($Os -eq 'windows') {
            @(
                'pwsh', '-NoLogo', '-NoProfile', '-Command',
                '$ErrorActionPreference = "Stop"; $project = Join-Path $env:TEMP "unity-sdk-smoke"; dotnet new console --output $project; if ($LASTEXITCODE -ne 0) { throw "SDK project creation failed" }; dotnet run --project $project --configuration Release; if ($LASTEXITCODE -ne 0) { throw "SDK build/run failed" }'
            )
        } else {
            @('sh', '-c', 'set -eu; dotnet new console --output /tmp/unity-sdk-smoke; dotnet run --project /tmp/unity-sdk-smoke --configuration Release')
        }
        $result = Get-DockerCommandResult -Context $Context -RunArguments $DockerRunArguments -Arguments (@(
            'run', '--rm', $Image
        ) + $command)
        $result.ExitCode | Should -Be 0 -Because ($result.Output | Out-String)
        ($result.Output | Out-String) | Should -Match 'Hello, World!'
    }

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
