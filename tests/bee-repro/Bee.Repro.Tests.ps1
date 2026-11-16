[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'ResultsPath is consumed in Pester discovery scope.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'Pester shares BeforeAll variables with It blocks.')]
param(
    [Parameter(Mandatory)]
    [string] $ResultsPath
)

BeforeDiscovery {
    $cases = @(
        Get-ChildItem -LiteralPath $ResultsPath -Filter result.json -Recurse -File |
            ForEach-Object { @{ Name = $_.Directory.Name; ResultFile = $_.FullName } }
    )
    if ($cases.Count -eq 0) {
        throw "No reproduction results found in $ResultsPath"
    }
}

Describe 'Cold Linux Windows-player build: <Name>' -ForEach $cases {
    BeforeAll {
        $result = Get-Content -LiteralPath $ResultFile -Raw | ConvertFrom-Json
    }

    It 'starts without a generated Library' {
        $result.coldLibrary | Should -BeTrue
    }

    It 'finishes successfully and produces a player' {
        $result.verdict | Should -BeExactly 'passed' -Because (
            "the build must finish; observed $($result.verdict) after $($result.elapsedSeconds) seconds"
        )
        $result.exitCodeBeforeCleanup | Should -Be 0
        @($result.players | Where-Object { $_.bytes -gt 0 -and $_.path -notmatch 'UnityCrashHandler' }).Count |
            Should -BeGreaterThan 0
    }
}
