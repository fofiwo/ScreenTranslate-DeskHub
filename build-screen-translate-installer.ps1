param(
    [switch]$RunTests,
    [switch]$NoRestore,
    [string]$Version,
    [string]$VpkPath = (Join-Path $PSScriptRoot '.tools\velopack\vpk.exe')
)

$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    if (-not (Test-Path -LiteralPath $VpkPath)) {
        throw '缺少 Velopack 1.2.0 CLI。请按 README.ScreenTranslate.md 安装到项目的 .tools/velopack。'
    }
    [xml]$project = Get-Content -LiteralPath 'src/OverTranslate/OverTranslate.csproj'
    if (-not $Version) { $Version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1) }
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '安装包版本必须为三段数字版本。' }
    $restoreArguments = @()
    if ($NoRestore) { $restoreArguments = @('--no-restore') }
    if ($RunTests) {
        foreach ($testProject in @('src/OverTranslate.Tests/OverTranslate.Tests.csproj', 'src/ScreenTranslate.UiTests/ScreenTranslate.UiTests.csproj')) {
            & dotnet test $testProject -c Release --nologo @restoreArguments
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $testProject" }
        }
    }

    # 安装包只读取新发布目录，绝不使用可能包含个人设置的 app/data。
    $payload = Join-Path $PSScriptRoot ("artifacts\installer-payload\{0}-{1}" -f $version, [Guid]::NewGuid().ToString("N"))
    $release = Join-Path $PSScriptRoot 'artifacts\releases'
    & dotnet publish 'src/OverTranslate/OverTranslate.csproj' -c Release -r win-x64 --self-contained true -p:Version=$Version -o $payload --nologo @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed' }
    Copy-Item -LiteralPath 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'FORK-NOTICES.md' -Destination $payload
    Copy-Item -LiteralPath 'docs/ScreenTranslate-Install.md' -Destination (Join-Path $payload 'README.md')
    New-Item -ItemType Directory -Path (Join-Path $payload 'docs/licenses') -Force | Out-Null
    Copy-Item -LiteralPath 'docs/licenses/FloatFolder-MIT.txt' -Destination (Join-Path $payload 'docs/licenses')
    $forbidden = @(Get-ChildItem -LiteralPath $payload -Force -Recurse | Where-Object {
        $_.Name -in @('data', 'logs', 'clipboard-history', 'library.json', 'sync.json', 'lan-preferences.json', 'sync-onboarding.json') -or
        $_.Name -like '.env*' -or $_.Name -like 'appsettings*.json' -or
        $_.Extension -in @('.clip', '.log', '.pfx', '.pem', '.key', '.db', '.sqlite', '.sqlite3')
    })
    if ($forbidden.Count) { throw '安装包发布目录包含设置或数据文件，已停止打包。' }

    & $VpkPath --skip-updates true --yes true pack --packId ScreenTranslate --packVersion $version --packDir $payload `
        --mainExe ScreenTranslate.exe --packTitle ScreenTranslate --packAuthors ScreenTranslate `
        --icon (Join-Path $PSScriptRoot 'src\OverTranslate\icons\app.ico') `
        --runtime win-x64 --channel win --delta None --noPortable true --shortcuts StartMenuRoot --outputDir $release
    if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed' }
    $setup = Join-Path $release 'ScreenTranslate-win-Setup.exe'
    if (-not (Test-Path -LiteralPath $setup)) { throw 'Installer output was not found' }
    $hash = Get-FileHash -LiteralPath $setup -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath "$setup.sha256" -Encoding ascii
    Write-Host "Installer ready: $setup"
} finally { Pop-Location }
