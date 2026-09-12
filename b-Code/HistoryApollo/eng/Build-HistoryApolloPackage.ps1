#requires -Version 5.1

<#
    构建 HistoryApollo 发布候选。

    本地默认写出 `z-Publish/HistoryApollo-vX.Y.Z/`。传入 OutputRoot 时该目录就是包根
    （宿主 staging 或临时校验）。
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputRoot,

    [string]$HistoryVulcanPackageRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$componentRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $componentRoot '..\..'))

# 版本必须三处一致：版本 props、模块 manifest、项目 manifest。
# 宿主对「manifest 与 ModuleInfo 版本不一致」的处理是静默跳过整个模块，
# 所以这里宁可构建失败，也不让它带着漂移的版本出厂。
$propsText = Get-Content -LiteralPath (Join-Path $componentRoot 'HistoryApolloVersion.props') -Raw -Encoding UTF8
$versionMatch = [regex]::Match($propsText, '<HistoryApolloVersion>(?<v>[^<]+)</HistoryApolloVersion>')
if (-not $versionMatch.Success) {
    throw 'HistoryApolloVersion.props 未声明 HistoryApolloVersion'
}
$version = $versionMatch.Groups['v'].Value

$manifestPath = Join-Path $componentRoot 'module.manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.version -ne $version) {
    throw "module.manifest.json ($($manifest.version)) 与 HistoryApolloVersion.props ($version) 不一致"
}

$projectManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'project.manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($projectManifest.project.version -ne $version) {
    throw "project.manifest.json ($($projectManifest.project.version)) 与 HistoryApolloVersion.props ($version) 不一致"
}

$buildProperties = @('-p:NuGetAudit=false')
if (-not [string]::IsNullOrWhiteSpace($HistoryVulcanPackageRoot)) {
    $buildProperties += "-p:HistoryVulcanPackageRoot=$HistoryVulcanPackageRoot"
}
& dotnet build (Join-Path $componentRoot 'HistoryApollo.csproj') -c $Configuration --nologo @buildProperties
if ($LASTEXITCODE -ne 0) { throw "构建失败，退出码 $LASTEXITCODE" }

$output = Join-Path $componentRoot "bin\$Configuration\net8.0"
if (-not (Test-Path -LiteralPath $output)) { throw "构建产物缺失: $output" }

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("HistoryApollo-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    foreach ($name in @('HistoryApollo.dll', 'HistoryApollo.xml', 'module.manifest.json')) {
        $source = Join-Path $output $name
        if (-not (Test-Path -LiteralPath $source)) { throw "应有的产物缺失: $source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage $name) -Force
    }

    # 模块消费文档随包走：装到运行区之后，读文档不必回头找源码仓。
    $docsSource = Join-Path $repoRoot 'b-Office\package\模块API.md'
    if (-not (Test-Path -LiteralPath $docsSource)) { throw "模块 API 文档缺失: $docsSource" }
    $docsStage = Join-Path $stage 'docs'
    New-Item -ItemType Directory -Path $docsStage -Force | Out-Null
    Copy-Item -LiteralPath $docsSource -Destination (Join-Path $docsStage '模块API.md') -Force

    # SHA256SUMS 覆盖包内全部有效载荷，排除自身、history/ 与 data/。
    $lines = Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        if ($relative -eq 'SHA256SUMS' -or $relative -like 'history/*' -or $relative -like 'data/*') { return }
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        "$hash  $relative"
    } | Where-Object { $_ }

    # 必须无 BOM：宿主按 ^[0-9A-Fa-f]{64}  <路径>$ 逐行匹配，BOM 会让首行匹配失败，
    # 整个包被判 invalid-checksum 而静默跳过。
    # Set-Content -Encoding utf8 在 Windows PowerShell 5.1 下写的是带 BOM 的 UTF-8，不能用。
    [System.IO.File]::WriteAllLines(
        (Join-Path $stage 'SHA256SUMS'),
        [string[]]$lines,
        (New-Object System.Text.UTF8Encoding $false))

    if (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
        $staged = [IO.Path]::GetFullPath($OutputRoot)
        New-Item -ItemType Directory -Path $staged -Force | Out-Null
        Get-ChildItem -LiteralPath $staged -Force | Remove-Item -Recurse -Force
        Copy-Item -Path (Join-Path $stage '*') -Destination $staged -Recurse -Force
        Write-Host "HistoryApollo $version staged: $staged"
        return
    }

    $publishRoot = Join-Path $repoRoot 'z-Publish'
    $historyRoot = Join-Path $publishRoot 'history'
    New-Item -ItemType Directory -Path $historyRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $publishRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'HistoryApollo-v*' -and $_.Name -ne "HistoryApollo-v$version" } |
        ForEach-Object {
            $archive = Join-Path $historyRoot $_.Name
            if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Recurse -Force }
            Move-Item -LiteralPath $_.FullName -Destination $archive
        }

    $candidate = Join-Path $publishRoot "HistoryApollo-v$version"
    if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Recurse -Force }
    New-Item -ItemType Directory -Path $candidate -Force | Out-Null
    Copy-Item -Path (Join-Path $stage '*') -Destination $candidate -Recurse -Force

    Write-Host "HistoryApollo $version packaged: $candidate"
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
