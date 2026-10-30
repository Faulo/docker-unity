[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester binds shared image data in separate script blocks.')]
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

BeforeAll {
    . (Join-Path $PSScriptRoot '../.jenkins/Docker.ps1')
}

Describe "Host Code Integrity pool contract [$Context, $Image]" -Skip:($Os -ne 'windows') {
    BeforeAll {
        $daemon = Invoke-DockerOutput -Context $Context -Arguments @('info', '--format', '{{json .}}') | ConvertFrom-Json
        $selfInspection = Get-DockerCommandResult -Context $Context -Arguments @('container', 'inspect', '--format', '{{json .}}', $env:COMPUTERNAME.ToLowerInvariant())
        $selfProcessContainer = $false
        if ($selfInspection.ExitCode -eq 0) {
            $selfContainer = ($selfInspection.Output | Out-String | ConvertFrom-Json)
            $selfProcessContainer = $selfContainer.HostConfig.Isolation -eq 'process' -and
                $selfContainer.Config.Hostname -eq $env:COMPUTERNAME -and
                $selfContainer.Id.StartsWith($env:COMPUTERNAME, [StringComparison]::OrdinalIgnoreCase)
        }
        $poolProbe = {
            $ErrorActionPreference = 'Stop'
            if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit host PowerShell.' }
            if (-not ('ContainerPoolProbe' -as [type])) {
                Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ContainerPoolProbe {
    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int required);
}
'@
            }
            $size = 4194304
            while ($true) {
                $buffer = [Runtime.InteropServices.Marshal]::AllocHGlobal($size)
                try {
                    $required = 0
                    # SystemPoolTagInformation is 22; CIcr is 0x72634943 in little-endian tag bytes.
                    $status = [ContainerPoolProbe]::NtQuerySystemInformation(22, $buffer, $size, [ref] $required)
                    if ($status -eq -1073741820) {
                        $size = [Math]::Max($size * 2, $required)
                        continue
                    }
                    if ($status -ne 0) { throw "Pool query failed: $status" }
                    $count = [Runtime.InteropServices.Marshal]::ReadInt32($buffer)
                    if ($count -lt 0 -or (8L + 40L * $count) -gt $size) { throw 'Invalid pool-tag buffer length' }
                    for ($index = 0; $index -lt $count; $index++) {
                        $entry = [IntPtr]::Add($buffer, 8 + 40 * $index)
                        if ([Runtime.InteropServices.Marshal]::ReadInt32($entry) -eq 0x72634943) {
                            $allocated = [BitConverter]::ToUInt32([BitConverter]::GetBytes([Runtime.InteropServices.Marshal]::ReadInt32($entry, 4)), 0)
                            $freed = [BitConverter]::ToUInt32([BitConverter]::GetBytes([Runtime.InteropServices.Marshal]::ReadInt32($entry, 8)), 0)
                            [pscustomobject]@{
                                Time = [DateTimeOffset]::UtcNow.ToString('o')
                                HostName = $env:COMPUTERNAME
                                PagedBytes = [Runtime.InteropServices.Marshal]::ReadInt64($entry, 16)
                                Outstanding = ([long] $allocated - [long] $freed + 4294967296L) % 4294967296L
                            }
                            return
                        }
                    }
                    throw 'CIcr pool tag absent'
                } finally {
                    [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)
                }
            }
        }
        $probeEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(
            "& { $poolProbe } | ForEach-Object { 'CIcr_SAMPLE:' + `$_.PagedBytes + ':' + `$_.Outstanding + ':' + `$_.HostName }"))

        function Read-HostPool {
            if ($env:COMPUTERNAME -eq $daemon.Name -or $selfProcessContainer) {
                $sample = & $poolProbe
            } else {
                $sshHost = if ($Context -eq 'default') { $daemon.Name } else { $Context }
                # Some Windows OpenSSH hosts cannot launch commands without a console.
                $output = @(& ssh -tt -o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=2 $sshHost powershell.exe -NoProfile -NonInteractive -EncodedCommand $probeEncoded 2>&1)
                if ($LASTEXITCODE -ne 0) { throw "Cannot read pool tags on Docker host $sshHost" }
                $plain = ($output | Out-String) -replace '\x1B\[[0-?]*[ -/]*[@-~]', ''
                $match = [regex]::Matches($plain, 'CIcr_SAMPLE:(\d+):(\d+):([\w-]+)') | Select-Object -Last 1
                if ($null -eq $match) { throw "Docker host $sshHost returned no CIcr sample: $plain" }
                $sample = [pscustomobject]@{
                    Time = [DateTimeOffset]::UtcNow.ToString('o')
                    HostName = $match.Groups[3].Value
                    PagedBytes = [long] $match.Groups[1].Value
                    Outstanding = [long] $match.Groups[2].Value
                }
            }
            $expectedName = if ($selfProcessContainer) { $env:COMPUTERNAME } else { $daemon.Name }
            if ($sample.HostName -ne $expectedName) {
                throw "Pool probe ran on $($sample.HostName), expected $expectedName for Docker host $($daemon.Name)"
            }
            return $sample
        }

        $hyperVRequested = ($DockerRunArguments -join ' ') -match '(?:^|\s)--isolation(?:=|\s+)hyperv(?:\s|$)'
        $isolation = if ($hyperVRequested -or $daemon.Isolation -eq 'hyperv') { 'hyperv' } else { 'process' }
        $identity = Invoke-DockerOutput -Context $Context -Arguments @('image', 'inspect', '--format', '{{.Id}} {{.OsVersion}}', $Image)
        Write-Information -InformationAction Continue -MessageData "CIcr host=$($daemon.Name); isolation=$isolation; image=$identity; allowance=0/0"
        $shellWorkload = {
            $ErrorActionPreference = 'Stop'
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ShellActivationProbe {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryW(string name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr FindExecutableW(string file, string directory, StringBuilder result);
}
'@
            # Load the AppInit trigger and exercise shell32 without relying on image-specific DLLs.
            foreach ($module in 'user32.dll', 'shell32.dll') {
                if ([ShellActivationProbe]::LoadLibraryW($module) -eq [IntPtr]::Zero) {
                    throw "Loading $module failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
                }
            }
            [void] [ShellActivationProbe]::FindExecutableW(
                (Join-Path $env:WINDIR 'System32/cmd.exe'), $null, [Text.StringBuilder]::new(260))
            Write-Output 'SHELL_ACTIVATION completed'
        }
        $activationEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($shellWorkload.ToString()))

        function Invoke-ProbeContainer {
            param([bool] $ActivateShell)
            $container = "kernel-pool-$([guid]::NewGuid().ToString('N'))"
            try {
                $command = if ($ActivateShell) {
                    @('--entrypoint', 'powershell.exe', $Image, '-NoProfile', '-NonInteractive', '-EncodedCommand', $activationEncoded)
                } else {
                    @('--entrypoint', 'cmd.exe', $Image, '/d', '/c', 'exit', '0')
                }
                $output = Invoke-DockerOutput -Context $Context -Arguments (@(
                    'run', '--rm', '--pull', 'never', '--network', 'none', '--isolation', $isolation,
                    '--no-healthcheck', '--name', $container
                ) + $command)
                if ($output) { Write-Information -InformationAction Continue -MessageData $output }
                $existing = Get-DockerCommandResult -Context $Context -Arguments @('container', 'inspect', $container)
                if ($existing.ExitCode -eq 0) { throw "Container $container was not removed" }
            } finally {
                $existing = Get-DockerCommandResult -Context $Context -Arguments @('container', 'inspect', $container)
                if ($existing.ExitCode -eq 0) {
                    Invoke-Docker -Context $Context -Arguments @('container', 'rm', '--force', $container)
                }
            }
        }
    }

    It 'retains no host CIcr after repeated <Workload> starts' -ForEach @(
        @{ Workload = 'minimal'; ActivateShell = $false },
        @{ Workload = 'shell application'; ActivateShell = $true }
    ) {
        # Warm the probe only: an image's first start must not hide retained pool.
        $null = Read-HostPool
        $previous = Read-HostPool
        $stable = $false
        for ($attempt = 0; $attempt -lt 6; $attempt++) {
            Start-Sleep -Seconds 3
            $current = Read-HostPool
            if ($current.PagedBytes -eq $previous.PagedBytes -and $current.Outstanding -eq $previous.Outstanding) {
                $stable = $true
                break
            }
            $previous = $current
        }
        if (-not $stable) { throw 'Host CIcr baseline is unstable; run this contract without concurrent builds or container starts.' }

        $before = $current
        $deltas = @()
        $allocationDeltas = @()
        foreach ($iteration in 1..3) {
            $timer = [Diagnostics.Stopwatch]::StartNew()
            Invoke-ProbeContainer -ActivateShell $ActivateShell
            $timer.Stop()
            Start-Sleep -Seconds 10
            $after = Read-HostPool
            $delta = $after.PagedBytes - $before.PagedBytes
            Write-Information -InformationAction Continue -MessageData ("CIcr start {0}: {1} bytes, {2} allocations; duration={3}s; before={4}; after={5}" -f
                $iteration, $delta, ($after.Outstanding - $before.Outstanding),
                $timer.Elapsed.TotalSeconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture),
                ($before | ConvertTo-Json -Compress), ($after | ConvertTo-Json -Compress))
            $deltas += $delta
            $allocationDeltas += $after.Outstanding - $before.Outstanding
            $before = $after
        }
        Start-Sleep -Seconds 30
        $idle = Read-HostPool
        Write-Information -InformationAction Continue -MessageData ("CIcr idle: {0} bytes, {1} allocations; sample={2}" -f
            ($idle.PagedBytes - $before.PagedBytes), ($idle.Outstanding - $before.Outstanding), ($idle | ConvertTo-Json -Compress))
        foreach ($delta in $deltas) {
            $delta | Should -BeLessOrEqual 0L -Because 'container teardown must add no host CIcr bytes'
        }
        foreach ($delta in $allocationDeltas) {
            $delta | Should -BeLessOrEqual 0L -Because 'container teardown must add no host CIcr allocations'
        }
        $idle.PagedBytes | Should -BeLessOrEqual $before.PagedBytes -Because 'idle observations must not add further CIcr retention'
        $idle.Outstanding | Should -BeLessOrEqual $before.Outstanding -Because 'idle observations must not add further CIcr allocations'
        $idle.PagedBytes | Should -BeLessOrEqual $current.PagedBytes -Because 'cumulative retention must stay at or below the baseline'
        $idle.Outstanding | Should -BeLessOrEqual $current.Outstanding -Because 'cumulative allocations must stay at or below the baseline'
    }
}
