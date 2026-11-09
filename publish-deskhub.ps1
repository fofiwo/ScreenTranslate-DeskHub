param([string]$Message = 'Update ScreenTranslate DeskHub')
$ErrorActionPreference = 'Stop'
$env:HTTP_PROXY = 'http://127.0.0.1:10808'
$env:HTTPS_PROXY = 'http://127.0.0.1:10808'
$public = Join-Path $PSScriptRoot 'artifacts/deskhub-public'
python (Join-Path $PSScriptRoot 'tools/prepare-public-source.py') $public
if ($LASTEXITCODE -ne 0) { throw 'Public source export failed' }
Push-Location -LiteralPath $public
try {
    if (-not (Test-Path .git)) {
        git init -b main
        git remote add origin https://github.com/fofiwo/ScreenTranslate-DeskHub.git
    }
    if ((git remote get-url origin) -ne 'https://github.com/fofiwo/ScreenTranslate-DeskHub.git') { throw 'Unexpected public remote' }
    git add --all
    git diff --cached --quiet
    if ($LASTEXITCODE -eq 1) {
        git -c user.name=fofiwo -c user.email=239156749+fofiwo@users.noreply.github.com commit --quiet -m $Message
        if ($LASTEXITCODE -ne 0) { throw 'Commit failed' }
    }
    $env:GIT_TERMINAL_PROMPT = '0'
    git -c credential.helper= -c 'credential.helper=!gh auth git-credential' -c credential.interactive=false -c http.proxy=http://127.0.0.1:10808 -c http.version=HTTP/1.1 -c http.lowSpeedLimit=1000 -c http.lowSpeedTime=30 push -u origin main
    if ($LASTEXITCODE -ne 0) { throw 'Push failed; local snapshot retained' }
} finally { Pop-Location }
