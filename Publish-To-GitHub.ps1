param(
    [string]$Repository = "https://github.com/Luxciax/CherryKey.git"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "未检测到 Git。请先安装 Git for Windows：https://git-scm.com/download/win"
}

if (-not (Test-Path ".git")) {
    git init
    if ($LASTEXITCODE -ne 0) { throw "git init 失败。" }
}

git branch -M main

if (-not (git config user.name)) { git config user.name "Luxciax" }
if (-not (git config user.email)) { git config user.email "Luxciax@users.noreply.github.com" }

$origin = git remote get-url origin 2>$null
if ($LASTEXITCODE -eq 0) {
    git remote set-url origin $Repository
} else {
    git remote add origin $Repository
}

Write-Host "正在同步远端仓库…" -ForegroundColor Cyan
git fetch origin main
if ($LASTEXITCODE -eq 0) {
    # Make the remote commit the parent while preserving this package's working tree.
    git reset --mixed origin/main
}

git add -A
$changes = git status --porcelain
if ($changes) {
    git commit -m "fix: make startup visible and add diagnostics"
    if ($LASTEXITCODE -ne 0) { throw "git commit 失败。" }
} else {
    Write-Host "没有需要提交的变化。" -ForegroundColor Yellow
}

Write-Host "`n正在推送到 $Repository" -ForegroundColor Cyan
Write-Host "首次推送时 Git Credential Manager 可能打开浏览器，请登录 GitHub 并授权。`n" -ForegroundColor Yellow

git push -u origin main
if ($LASTEXITCODE -ne 0) {
    throw "推送失败。请检查 GitHub 登录状态和仓库权限。"
}

Write-Host "`n推送完成。GitHub Actions 已自动开始 Windows 构建。" -ForegroundColor Green
Write-Host "查看构建：https://github.com/Luxciax/CherryKey/actions" -ForegroundColor Green
Read-Host "按 Enter 关闭"
