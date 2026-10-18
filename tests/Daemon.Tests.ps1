param(
    [string] $Namespace,
    [string] $Name,
    [string] $Variant,
    [string] $Context,
    [string] $Image,
    [string] $Os,
    [string[]] $DockerRunArguments
)

Describe "Docker MCP daemon contract [$Os, $Image]" -Tag 'DockerDaemon' {
    BeforeAll {
        . (Join-Path $PSScriptRoot '../.jenkins/Docker.ps1')
        $id = [guid]::NewGuid().ToString('N')
        $fixtureImage = "tmp/unity-daemon-fixture:$id"
        $baseAlias = "tmp/unity-daemon-base:$id"
        $controller = "unity-daemon-controller-$id"
        $seed = "unity-daemon-seed-$id"
        $volume = "unity-daemon-project-$id"
        $controllerId = $null
        $endpoint = 'http://127.0.0.1:8080/mcp'
        $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

        function ConvertFrom-DaemonEvent {
            param([string] $Text)
            $events = @($Text -split '\r?\n' | Where-Object { $_.StartsWith('data: ') } |
                ForEach-Object { $_.Substring(6) | ConvertFrom-Json })
            $events.Count | Should -BeGreaterThan 0 -Because $Text
            return $events
        }

        function Invoke-DaemonRequestAsync {
            param([string] $Method, [hashtable] $Parameters, [hashtable] $Headers = @{})
            $body = @{
                jsonrpc = '2.0'
                id = [guid]::NewGuid().ToString('N')
                method = $Method
                params = $Parameters
            } | ConvertTo-Json -Depth 12 -Compress
            $arguments = @(
                '--context', $Context, 'exec', $controller,
                'curl', '--silent', '--show-error', '--fail', '--no-buffer',
                '--max-time', '300', '--noproxy', '127.0.0.1',
                '--header', 'Content-Type: application/json',
                '--header', 'Accept: application/json, text/event-stream'
            )
            foreach ($key in $Headers.Keys) {
                # curl needs a semicolon to transmit an explicitly empty header.
                $header = if ($Headers[$key] -eq '') { $key + ';' } else { $key + ': ' + $Headers[$key] }
                $arguments += @('--header', $header)
            }
            $arguments += @('--data', $body, $endpoint)
            $startInfo = [Diagnostics.ProcessStartInfo]::new((Get-Command docker -ErrorAction Stop).Source)
            $startInfo.UseShellExecute = $false
            $startInfo.CreateNoWindow = $true
            $startInfo.RedirectStandardOutput = $true
            $startInfo.RedirectStandardError = $true
            foreach ($argument in $arguments) { $startInfo.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::Start($startInfo)
            return @{
                Process = $process
                Output = $process.StandardOutput.ReadToEndAsync()
                Error = $process.StandardError.ReadToEndAsync()
            }
        }

        function Complete-DaemonRequest {
            param([hashtable] $Pending)
            try {
                if (-not $Pending.Process.WaitForExit(310000)) {
                    $Pending.Process.Kill($true)
                    throw 'The MCP request exceeded its deadline.'
                }
                $output = $Pending.Output.GetAwaiter().GetResult()
                $errorOutput = $Pending.Error.GetAwaiter().GetResult()
                $Pending.Process.ExitCode | Should -Be 0 -Because $errorOutput
                return ConvertFrom-DaemonEvent -Text $output
            } finally {
                if (-not $Pending.Process.HasExited) { $Pending.Process.Kill($true) }
                $Pending.Process.Dispose()
                $Pending.Process = $null
            }
        }

        function Invoke-DaemonRequest {
            param([string] $Method, [hashtable] $Parameters, [hashtable] $Headers = @{})
            $pending = Invoke-DaemonRequestAsync -Method $Method -Parameters $Parameters -Headers $Headers
            return Complete-DaemonRequest -Pending $pending
        }

        function Get-DaemonToolResult {
            param($Response)
            $Response.result.PSObject.Properties['isError']?.Value | Should -Not -BeTrue -Because ($Response | ConvertTo-Json -Depth 12)
            return $Response.result.structuredContent.result
        }

        function Invoke-DaemonTool {
            param([string] $Tool, [hashtable] $Arguments, [hashtable] $Headers = @{})
            $events = @(Invoke-DaemonRequest -Method 'tools/call' -Parameters @{ name = $Tool; arguments = $Arguments } -Headers $Headers)
            $responses = @($events | Where-Object { $null -ne $_.PSObject.Properties['id']?.Value })
            $responses.Count | Should -Be 1
            return Get-DaemonToolResult -Response $responses[0]
        }

        function Get-TestCredentialHeader {
            param([string] $Prefix)
            return @{
                'X-Unity-Credentials-Usr' = "$Prefix-unity-user"
                'X-Unity-Credentials-Psw' = "$Prefix-unity-password"
                'X-Email-Credentials-Usr' = "$Prefix-email-user"
                'X-Email-Credentials-Psw' = "$Prefix-email-password"
            }
        }

        function Invoke-TestMethod {
            param([string] $Method, [hashtable] $Headers = @{}, [string] $Root = $projectRoot)
            return Invoke-DaemonTool -Tool 'execute_method' -Arguments @{
                projectRoot = $Root; method = $Method; arguments = @()
            } -Headers $Headers
        }

        function Get-SingleDaemonWorker {
            $workers = @((Invoke-DockerOutput -Context $Context -Arguments @(
                'ps', '--all', '--quiet', '--no-trunc',
                '--filter', 'label=net.slothsoft.unity.kind=worker',
                '--filter', "label=net.slothsoft.unity.controller=$controllerId"
            )) -split '\r?\n' | Where-Object { $_ })
            $workers.Count | Should -Be 1
            return $workers[0]
        }

        function Invoke-DaemonHttp {
            param([string] $Path, [string[]] $Arguments = @())
            # Requests use loopback; a literal host avoids native wildcard expansion on Unix.
            return Invoke-DockerOutput -Context $Context -Arguments (@(
                'exec', $controller, 'curl', '--silent', '--show-error', '--fail',
                '--max-time', '60', '--noproxy', '127.0.0.1'
            ) + $Arguments + @('http://127.0.0.1:8080' + $Path))
        }

        # Replace only the external command backend. The native launcher,
        # sidecar, Docker client, probes and workers come from the tested image.
        Invoke-Docker -Context $Context -Arguments @('image', 'tag', $Image, $baseAlias)
        $buildArguments = @(
            'build', '--file', (Join-Path $PSScriptRoot "DaemonFixtures/Dockerfile.$Os"),
            '--tag', $fixtureImage, '--build-arg', "BASE_IMAGE=$baseAlias"
        )
        if ($Os -eq 'windows') {
            $kernel = Invoke-DockerOutput -Context $Context -Arguments @('info', '--format', '{{.KernelVersion}}')
            $isolation = if ($kernel -match '17763') { 'process' } else { 'hyperv' }
            $buildArguments += @('--isolation', $isolation)
        }
        Invoke-Docker -Context $Context -Arguments ($buildArguments + @($repository))

        $fixtureTarget = if ($Os -eq 'windows') { 'C:\fixture' } else { '/fixture' }
        $keeper = if ($Os -eq 'windows') { 'cmd.exe' } else { 'cat' }
        $fixtureRunArguments = @()
        if ($Os -eq 'windows') { $fixtureRunArguments = @('--isolation', $isolation) }
        Invoke-Docker -Context $Context -RunArguments $fixtureRunArguments -Arguments @(
            'run', '--detach', '--tty', '--name', $seed,
            '--mount', "type=volume,source=$volume,target=$fixtureTarget", $fixtureImage, $keeper
        )
        $project = Join-Path $PSScriptRoot '../common/Unity.Tests/test-files/ValidProject'
        if ($Os -eq 'windows') {
            $archive = Join-Path $TestDrive 'daemon-project.zip'
            Compress-Archive -Path (Join-Path $project '*') -DestinationPath $archive
            Invoke-Docker -Context $Context -Arguments @('cp', $archive, ($seed + ':C:\Windows\Temp\project.zip'))
            Invoke-Docker -Context $Context -Arguments @(
                'exec', $seed, 'pwsh', '-NoProfile', '-Command',
                '[IO.Compression.ZipFile]::ExtractToDirectory("C:\Windows\Temp\project.zip", "C:\fixture")'
            )
        } else {
            Invoke-Docker -Context $Context -Arguments @('cp', "$project/.", ($seed + ':' + $fixtureTarget))
        }
        $seedConfiguration = Invoke-DockerOutput -Context $Context -Arguments @('inspect', $seed) | ConvertFrom-Json
        $projectRoot = @($seedConfiguration[0].Mounts | Where-Object { $_.Name -eq $volume })[0].Source
        $dockerMount = if ($Os -eq 'windows') {
            'type=npipe,source=\\.\pipe\docker_engine,target=\\.\pipe\docker_engine'
        } else {
            'type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock'
        }
        Invoke-Docker -Context $Context -RunArguments $fixtureRunArguments -Arguments @(
            'run', '--detach', '--name', $controller, '--env', 'UNITY_MCP=1',
            '--env', 'UNITY_CREDENTIALS_USR=daemon-unity-user',
            '--env', 'UNITY_CREDENTIALS_PSW=daemon-unity-password',
            '--env', 'EMAIL_CREDENTIALS_USR=daemon-email-user',
            '--env', 'EMAIL_CREDENTIALS_PSW=daemon-email-password',
            '--mount', $dockerMount, $fixtureImage
        )
        $controllerId = Invoke-DockerOutput -Context $Context -Arguments @('inspect', '--format', '{{.Id}}', $controller)
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            $health = Get-DockerCommandResult -Context $Context -Arguments @('exec', $controller, 'unity-sidecar', 'health')
            if ($health.ExitCode -eq 0) { $ready = $true; break }
            Start-Sleep -Seconds 1
        }
        $ready | Should -BeTrue -Because ($health.Output | Out-String)
        $initialization = @(Invoke-DaemonRequest -Method 'initialize' -Parameters @{
            protocolVersion = '2025-06-18'
            capabilities = @{}
            clientInfo = @{ name = 'unity-pester'; version = '1' }
        })
        $initialization[0].result.serverInfo.name | Should -BeExactly 'unity'
    }

    AfterAll {
        if ($controllerId) {
            $owned = Get-DockerCommandResult -Context $Context -Arguments @(
                'ps', '--all', '--quiet', '--no-trunc', '--filter', "label=net.slothsoft.unity.controller=$controllerId"
            )
            foreach ($ownedContainer in @($owned.Output | Where-Object { $_ -match '^[a-f0-9]{12,64}$' })) {
                Get-DockerCommandResult -Context $Context -Arguments @('rm', '--force', '--volumes', $ownedContainer) | Out-Null
            }
        }
        Get-DockerCommandResult -Context $Context -Arguments @('rm', '--force', '--volumes', $controller, $seed) | Out-Null
        Get-DockerCommandResult -Context $Context -Arguments @('volume', 'rm', $volume) | Out-Null
        Get-DockerCommandResult -Context $Context -Arguments @('image', 'rm', $fixtureImage, $baseAlias) | Out-Null
    }

    It 'advertises the tools and reads project metadata through a Docker probe' {
        $tools = @(Invoke-DaemonRequest -Method 'tools/list' -Parameters @{})
        (@($tools[0].result.tools.name | Sort-Object) -join ',') | Should -BeExactly 'build_and_serve_webgl,execute_method,get_project_info,run_tests'
        $webGl = @($tools[0].result.tools | Where-Object name -eq 'build_and_serve_webgl')[0]
        @($webGl.inputSchema.properties.PSObject.Properties.Name) | Should -Be @('projectRoot')
        $project = Invoke-DaemonTool -Tool 'get_project_info' -Arguments @{ projectRoot = $projectRoot }
        $project.projectName | Should -BeExactly 'Example Game'
        $project.editor.version | Should -BeExactly '6000.3.13f1'
        $project.rendering.renderPipeline | Should -BeExactly 'Universal'
        $project.packages.custom | Should -BeExactly '1.2.3'
    }

    It 'preserves method arguments, stdout, stderr and exit status' {
        $arguments = @('', 'two words', '--', '"quoted"')
        $result = Invoke-DaemonTool -Tool 'execute_method' -Arguments @{
            projectRoot = $projectRoot; method = 'DaemonTests.Arguments'; arguments = $arguments
        }
        $result.exitStatus | Should -Be 7
        $result.errorOutput | Should -BeExactly 'daemon-test stderr'
        $backend = $result.output | ConvertFrom-Json
        $backend.method | Should -BeExactly 'DaemonTests.Arguments'
        (ConvertTo-Json -InputObject @($backend.arguments) -Compress) | Should -BeExactly (ConvertTo-Json -InputObject $arguments -Compress)
        $backend.credentials.unity | Should -BeTrue
        $backend.credentials.email | Should -BeTrue
    }

    It 'isolates simultaneous client credentials while reusing one worker' {
        Invoke-TestMethod -Method 'DaemonTests.Fallback' | Out-Null
        $worker = Get-SingleDaemonWorker
        $pending = @()
        try {
            foreach ($client in @('A', 'B')) {
                $pending += Invoke-DaemonRequestAsync -Method 'tools/call' -Parameters @{
                    name = 'execute_method'
                    arguments = @{ projectRoot = $projectRoot; method = "DaemonTests.Client$client"; arguments = @() }
                } -Headers (Get-TestCredentialHeader -Prefix ('client-' + $client.ToLowerInvariant()))
            }
            foreach ($request in $pending) {
                $events = @(Complete-DaemonRequest -Pending $request)
                $result = Get-DaemonToolResult -Response $events[-1]
                $backend = $result.output | ConvertFrom-Json
                $backend.credentials.unity | Should -BeTrue
                $backend.credentials.email | Should -BeTrue
            }
        } finally {
            foreach ($request in $pending) {
                if ($null -ne $request.Process) {
                    if (-not $request.Process.HasExited) { $request.Process.Kill($true) }
                    $request.Process.Dispose()
                }
            }
        }
        (Get-SingleDaemonWorker) | Should -BeExactly $worker
    }

    It 'accepts an incomplete request credential set without restoring the empty field' {
        $result = Invoke-TestMethod -Method 'DaemonTests.Partial' -Headers @{ 'X-Unity-Credentials-Usr' = '' }
        $backend = $result.output | ConvertFrom-Json
        $backend.credentials.unity | Should -BeFalse
        $backend.credentials.email | Should -BeTrue
    }

    It 'streams correlated increasing progress before the final result' {
        $token = "pester-progress-$Os"
        $events = @(Invoke-DaemonRequest -Method 'tools/call' -Parameters @{
            name = 'execute_method'
            arguments = @{ projectRoot = $projectRoot; method = 'DaemonTests.Progress'; arguments = @() }
            _meta = @{ progressToken = $token }
        })
        $progress = @($events | Where-Object { $_.PSObject.Properties['method']?.Value -eq 'notifications/progress' })
        $progress.Count | Should -BeGreaterThan 0
        $events[-1].PSObject.Properties['id']?.Value | Should -Not -BeNullOrEmpty
        $previous = -1
        foreach ($notification in $progress) {
            $notification.params.progressToken | Should -BeExactly $token
            $notification.params.progress | Should -BeGreaterThan $previous
            $notification.params.PSObject.Properties['total']?.Value | Should -BeNullOrEmpty
            $previous = $notification.params.progress
        }
        $result = Get-DaemonToolResult -Response $events[-1]
        $result.exitStatus | Should -Be 7
        $result.errorOutput | Should -BeExactly 'daemon-test stderr'
        ($result.output | ConvertFrom-Json).method | Should -BeExactly 'DaemonTests.Progress'
    }

    It 'preserves the native launcher working directory and Composer configuration' {
        $workingDirectory = if ($Os -eq 'windows') { 'C:\Windows\Temp' } else { '/tmp' }
        $result = Get-DockerCommandResult -Context $Context -Arguments @(
            'exec', '--workdir', $workingDirectory, $controller, 'unity',
            'method', $workingDirectory, 'DaemonTests.Arguments', '--'
        )
        $result.ExitCode | Should -Be 7 -Because ($result.Output | Out-String)
        $json = @($result.Output | Where-Object { $_ -is [string] -and $_.StartsWith('{') })
        $json.Count | Should -Be 1
        $backend = $json[0] | ConvertFrom-Json
        $backend.workingDirectory | Should -BeExactly $workingDirectory
        $backend.composerProject | Should -BeExactly $(if ($Os -eq 'windows') { 'C:\unity\composer.json' } else { '/unity/composer.json' })
        $backend.composerVendorDirectory | Should -BeExactly $(if ($Os -eq 'windows') { 'C:\unity\vendor' } else { '/unity/vendor' })
        $backend.credentials.unity | Should -BeTrue
        $backend.credentials.email | Should -BeTrue
    }

    It 'reuses normalized project workers and mounts only the project directories' {
        Invoke-TestMethod -Method 'DaemonTests.First' | Out-Null
        $worker = Get-SingleDaemonWorker
        $project = Invoke-DaemonTool -Tool 'get_project_info' -Arguments @{ projectRoot = $projectRoot }
        Invoke-TestMethod -Method 'DaemonTests.Reuse' -Root $project.projectRoot | Out-Null
        (Get-SingleDaemonWorker) | Should -BeExactly $worker
        $mounts = Invoke-DockerOutput -Context $Context -Arguments @('inspect', '--format', '{{json .Mounts}}', $worker) | ConvertFrom-Json
        @($mounts | Where-Object { $_.Destination -match '(?:^|[\\/])(Assets|Packages|ProjectSettings)$' }).Count | Should -Be 3
        @($mounts | Where-Object { $_.Destination -in @('/var/run/docker.sock', '\\.\pipe\docker_engine') }).Count | Should -Be 0
        $configuration = Invoke-DockerOutput -Context $Context -Arguments @('inspect', '--format', '{{json .Config}}', $worker)
        $workerLabels = (Invoke-DockerOutput -Context $Context -Arguments @('inspect', '--format', '{{json .Config.Labels}}', $worker)) | ConvertFrom-Json
        $workerLabels.'net.slothsoft.unity.worker-configuration' | Should -Match '^[a-f0-9]{64}$'
        foreach ($value in @('daemon-unity-user', 'daemon-unity-password', 'daemon-email-user', 'daemon-email-password')) {
            $configuration | Should -Not -Match ([regex]::Escape($value))
        }
    }

    It 'keeps request credentials out of persistent controller configuration' {
        foreach ($client in @('a', 'b')) {
            Invoke-TestMethod -Method ('DaemonTests.Client' + $client.ToUpperInvariant()) -Headers (Get-TestCredentialHeader -Prefix "client-$client") | Out-Null
        }
        $configuration = Invoke-DockerOutput -Context $Context -Arguments @('inspect', '--format', '{{json .Config}}', $controller)
        foreach ($client in @('a', 'b')) {
            foreach ($suffix in @('unity-user', 'unity-password', 'email-user', 'email-password')) {
                $configuration | Should -Not -Match ([regex]::Escape("client-$client-$suffix"))
            }
        }
    }

    It 'parses test reports returned by the worker' {
        $result = Invoke-DaemonTool -Tool 'run_tests' -Arguments @{ projectRoot = $projectRoot; modes = @('EditMode', 'Play Mode') }
        $result.outcome | Should -BeExactly 'passed'
        $result.counts.total | Should -Be 2
        $result.counts.passed | Should -Be 2
    }

    It 'builds and serves WebGL listings, headers, compressed assets and byte ranges' {
        $result = Invoke-DaemonTool -Tool 'build_and_serve_webgl' -Arguments @{ projectRoot = $projectRoot }
        $result.projectSlug | Should -BeExactly 'example-game'
        $result.buildId | Should -Match '^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}Z$'
        $result.path | Should -BeExactly ("/webgl/example-game/" + $result.buildId + '/')
        $result.url | Should -BeLike 'http://127.0.0.1:8080/webgl/example-game/*'
        (Invoke-DaemonHttp -Path '/webgl/') | Should -Match 'example-game'
        (Invoke-DaemonHttp -Path '/webgl/example-game/') | Should -Match ([regex]::Escape($result.buildId))
        $index = Invoke-DaemonHttp -Path $result.path -Arguments @('--include')
        $index | Should -Match 'Daemon WebGL Build'
        $index | Should -Match '(?im)^Cross-Origin-Opener-Policy: same-origin\r?$'
        $index | Should -Match '(?im)^Cross-Origin-Embedder-Policy: require-corp\r?$'
        $index | Should -Match '(?im)^Cross-Origin-Resource-Policy: cross-origin\r?$'
        $encoded = Invoke-DaemonHttp -Path ($result.path + 'Build/game.wasm.br') -Arguments @('--head')
        $encoded | Should -Match '(?im)^Content-Type: application/wasm(?:;[^\r\n]*)?\r?$'
        $encoded | Should -Match '(?im)^Content-Encoding: br\r?$'
        $range = Invoke-DaemonHttp -Path ($result.path + 'Build/game.data') -Arguments @('--include', '--header', 'Range: bytes=2-5')
        $range | Should -Match '^HTTP/1\.[01] 206'
        @($range -split '\r?\n\r?\n', 2)[1].Trim() | Should -BeExactly '2345'
    }
}
