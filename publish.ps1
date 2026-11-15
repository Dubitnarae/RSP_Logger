# 릴리스 게시: 버전 올리기(src\Program.cs) -> CHANGELOG 정리 -> 커밋·push -> 이 스크립트 실행
# GitHub 릴리스에 RSP_Logger.exe + .sha256을 올린다. 자동 업데이트가 이 두 파일을 받는다.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
& .\build.ps1
$v = [version](Get-Item dist\RSP_Logger.exe).VersionInfo.FileVersion
$ver = "$($v.Major).$($v.Minor).$($v.Build)"
$hash = (Get-FileHash dist\RSP_Logger.exe -Algorithm SHA256).Hash.ToLower()
Set-Content -Encoding ascii dist\RSP_Logger.exe.sha256 "$hash  RSP_Logger.exe"
# CHANGELOG의 해당 버전 섹션을 릴리스 설명으로 사용
$notes = (Get-Content CHANGELOG.md -Raw -Encoding utf8) -split "(?m)^## " | Where-Object { $_.StartsWith("[$ver]") } | Select-Object -First 1
if (-not $notes) { throw "CHANGELOG.md에 [$ver] 섹션이 없습니다." }
$notes = ($notes -split "`n", 2)[1].Trim()
$nf = New-TemporaryFile; [IO.File]::WriteAllText($nf, $notes)  # BOM 없이
git tag -f "v$ver"; git push -f origin "v$ver"
gh release create "v$ver" dist\RSP_Logger.exe dist\RSP_Logger.exe.sha256 --title "RSP Logger v$ver" --notes-file $nf
Remove-Item $nf
