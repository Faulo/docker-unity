param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $OsBase,

    [Parameter(Mandatory)]
    [string] $OriginalX64,

    [Parameter(Mandatory)]
    [string] $OriginalX86,

    [string] $LlvmBin = "$env:TEMP\codex-llvm-22.1.6\bin",

    [string] $WindowsSdkLib = "C:\Program Files (x86)\Windows Kits\10\Lib\10.0.22621.0\um"
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$sourceDirectory = $PSScriptRoot
$outputDirectory = Join-Path $sourceDirectory $OsBase
$hashManifest = Join-Path $outputDirectory 'shell32-hashes.psd1'
$readObject = Join-Path $LlvmBin 'llvm-readobj.exe'
$clang = Join-Path $LlvmBin 'clang-cl.exe'
$linker = Join-Path $LlvmBin 'lld-link.exe'

if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container) -or
    -not (Test-Path -LiteralPath $hashManifest -PathType Leaf)) {
    throw "Unsupported Windows OS base: $OsBase"
}

foreach ($tool in $readObject, $clang, $linker) {
    if (-not (Test-Path -LiteralPath $tool)) {
        throw "Required LLVM tool not found: $tool"
    }
}

function New-ExportResponse {
    param(
        [string] $OriginalDll,
        [string] $Architecture,
        [string] $OutputPath
    )

    $exportText = (& $readObject --coff-exports $OriginalDll | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to inspect exports in $OriginalDll"
    }

    $blocks = [regex]::Matches(
        $exportText,
        'Export \{(?<body>.*?)\}',
        [Text.RegularExpressions.RegexOptions]::Singleline)
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($block in $blocks) {
        $body = $block.Groups['body'].Value
        $ordinalMatch = [regex]::Match($body, 'Ordinal: (?<ordinal>\d+)')
        $nameMatch = [regex]::Match($body, 'Name: (?<name>[^\r\n]*)')
        if (-not $ordinalMatch.Success -or -not $nameMatch.Success) {
            continue
        }

        $ordinal = $ordinalMatch.Groups['ordinal'].Value
        $name = $nameMatch.Groups['name'].Value.Trim()
        if ($name -eq 'FindExecutableW') {
            $hook = if ($Architecture -eq 'x86') {
                '_HookFindExecutableW@12'
            } else {
                'HookFindExecutableW'
            }
            $lines.Add("/EXPORT:FindExecutableW=$hook,@$ordinal")
        } elseif ($name -eq 'ShellExecuteExW') {
            $hook = if ($Architecture -eq 'x86') {
                '_HookShellExecuteExW@4'
            } else {
                'HookShellExecuteExW'
            }
            $lines.Add("/EXPORT:ShellExecuteExW=$hook,@$ordinal")
        } elseif ($name) {
            $lines.Add("/EXPORT:$name=shell32real.$name,@$ordinal")
        } else {
            $lines.Add("/EXPORT:UnityOrdinal$ordinal=shell32real.#$ordinal,@$ordinal,NONAME")
        }
    }

    if ($lines.Count -lt 1000) {
        throw "Unexpectedly small SHELL32 export set in $OriginalDll`: $($lines.Count)"
    }
    [IO.File]::WriteAllLines($OutputPath, $lines)
}

function Build-Architecture {
    param(
        [ValidateSet('x64', 'x86')]
        [string] $Architecture,
        [string] $OriginalDll
    )

    $object = Join-Path $outputDirectory "shell32-proxy-$Architecture.obj"
    $testObject = Join-Path $sourceDirectory "shell32-proxy-test-$Architecture.obj"
    $response = Join-Path $outputDirectory "shell32-exports-$Architecture.rsp"
    $proxy = Join-Path $outputDirectory "shell32-proxy-$Architecture.dll"
    $importLibrary = Join-Path $outputDirectory "shell32-proxy-$Architecture.lib"
    $test = Join-Path $sourceDirectory "shell32-proxy-test-$Architecture.exe"
    $sdkLibraries = Join-Path $WindowsSdkLib $Architecture
    $targetArgument = if ($Architecture -eq 'x86') {
        '--target=i686-pc-windows-msvc'
    } else {
        '--target=x86_64-pc-windows-msvc'
    }

    New-ExportResponse $OriginalDll $Architecture $response

    & $clang $targetArgument /nologo /c /GS- /Zl /O2 "/Fo$object" (
        Join-Path $sourceDirectory 'shell32-proxy.cpp'
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to compile the $Architecture SHELL32 proxy"
    }
    & $clang $targetArgument /nologo /c /GS- /Zl /O2 "/Fo$testObject" (
        Join-Path $sourceDirectory 'shell32-proxy-test.cpp'
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to compile the $Architecture SHELL32 proxy test"
    }

    Push-Location $sourceDirectory
    try {
        & $linker /dll /noentry "/machine:$Architecture" /nodefaultlib /timestamp:0 `
            "/out:$proxy" "/implib:$importLibrary" `
            $object (Join-Path $sdkLibraries 'kernel32.lib') `
            (Join-Path $sdkLibraries 'shlwapi.lib') "@$response"
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to link the $Architecture SHELL32 proxy"
        }
        & $linker /subsystem:console /entry:mainCRTStartup "/machine:$Architecture" /timestamp:0 `
            /nodefaultlib "/out:$test" $testObject `
            (Join-Path $sdkLibraries 'kernel32.lib') `
            (Join-Path $sdkLibraries 'shell32.lib')
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to link the $Architecture SHELL32 proxy test"
        }
    } finally {
        Pop-Location
    }

    Remove-Item -LiteralPath $object, $testObject, $importLibrary
}

Build-Architecture x64 $OriginalX64
Build-Architecture x86 $OriginalX86

Get-FileHash -Algorithm SHA256 (
    Join-Path $outputDirectory 'shell32-proxy-x64.dll'
), (
    Join-Path $outputDirectory 'shell32-proxy-x86.dll'
), (
    Join-Path $sourceDirectory 'shell32-proxy-test-x64.exe'
), (
    Join-Path $sourceDirectory 'shell32-proxy-test-x86.exe'
)
