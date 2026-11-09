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
        git -c user.name=fofiwo -c user.email=239156749+fofiwo@users.noreply.github.com commit -m $Message
        if ($LASTEXITCODE -ne 0) { throw 'Commit failed' }
    }
    git -c 'credential.helper=!gh auth git-credential' -c http.version=HTTP/1.1 push -u origin main
    if ($LASTEXITCODE -ne 0) { throw 'Push failed; local snapshot retained' }
} finally { Pop-Location }
