"""Export a source allowlist into an isolated public checkout without local data or history."""
from pathlib import Path
import hashlib
import json
import re
import shutil
import subprocess
import sys

source = Path(__file__).resolve().parents[1]
target = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else source / "artifacts" / "deskhub-public"
if target == source or source.is_relative_to(target):
    raise SystemExit("Destination must be a separate checkout")
target.mkdir(parents=True, exist_ok=True)
manifest_file = source / "artifacts" / "deskhub-source-manifest.json"
previous = json.loads(manifest_file.read_text(encoding="utf-8")) if manifest_file.exists() else []
roots = {"src"}
files = {"LICENSE", "THIRD-PARTY-NOTICES.txt", "FORK-NOTICES.md", "PRIVACY.md", ".gitattributes", ".gitignore",
         "build-screen-translate-installer.ps1", "README.DeskHub.md", "publish-deskhub.ps1",
         "docs/ScreenTranslate-Install.md", "docs/Screenshot.md", "docs/ScreenTranslate-Lan-Sync.md", "docs/Daily-Adversarial-Review-2026-11-09.md", "docs/Update-Proxy-Review-2026-11-09.md", ".github/workflows/deskhub-release.yml",
         "tools/prepare-public-source.py",
         "DESIGN.md", "PRODUCT.md",
         "docs/Desktop-Visual-Language-2026-11-11.md", "docs/Desktop-Visual-Adversarial-Review-2026-11-11.md",
         "docs/File-Receive-Adversarial-Review-2026-11-11.md", "docs/File-Receive-Page-2026-11-11.md",
         "docs/File-Transfer-Completion-2026-11-11.md", "docs/File-Transfer-UX-2026-11-11.md",
         "docs/Screenshot-First-Use-Adversarial-Review-2026-11-11.md", "docs/Screenshot-First-Use-Performance-2026-11-11.md",
         "docs/Shortcut-First-Open-2026-11-11.md",
         ".codex/skills/git-release-overtranslate/template.md",
         ".claude/skills/git-release-overtranslate/template.md"}
forbidden_dirs = {".git", "bin", "obj", "data", "logs", ".vs", "TestResults", "clipboard-history"}
forbidden_names = {"library.json", "sync.json", "lan-preferences.json", "sync-onboarding.json", "settings.json"}
forbidden_suffixes = {".clip", ".log", ".pfx", ".pem", ".key", ".db", ".sqlite", ".sqlite3", ".user", ".suo"}
result = subprocess.run(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
                        cwd=source, check=True, stdout=subprocess.PIPE)
manifest = []
for name in sorted(set(result.stdout.decode("utf-8").split("\0")) - {""}):
    path = Path(name)
    if not (path.parts[0] in roots or name in files or name.startswith("docs/licenses/") or name.startswith("docs/guides/OLLAMA_GUIDE") or name.startswith("tools/UpdateFeedProbe/")):
        continue
    if any(part in forbidden_dirs for part in path.parts) or path.name in forbidden_names:
        continue
    if path.name.startswith(".env") or path.name.lower().startswith("appsettings") and path.suffix == ".json" or path.suffix.lower() in forbidden_suffixes:
        continue
    original = source / path
    if not original.is_file() or original.is_symlink():
        continue
    if original.stat().st_size >= 100 * 1024 * 1024:
        raise SystemExit("Oversize source file: " + name)
    destination = target / path
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(original, destination)
    manifest.append({"path": name, "sha256": hashlib.sha256(destination.read_bytes()).hexdigest()})
shutil.copyfile(source / "README.DeskHub.md", target / "README.md")
# Scan only the exported non-personal source. Never print matched values.
patterns = [re.compile(rb"gh[pousr]_[A-Za-z0-9]{30,}"), re.compile(rb"github_pat_[A-Za-z0-9_]{40,}"),
            re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")]
for entry in manifest:
    path = target / entry["path"]
    if path.suffix.lower() in {".cs", ".json", ".yml", ".yaml", ".md", ".ps1", ".py", ".config"}:
        body = path.read_bytes()
        if any(pattern.search(body) for pattern in patterns):
            raise SystemExit("Potential credential in exported source (value withheld): " + entry["path"])
current_names = {entry["path"] for entry in manifest}
for entry in previous:
    if entry["path"] not in current_names:
        stale = (target / entry["path"]).resolve()
        if not stale.is_relative_to(target) or ".git" in stale.relative_to(target).parts:
            raise SystemExit("Unsafe stale export path")
        if stale.is_file():
            if hashlib.sha256(stale.read_bytes()).hexdigest() != entry["sha256"]:
                raise SystemExit("Public snapshot has an independent edit; retained: " + entry["path"])
            stale.unlink()
manifest_file.parent.mkdir(parents=True, exist_ok=True)
manifest_file.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
print(json.dumps({"destination": str(target), "files": len(manifest), "bytes": sum((target / e["path"]).stat().st_size for e in manifest)}))
