#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [ValidateSet('avalonia', 'mew')]
    [string] $Ui = 'avalonia',
    [switch] $Test,
    [switch] $Publish,
    [switch] $Aot,
    [switch] $KeepAotSymbols
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root

# 双前端（ARCHITECTURE.md §2.1）：只有启动栈不同，Xdm.Core / Xdm.Ui.Shared 与测试完全共用。
switch ($Ui) {
    'avalonia' {
        $appProject = 'src/Xdm.App/Xdm.App.csproj'
        $publishDir = 'publish/win-x64'
        $aotDir = 'publish/win-x64-aot'
    }
    'mew' {
        $appProject = 'src/Xdm.App.Mew/Xdm.App.Mew.csproj'
        $publishDir = 'publish/mew-win-x64'
        $aotDir = 'publish/mew-win-x64-aot'
    }
}

try {
    dotnet build XdmSharp.sln -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($Test) {
        dotnet test XdmSharp.sln -c $Configuration --no-build --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    if ($Publish) {
        # SimdAIO 要求 win-x64；不指定 RID 时 publish 会跳过多文件/单文件设定
        if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
        dotnet publish $appProject -c Release -r win-x64 -o $publishDir --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    if ($Aot) {
        # NativeAOT 档：csproj 里的条件组据此固定 RID / SelfContained / StripSymbols
        if (Test-Path $aotDir) { Remove-Item $aotDir -Recurse -Force }
        dotnet publish $appProject -c Release -r win-x64 -o $aotDir --nologo -p:PublishAot=true
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        # AfterTargets="Publish" 早于 ILC 符号拷贝，删不掉 ~118MB 的 .pdb；发布完成后在这里清
        if (-not $KeepAotSymbols) {
            Get-ChildItem $aotDir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
        }
    }
}
finally {
    Pop-Location
}
