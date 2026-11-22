#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [switch] $Test,
    [switch] $Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root

try {
    dotnet build XdmSharp.sln -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($Test) {
        dotnet test XdmSharp.sln -c $Configuration --no-build --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    if ($Publish) {
        # SimdAIO 要求 win-x64；不指定 RID 时 publish 会跳过多文件/单文件设定
        dotnet publish src/Xdm.App/Xdm.App.csproj -c Release -r win-x64 -o publish/win-x64 --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
finally {
    Pop-Location
}
