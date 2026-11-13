# ============================================================
#  DHMemCleaner 빌드·설치파일·릴리즈 스크립트
#
#  사용법:
#    .\build.ps1                 # 테스트 → publish → 설치 파일 + .sha256 생성
#    .\build.ps1 -SkipTests      # 테스트 생략
#    .\build.ps1 -Release        # 위 + GitHub 릴리즈(vX.Y.Z) 생성·업로드 (gh 로그인 필요)
#
#  버전은 MemoryCleaner.csproj 의 <Version> 하나만 고치면 된다.
#  산출물: installer_output\DHMemCleaner_Setup_X.Y.Z.exe (+ .sha256)
#  앱의 자체 업데이트는 이 두 파일 이름 규칙에 의존한다(Core\Updater.cs).
# ============================================================
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$Release,
    [string]$NotesFile
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

function Fail($msg) { Write-Host "[build] 실패: $msg" -ForegroundColor Red; exit 1 }

# ── 버전 ─────────────────────────────────────────────────────
[xml]$proj = Get-Content (Join-Path $root 'MemoryCleaner.csproj') -Raw
$version = ($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version -or $version -notmatch '^\d+\.\d+\.\d+$') { Fail "csproj <Version> 을 읽지 못함: '$version'" }
Write-Host "[build] 버전 $version"

# ── 테스트 ───────────────────────────────────────────────────
if (-not $SkipTests) {
    Write-Host '[build] 테스트 실행'
    dotnet test (Join-Path $root 'Tests\MemoryCleaner.Tests.csproj') -nologo
    if ($LASTEXITCODE -ne 0) { Fail '테스트 실패' }
}

# ── publish (self-contained 단일 exe) ────────────────────────
$publish = Join-Path $root 'publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Write-Host '[build] dotnet publish'
dotnet publish (Join-Path $root 'MemoryCleaner.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish -nologo
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish 실패' }

$exe = Join-Path $publish 'MemoryCleaner.exe'
if (-not (Test-Path $exe)) { Fail "publish 산출물 없음: $exe" }
$fileVer = (Get-Item $exe).VersionInfo.ProductVersion
if (-not $fileVer.StartsWith($version)) { Fail "exe 버전($fileVer)이 csproj 버전($version)과 다름" }

# ── 설치 파일 (Inno Setup) ───────────────────────────────────
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Fail 'ISCC.exe(Inno Setup 6)를 찾지 못함' }

Write-Host '[build] 설치 파일 컴파일'
& $iscc "/DMyAppVersion=$version" (Join-Path $root 'installer.iss')
if ($LASTEXITCODE -ne 0) { Fail 'ISCC 실패' }

$setupName = "DHMemCleaner_Setup_$version.exe"
$setup = Join-Path $root "installer_output\$setupName"
if (-not (Test-Path $setup)) { Fail "설치 파일 없음: $setup" }

# ── 체크섬 (sha256sum 형식, BOM 없는 ASCII) ──────────────────
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$shaPath = "$setup.sha256"
[System.IO.File]::WriteAllText($shaPath, "$hash  $setupName`n", [System.Text.Encoding]::ASCII)
Write-Host "[build] $setupName"
Write-Host "[build] sha256 $hash"

# ── GitHub 릴리즈 ────────────────────────────────────────────
if ($Release) {
    $tag = "v$version"
    gh release view $tag *> $null
    if ($LASTEXITCODE -eq 0) { Fail "릴리즈 $tag 가 이미 있음 — csproj <Version>을 올리세요" }

    $notesArgs = @('--generate-notes')
    if ($NotesFile) { $notesArgs = @('--notes-file', $NotesFile) }

    Write-Host "[build] GitHub 릴리즈 $tag 생성"
    gh release create $tag $setup $shaPath --title "메모리클리너 $tag" @notesArgs
    if ($LASTEXITCODE -ne 0) { Fail 'gh release create 실패' }
}

Write-Host '[build] 완료' -ForegroundColor Green
