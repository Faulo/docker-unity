[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'ResultsPath is consumed in Pester discovery scope.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'Pester shares BeforeAll variables with It blocks.')]
param(
    [Parameter(Mandatory)]
    [string] $ResultsPath
)

BeforeDiscovery {
    $cases = @(
        Get-ChildItem -LiteralPath $ResultsPath -Filter result.json -Recurse -File |
            ForEach-Object {
                $data = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                if ($null -ne $data.cancellation -or $null -ne $data.cancellationRequested) {
                    @{ Name = "$($_.Directory.Parent.Name)/$($_.Directory.Name)"; ResultFile = $_.FullName }
                }
            }
    )
    if ($cases.Count -eq 0) {
        throw "No cancellation results found in $ResultsPath"
    }
}

Describe 'Linux cancellation during Bee: <Name>' -ForEach $cases {
    BeforeAll {
        $result = Get-Content -LiteralPath $ResultFile -Raw | ConvertFrom-Json
    }

    It 'observes Unity and Bee before sending the signal' {
        $result.cancellation.processesBeforeSignal.name | Should -Contain 'Unity'
        $result.cancellation.processesBeforeSignal.name | Should -Contain 'bee_backend'
    }

    It 'leaves no Unity or Bee processes after twenty seconds' {
        $result.verdict | Should -BeExactly 'cancelled_cleanly'
        @($result.cancellation.processesAfter20Seconds).Count | Should -Be 0
    }
}
