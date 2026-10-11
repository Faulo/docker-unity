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

Describe "Docker integration environment [$Context, $Image]" {
    It "reaches docker daemon $Context" {
        Invoke-Docker -Context $Context -Arguments @('info')
    }

    It "has image $Image" {
        Invoke-Docker -Context $Context -Arguments @('image', 'inspect', $Image)
    }
}
