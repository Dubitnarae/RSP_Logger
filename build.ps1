# exe 빌드: 먼저 src\Program.cs 의 AssemblyVersion·AssemblyFileVersion 수정 -> 이 스크립트 실행 -> dist\RSP_Logger.exe
# Windows 에 들어 있는 C# 컴파일러(.NET Framework 4.x csc)를 쓴다 — 따로 설치할 것 없음. C# 5 문법까지만 된다.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
New-Item -ItemType Directory -Force dist | Out-Null
& $csc /nologo /codepage:65001 /target:winexe /optimize+ /out:dist\RSP_Logger.exe `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.DataVisualization.dll `
  "/resource:config\settings.toml,settings.toml" "/resource:config\tags.csv,tags.csv" src\*.cs
if ($LASTEXITCODE -ne 0) { throw "컴파일 실패" }
# 자체 시험 (파이썬판 tests 를 옮긴 것) — 실패하면 게시하지 않는다
$p = Start-Process dist\RSP_Logger.exe -ArgumentList "--selftest" -Wait -PassThru -NoNewWindow -RedirectStandardOutput selftest_out.txt
Get-Content selftest_out.txt -Encoding utf8 | Select-Object -Last 1
if ($p.ExitCode -ne 0) { Get-Content selftest_out.txt -Encoding utf8 | Select-String "FAIL"; throw "자체 시험 실패 — 빌드 중단" }
