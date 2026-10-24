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
    $editorConfigs = @(
        @{ Label = 'unconfigured opt-in rule'; Rules = 'dotnet_diagnostic.UNT0033.severity = none'; Severity = 'info'; Expected = @() }
        @{ Label = 'explicitly disabled rule'; Rules = "dotnet_diagnostic.UNT0021.severity = none`ndotnet_diagnostic.UNT0033.severity = none"; Severity = 'info'; Expected = @() }
        @{ Label = 'warning opt-in'; Rules = "dotnet_diagnostic.UNT0021.severity = warning`ndotnet_diagnostic.UNT0033.severity = none"; Severity = 'warn'; Expected = @('UNT0021') }
        @{ Label = 'error opt-in'; Rules = "dotnet_diagnostic.UNT0021.severity = error`ndotnet_diagnostic.UNT0033.severity = none"; Severity = 'warn'; Expected = @('UNT0021') }
        @{ Label = 'suggestion below default threshold'; Rules = "dotnet_diagnostic.UNT0021.severity = suggestion`ndotnet_diagnostic.UNT0033.severity = none"; Severity = 'warn'; Expected = @() }
        @{ Label = 'suggestion at info threshold'; Rules = "dotnet_diagnostic.UNT0021.severity = suggestion`ndotnet_diagnostic.UNT0033.severity = none"; Severity = 'info'; Expected = @('UNT0021') }
        @{ Label = 'independently enabled rule'; Rules = "dotnet_diagnostic.UNT0021.severity = none`ndotnet_diagnostic.UNT0033.severity = warning"; Severity = 'warn'; Expected = @('UNT0033') }
        @{ Label = 'multiple enabled rules'; Rules = "dotnet_diagnostic.UNT0021.severity = warning`ndotnet_diagnostic.UNT0033.severity = warning"; Severity = 'warn'; Expected = @('UNT0021', 'UNT0033') }
        @{ Label = 'file pattern override'; Rules = "dotnet_diagnostic.UNT0021.severity = warning`ndotnet_diagnostic.UNT0033.severity = none`n[Sample/*.cs]`ndotnet_diagnostic.UNT0021.severity = none"; Severity = 'info'; Expected = @() }
    )
}

Describe "Unity analyzer configuration [$Os, $Image]" {
    BeforeAll {
        . (Join-Path $PSScriptRoot '../.jenkins/Docker.ps1')
        $container = "unity-analyzers-$([guid]::NewGuid().ToString('N'))"
        $fixtureRoot = if ($Os -eq 'windows') { 'C:\Windows\Temp\unity-analyzers' } else { '/tmp/unity-analyzers' }
        $keeper = if ($Os -eq 'windows') { 'cmd.exe' } else { 'cat' }
        Invoke-Docker -Context $Context -Arguments @('run', '--detach', '--interactive', '--name', $container, $Image, $keeper)
        Invoke-Docker -Context $Context -Arguments @('cp', (Join-Path $PSScriptRoot 'AnalyzerFixtures'), "${container}:$fixtureRoot")
        $project = if ($Os -eq 'windows') { "$fixtureRoot\Sample\Sample.csproj" } else { "$fixtureRoot/Sample/Sample.csproj" }
        $engineProject = if ($Os -eq 'windows') { "$fixtureRoot\Engine\Engine.csproj" } else { "$fixtureRoot/Engine/Engine.csproj" }
        Invoke-Docker -Context $Context -Arguments @('exec', $container, 'dotnet', 'build', $project, '--verbosity', 'quiet')
    }

    AfterAll {
        if ($container) {
            Get-DockerCommandResult -Context $Context -Arguments @('rm', '--force', '--volumes', $container) | Out-Null
        }
    }

    It 'keeps UNT0021 disabled without an editorconfig, even at info severity' {
        $result = Get-DockerCommandResult -Context $Context -Arguments @(
            'exec', $container, 'dotnet', 'format', $project, '--verify-no-changes', '--no-restore',
            '--diagnostics', 'UNT0021', '--severity', 'info'
        )
        $result.ExitCode | Should -Be 0 -Because ($result.Output | Out-String)
        ($result.Output | Out-String) | Should -Not -Match 'UNT0021'
    }

    It 'respects editorconfig: <Label>' -ForEach $editorConfigs {
        $config = Join-Path $TestDrive 'analyzers.editorconfig'
        Set-Content -LiteralPath $config -Value "root = true`n[*.cs]`n$Rules" -Encoding utf8
        Invoke-Docker -Context $Context -Arguments @('cp', $config, "${container}:$fixtureRoot/.editorconfig")
        $arguments = @('exec', $container, 'dotnet', 'format', $project, '--verify-no-changes', '--no-restore')
        if ($Severity -ne 'warn') {
            $arguments += @('--severity', $Severity)
        }
        $result = Get-DockerCommandResult -Context $Context -Arguments $arguments
        $output = $result.Output | Out-String
        $result.ExitCode | Should -Be $(if ($Expected.Count) { 2 } else { 0 }) -Because $output
        foreach ($diagnostic in 'UNT0021', 'UNT0033') {
            if ($diagnostic -in $Expected) {
                $output | Should -Match $diagnostic
            } else {
                $output | Should -Not -Match $diagnostic
            }
        }
        $output | Should -Not -Match 'CS8032|CS9057|Exception|error (MSB|NU)'
    }

    It 'accepts corrected Unity messages with both rules enabled' {
        $source = Join-Path $TestDrive 'NewMonoBehaviourScript.cs'
        $original = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AnalyzerFixtures/Sample/NewMonoBehaviourScript.cs') -Raw
        Set-Content -LiteralPath $source -Value ($original.Replace('public void Start()', 'protected void Start()').Replace('private void UPDATE()', 'protected void Update()')) -NoNewline -Encoding utf8
        Invoke-Docker -Context $Context -Arguments @('cp', $source, "${container}:$fixtureRoot/Sample/NewMonoBehaviourScript.cs")
        $config = Join-Path $TestDrive 'corrected.editorconfig'
        Set-Content -LiteralPath $config -Value "root = true`n[*.cs]`ndotnet_diagnostic.UNT0021.severity = warning`ndotnet_diagnostic.UNT0033.severity = warning" -Encoding utf8
        Invoke-Docker -Context $Context -Arguments @('cp', $config, "${container}:$fixtureRoot/.editorconfig")
        Invoke-Docker -Context $Context -Arguments @('exec', $container, 'dotnet', 'format', $project, '--verify-no-changes', '--no-restore')
    }

    It 'accepts a project without Unity behaviors' {
        Invoke-Docker -Context $Context -Arguments @('exec', $container, 'dotnet', 'format', $engineProject, '--verify-no-changes', '--no-restore')
    }
}
