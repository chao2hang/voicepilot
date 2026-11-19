param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$Push
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if ($Version -notmatch '^v\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "版本必须为 vMAJOR.MINOR.PATCH，可带 SemVer 预发布后缀：$Version"
}

$dirty = git status --porcelain
if ($LASTEXITCODE -ne 0) { throw '无法读取 Git 状态。' }
if ($dirty) { throw '工作区有未提交变更。请先提交或清理，再创建版本标签。' }
$existingTag = git tag --list $Version
$commit = (git rev-parse HEAD).Trim()
if ($existingTag) {
    $tagCommit = (git rev-parse "$Version^{}").Trim()
    if (-not $Push) { throw "标签已存在：$Version。若已确认，可直接执行 git push origin $Version。" }
    if ($tagCommit -ne $commit) { throw "标签 $Version 指向 $tagCommit，不是当前提交 $commit；拒绝移动历史版本。" }
    Write-Host "使用当前提交上的现有标签 $Version"
}
else {
    & git tag -a $Version -m "VoicePilot $Version" $commit
    if ($LASTEXITCODE -ne 0) { throw '创建注释标签失败。' }
    Write-Host "已创建本地标签 $Version -> $commit"
}
if ($Push) {
    & git -c http.version=HTTP/1.1 push origin $Version
    if ($LASTEXITCODE -ne 0) { throw "标签仍只在本地。修复网络后执行：git push origin $Version" }
    Write-Host "已推送 $Version；GitHub Actions 将构建并创建 Release。" -ForegroundColor Green
}
else {
    Write-Host "尚未推送，不会触发 GitHub 构建。确认后执行：git push origin $Version"
}
