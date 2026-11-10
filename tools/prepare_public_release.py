#!/usr/bin/env python3
"""Audit public source candidates and write a fresh source ZIP without Git history."""
import argparse
import hashlib
import re
import subprocess
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ROOT_FILES = {'.gitignore', 'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'build.ps1'}
FOLDERS = {'src', 'tests', 'tools', 'docs'}
ASSETS = {'assets/fedatather.svg', 'assets/boss_public.jpg'}
PATTERNS = {
    'private key': re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'),
    'GitHub token': re.compile(rb'(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})'),
    'AWS access key': re.compile(rb'(?:AKIA|ASIA)[A-Z0-9]{16}'),
    'Slack token': re.compile(rb'xox[baprs]-[A-Za-z0-9-]{20,}'),
    'assigned credential': re.compile(rb'''(?i)(?:api[_-]?key|client[_-]?secret|access[_-]?token|password)\s*[:=]\s*["'][A-Za-z0-9_+/.=-]{16,}["']'''),
    'personal Windows path': re.compile(rb'(?i)[A-Z]:[\\/]+Users[\\/]+(?!Public\b|Default\b)[A-Za-z0-9_.-]+'),
    'personal Linux path': re.compile(rb'/home/[a-zA-Z0-9_.-]+/'),
}


def git(*args):
    return subprocess.check_output(['git', '-C', str(ROOT), *args])


def candidates():
    return sorted(set(git('ls-files', '--cached', '--others', '--exclude-standard', '-z').decode().strip('\0').split('\0')) - {''})


def allowed(name):
    path = Path(name)
    extensions = {'src': {'.cs', '.manifest'}, 'tests': {'.cs', '.ps1', '.py'},
                  'tools': {'.cs', '.ps1', '.py'}, 'docs': {'.md', '.html', '.svg', '.png'}}
    return name in ROOT_FILES or name in ASSETS or (path.parts[0] in FOLDERS and path.suffix.lower() in extensions[path.parts[0]])


def audit(files):
    problems = []
    for name in files:
        path = ROOT / name
        if not allowed(name):
            problems.append(name + ': unexpected public file')
            continue
        if path.is_symlink() or not path.is_file():
            problems.append(name + ': symlink or non-file')
            continue
        data = path.read_bytes()
        for label, pattern in PATTERNS.items():
            if pattern.search(data):
                problems.append(name + ': ' + label)
        if path.suffix.lower() in {'.jpg', '.jpeg'} and (b'Exif\0\0' in data or b'http://ns.adobe.com/xap/' in data):
            problems.append(name + ': image contains EXIF or XMP metadata')
    return problems


def check_ignores():
    private = ['.env', '.aws/credentials', '.ssh/id_rsa', '.agents/state.json', '.codex/config.toml',
               'assets/boss_here.jpg', 'WebView2/Default/Cookies', 'settings.ini', 'settings.ini.folded.json',
               'exports/table.csv', 'dist/Fedatather.exe', 'build/private.txt']
    public = ['.env.example', 'assets/boss_public.jpg', 'assets/fedatather.svg',
              'docs/screenshots/phone-tabs.png', 'docs/demo/index.html', 'src/BrowserForm.cs']
    for name, ignored in [(name, True) for name in private] + [(name, False) for name in public]:
        result = subprocess.run(['git', '-C', str(ROOT), 'check-ignore', '--no-index', '-q', name])
        if (result.returncode == 0) != ignored:
            raise SystemExit('Incorrect ignore rule for ' + name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', action='store_true')
    args = parser.parse_args()
    check_ignores()
    files = candidates()
    problems = audit(files)
    if problems:
        raise SystemExit('\n'.join(problems))
    required = ROOT_FILES | ASSETS | {'docs/screenshots/' + name for name in ['phone-tabs.png', 'desktop.png', 'devtools.png', 'boss-mode.png']}
    missing = required - set(files)
    if missing:
        raise SystemExit('Missing public files: ' + ', '.join(sorted(missing)))
    print('PASS: audited {} public files; no configured secret or personal-path matches.'.format(len(files)))
    print('PASS: private data and build outputs ignored; public assets and screenshots remain trackable.')
    historical = git('rev-list', '--objects', '--all').decode()
    if ' assets/boss_here.jpg' in historical:
        print('NOTE: local historical refs contain the private Boss image. Source ZIP excludes all Git history.')
    if args.package:
        out = ROOT / 'build' / 'Fedatather-source.zip'
        out.parent.mkdir(exist_ok=True)
        with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name in files:
                archive.writestr('Fedatather/' + name, (ROOT / name).read_bytes())
        with zipfile.ZipFile(out) as archive:
            expected = {'Fedatather/' + name for name in files}
            if set(archive.namelist()) != expected or archive.testzip():
                raise SystemExit('Archive verification failed')
            for name in files:
                if archive.read('Fedatather/' + name) != (ROOT / name).read_bytes():
                    raise SystemExit('Archive content mismatch: ' + name)
        digest = hashlib.sha256(out.read_bytes()).hexdigest()
        (out.parent / 'Fedatather-source.sha256').write_text(digest + '  Fedatather-source.zip\n', encoding='utf-8')
        print('Created build/Fedatather-source.zip and verified every member; no .git directory or private image.')
    print('Pattern scanning cannot establish ownership or guarantee that every possible secret was detected.')


if __name__ == '__main__':
    main()
