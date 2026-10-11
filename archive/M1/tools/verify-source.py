#!/usr/bin/env python3
"""Packaging/static audit ONLY. Does not compile C# or execute product tests."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]

def audit() -> dict:
    checks: list[dict] = []
    def check(name: str, ok: bool, **detail: object) -> None:
        checks.append({'name': name, 'status': 'passed' if ok else 'failed', **detail})
    projects = sorted(ROOT.rglob('*.csproj'))
    check('five-projects-present', len(projects) == 5)
    for project in projects:
        tree = ET.parse(project)
        refs = tree.findall('.//ProjectReference')
        check('project-references:' + project.relative_to(ROOT).as_posix(),
              all((project.parent / r.attrib['Include'].replace('\\', '/')).is_file() for r in refs))
        check('no-external-packages:' + project.stem, not tree.findall('.//PackageReference'))
    solution = ET.parse(ROOT / 'TypeLay.slnx')
    check('solution-paths', all((ROOT / p.attrib['Path']).is_file() for p in solution.findall('.//Project')))
    ET.parse(ROOT / 'Directory.Build.props'); ET.parse(ROOT / 'NuGet.config')
    json.loads((ROOT / 'global.json').read_text(encoding='utf-8'))
    check('configuration-xml-json-parses', True)
    production = '\n'.join(p.read_text(encoding='utf-8') for p in (ROOT/'src').rglob('*.cs'))
    # A lexical tripwire, not a proof of no network or no key injection.
    risky_calls = re.findall(r'\b(?:SendInput|keybd_event|SetClipboardData|HttpClient|WebClient)\s*\(', production)
    check('lexical-forbidden-call-tripwire', not risky_calls)
    fixture = ROOT/'fixtures/browser/index.html'
    html = fixture.read_text(encoding='utf-8')
    check('fixture-has-no-remote-assets', not re.search(r'(?:src|href)=["\']https?://', html))
    script = re.search(r'<script>(.*?)</script>', html, re.S)
    check('fixture-script-present', script is not None)
    if shutil.which('node') and script:
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/'fixture.cjs'; path.write_text(script.group(1),encoding='utf-8')
            result = subprocess.run(['node','--check',str(path)],capture_output=True,text=True,timeout=10)
            check('browser-fixture-javascript-syntax', result.returncode == 0)
    else:
        checks.append({'name':'browser-fixture-javascript-syntax','status':'not-run','reason':'node-not-found'})
    refs = json.loads((ROOT/'reference/INPUTS.json').read_text(encoding='utf-8'))
    for item in refs['copied_files']:
        data=(ROOT/'reference'/item['stored_name']).read_bytes()
        check('unchanged-reference:' + item['stored_name'], hashlib.sha256(data).hexdigest()==item['sha256'])
    return {'suite':'source-packaging-audit','status':'passed' if all(c['status']=='passed' for c in checks) else 'incomplete-or-failed',
            'scope':'XML/JSON/paths/hashes/limited lexical tripwires/JS syntax ONLY', 'checks':checks,
            'does_not_prove':['C# compilation','C# unit tests','Win32 behavior','focus','global keyboard hook','IME','local inference','privacy isolation']}

def main() -> int:
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--output',type=Path)
    args=parser.parse_args()
    result=audit();text=json.dumps(result,ensure_ascii=False,indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(text+'\n',encoding='utf-8')
    print(text)
    return 0 if result['status']=='passed' else 1
if __name__=='__main__':
    raise SystemExit(main())
