"""Dependency-free structural checks. These do not compile C# or certify Windows behavior."""
from __future__ import annotations
import ast
import hashlib
import json
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
checks: list[dict[str, object]] = []

def check(name: str, fn) -> None:
    try:
        detail = fn()
        checks.append({"name": name, "status": "passed", "detail": detail})
    except Exception as exc:
        checks.append({"name": name, "status": "failed", "error": type(exc).__name__ + ': ' + str(exc)})

def projects():
    solution = ET.parse(ROOT / 'TypeLay.slnx')
    entries = [p.attrib['Path'] for p in solution.iter('Project')]
    assert len(entries) == 10 and len(set(entries)) == 10
    for path in entries:
        p = ROOT / path
        xml = ET.parse(p)
        for ref in xml.iter('ProjectReference'):
            assert (p.parent / ref.attrib['Include']).resolve().is_file(), ref.attrib
    ET.parse(ROOT / 'Directory.Build.props')
    ET.parse(ROOT / 'NuGet.config')
    ET.parse(ROOT / 'src/TypeLay.App/app.manifest')
    return {'projects': len(entries), 'level': 'XML syntax and file references only'}

def python_syntax():
    files = [p for folder in ('worker', 'tests/python', 'tools') for p in (ROOT / folder).rglob('*.py')]
    for p in files:
        text = p.read_text(encoding='utf-8-sig')
        ast.parse(text, filename=str(p))
        compile(text, str(p), 'exec')
    return {'files': len(files)}

def baseline():
    data = json.loads((ROOT / 'reference/INPUTS.json').read_text(encoding='utf-8'))
    for item in data['copied_files']:
        assert hashlib.sha256((ROOT / 'reference' / item['stored_name']).read_bytes()).hexdigest() == item['sha256'], item['stored_name']
    return 'Archived reference SHA-256 values match; product decisions are documented separately.'

def worker_boundary():
    forbidden = {'requests', 'http', 'urllib', 'socket', 'aiohttp', 'ftplib'}
    for p in (ROOT / 'worker').rglob('*.py'):
        for node in ast.walk(ast.parse(p.read_text(encoding='utf-8'))):
            names = [n.name for n in node.names] if isinstance(node, ast.Import) else [node.module or ''] if isinstance(node, ast.ImportFrom) else []
            assert not any(name.split('.')[0] in forbidden for name in names), str(p)
    return 'No direct networking imports in worker source; not an OS network sandbox.'

def safety_tripwires():
    app = ROOT / 'src/TypeLay.App'
    settings = (app / 'SettingsStore.cs').read_text(encoding='utf-8')
    controller = (app / 'SessionController.cs').read_text(encoding='utf-8')
    hook = (app / 'KeyboardHook.cs').read_text(encoding='utf-8')
    assert 'ExperimentalWriteback = false' in settings
    assert '_settings.Value.ExperimentalWriteback' in controller
    for forbidden in ('AutomationElement', 'HttpClient', 'SendInput', 'Clipboard', 'WriteSafety.Validate'):
        assert forbidden not in hook, forbidden
    broker = (ROOT / 'src/TypeLay.Broker/BrowserProvider.cs').read_text(encoding='utf-8')
    assert 'value.SetValue(plan.ResultText)' in broker
    assert '_quarantine.Add' in broker
    return 'Guard text/architecture checks only; does not prove atomicity, privacy or physical Tab behavior.'

def native_image():
    data = (ROOT / 'TypeLay.exe').read_bytes()
    assert data[:2] == b'MZ'
    offset = struct.unpack_from('<I', data, 0x3c)[0]
    assert data[offset:offset+4] == b'PE\0\0'
    machine = struct.unpack_from('<H', data, offset+4)[0]
    assert machine == 0x8664
    assert struct.unpack_from('<H', data, offset+24)[0] == 0x20b
    subsystem = struct.unpack_from('<H', data, offset+24+68)[0]
    assert subsystem == 2
    return {'machine': 'x64', 'subsystem': 'Windows GUI', 'bytes': len(data), 'role': 'bootstrap launcher only', 'executed_on_windows': False}

def fixture_script():
    node = shutil.which('node')
    if not node:
        return 'Node unavailable; JS syntax not run. XML/Python checks are independent.'
    html = (ROOT / 'fixtures/browser/index.html').read_text(encoding='utf-8')
    scripts = re.findall(r'<script(?:\s[^>]*)?>(.*?)</script>', html, re.S | re.I)
    assert scripts
    with tempfile.TemporaryDirectory() as folder:
        p = Path(folder) / 'fixture.js'
        p.write_text('\n'.join(scripts), encoding='utf-8')
        subprocess.run([node, '--check', str(p)], check=True, capture_output=True)
    return {'inline_scripts': len(scripts), 'level': 'JavaScript syntax only; no browser integration run'}

def package_hygiene():
    bad = []
    for p in ROOT.rglob('*'):
        rel = p.relative_to(ROOT)
        if any(part in ('bin','obj','.git','dist','artifacts') for part in rel.parts):
            continue
        if p.is_symlink() or p.suffix.lower() in ('.ttf','.otf','.woff','.woff2','.pfx','.pem','.key'):
            bad.append(str(rel))
    assert not bad, bad
    return 'No symlink, bundled font or private-key files in source delivery.'

for name, fn in [('solution-structure', projects), ('python-syntax', python_syntax),
                 ('reference-integrity', baseline), ('worker-network-imports', worker_boundary),
                 ('safety-static-tripwires', safety_tripwires), ('launcher-pe-header', native_image),
                 ('browser-fixture-js-syntax', fixture_script), ('package-hygiene', package_hygiene)]:
    check(name, fn)
report = {'schema': 1, 'suite': 'source-structural-checks', 'scope': 'Not C# compilation or Windows acceptance',
          'status': 'passed' if all(c['status'] == 'passed' for c in checks) else 'failed', 'checks': checks}
print(json.dumps(report, ensure_ascii=False, indent=2))
sys.exit(0 if report['status'] == 'passed' else 1)
