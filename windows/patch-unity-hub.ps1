param(
    [string] $Path = 'C:\Program Files\Unity Hub\resources\app.asar'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$data = [IO.File]::ReadAllBytes($Path)
$text = [Text.Encoding]::GetEncoding(28591).GetString($data)

function Find-All {
    param(
        [string] $Value,
        [string] $Pattern
    )

    $matches = [Collections.Generic.List[int]]::new()
    for ($index = $Value.IndexOf($Pattern, [StringComparison]::Ordinal);
         $index -ge 0;
         $index = $Value.IndexOf($Pattern, $index + 1, [StringComparison]::Ordinal)) {
        $matches.Add($index)
    }
    return $matches.ToArray()
}

function Set-Replacement {
    param(
        [string] $Original,
        [string] $Replacement,
        [string] $Label
    )

    if ($Replacement.Length -gt $Original.Length) {
        throw "$Label replacement is too large"
    }
    $padded = $Replacement.PadRight($Original.Length)
    $originalMatches = @(Find-All -Value $text -Pattern $Original)
    $replacementMatches = @(Find-All -Value $text -Pattern $padded)
    if ($originalMatches.Count -eq 1 -and $replacementMatches.Count -eq 0) {
        [Text.Encoding]::ASCII.GetBytes($padded).CopyTo($data, $originalMatches[0])
        return
    }
    if ($originalMatches.Count -eq 0 -and $replacementMatches.Count -eq 1) {
        return
    }
    throw "Expected one $Label block, found $($originalMatches.Count) original and $($replacementMatches.Count) patched"
}

Set-Replacement `
    -Original 'override:!1,removeOnFail:!1,removeOnStop:!1,retry:!1,' `
    -Replacement 'removeOnFail:!1,removeOnStop:!1,retry:{maxRetries:5},' `
    -Label 'Hub download options'
Set-Replacement `
    -Original 'return b(0<a.__downloaded?a.resume():a.__start())' `
    -Replacement 'return a.__opts.override=!0,b(a.__start())' `
    -Label 'Hub download retry behavior'
Set-Replacement `
    -Original 'logger.info("executing",s,l),e){const e=featureFlagsService.getFlagByName' `
    -Replacement 'logger.info("executing",s,l),0){const e=featureFlagsService.getFlagByName' `
    -Label 'Windows installer elevation'
Set-Replacement `
    -Original 'function installFromExe(e,r,t){return logger.debug("installFromExe"),new Promise(((o,n)=>{let i="/S";r&&""!==r&&(i=r);let a="";t&&(a=`/D=${t}`),logger.info(`install ${e} ${i} ${a}`),proc.exec(`"${e}" ${i} ${a}`,{name:"Unity installer"},((e,r)=>{e?n(e):r?n(r):o()}))}))}' `
    -Replacement 'function installFromExe(e,r,t){return new Promise(((o,n)=>{let i=r||"/S",a=t?`/D=${t}`:"",s=30,c=()=>proc.exec(`"${e}" ${i} ${a}`,{name:"Unity installer"},((e,r)=>{e&&/another process/.test(e.message)&&s--?setTimeout(c,1000):e?n(e):r?n(r):o()}));setTimeout(c,1000)}))}' `
    -Label 'Windows editor installer'

[IO.File]::WriteAllBytes($Path, $data)
