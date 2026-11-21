"""Build release notices from the exact runtime packs used by a .NET publish.

Run after `dotnet publish`:
    python tools/make_notices.py src/obj/project.assets.json path/to/YTM-Overlay.runtimeconfig.json release/NOTICES.txt
The runtime config identifies the included framework versions; project.assets.json
identifies package caches and any separately restored host pack. If an installed
SDK pack is not in the NuGet cache, the matching official NuGet package is read
for its notices. Failure is intentional rather than shipping stale notices.
"""
import io
import json
import re
import sys
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
if len(sys.argv) != 4:
    raise SystemExit('Usage: python tools/make_notices.py <project.assets.json> <runtimeconfig.json> <output.txt>')
assets = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8'))
config = json.loads(Path(sys.argv[2]).read_text(encoding='utf-8'))
folders = [Path(p) for p in assets['packageFolders']]
frameworks = assets['project']['frameworks']
if len(frameworks) != 1:
    raise SystemExit('Expected exactly one target framework; review release notices.')
deps = next(iter(frameworks.values())).get('downloadDependencies', [])
included = {f['name']: f['version'] for f in config['runtimeOptions']['includedFrameworks']}


def exact_version(name, fallback):
    matches = [d for d in deps if d['name'].lower() == name.lower()]
    if len(matches) > 1:
        raise SystemExit('Ambiguous runtime dependency: ' + name)
    if matches:
        m = re.fullmatch(r'\[([^,\]]+),\s*([^\]]+)\]', matches[0]['version'])
        if not m or m.group(1) != m.group(2):
            raise SystemExit('Expected an exact restored version for ' + name)
        version = m.group(1)
        if fallback and version != fallback:
            raise SystemExit('Runtime config and restored package disagree for ' + name)
    else:
        version = fallback
    if not version or not re.fullmatch(r'[0-9A-Za-z.+-]+', version):
        raise SystemExit('Cannot determine exact runtime version for ' + name)
    return version


def pack_files(name, version, wanted):
    for folder in folders:
        path = folder / name.lower() / version.lower()
        if path.is_dir():
            files = {p.name.lower(): p for p in path.iterdir() if p.is_file()}
            if all(w.lower() in files for w in wanted):
                return [files[w.lower()].read_text(encoding='utf-8-sig') for w in wanted]
    # A native Windows SDK can contain runtime packs without restoring them to
    # the NuGet cache. Get notices from the exact public Microsoft package.
    package = name.lower()
    url = 'https://api.nuget.org/v3-flatcontainer/{0}/{1}/{0}.{1}.nupkg'.format(package, version.lower())
    with urllib.request.urlopen(url, timeout=90) as response:
        data = response.read()
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        files = {p.lower(): p for p in archive.namelist()}
        return [archive.read(files[w.lower()]).decode('utf-8-sig') for w in wanted]


def normalize(text):
    return text.replace('\r\n', '\n').strip()


def section(title, content):
    return '\n\n' + '=' * 72 + '\n' + title + '\n' + '=' * 72 + '\n\n' + normalize(content) + '\n'


core_name = 'Microsoft.NETCore.App.Runtime.win-x64'
desktop_name = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'
host_name = 'Microsoft.NETCore.App.Host.win-x64'
core_version = exact_version(core_name, included.get('Microsoft.NETCore.App'))
desktop_version = exact_version(desktop_name, included.get('Microsoft.WindowsDesktop.App'))
# The apphost usually matches the framework version; if separately restored,
# its exact version takes precedence. Do not silently assume a missing version.
host_entries = [d for d in deps if d['name'].lower() == host_name.lower()]
host_version = exact_version(host_name, None if host_entries else core_version)
core_license, core_notices = pack_files(core_name, core_version, ['LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'])
desktop_license, = pack_files(desktop_name, desktop_version, ['LICENSE'])
host_license, host_notices = pack_files(host_name, host_version, ['LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'])
text = ('YTM Overlay v0.0.1 beta — release notices\n'
        'Keep this file with the downloadable executable.\n'
        'This unofficial project is not affiliated with YouTube, Google, Twitch or Kick.\n'
        'External services have their own terms and privacy policies.\n'
        'The Windows SDK targeting pack is provided under https://aka.ms/WinSDKLicenseURL.\n')
text += section('Project license', (ROOT / 'LICENSE').read_text(encoding='utf-8'))
text += section('.NET Runtime ' + core_version + ' license', core_license)
text += section('.NET Runtime ' + core_version + ' third-party notices', core_notices)
text += section('.NET Windows Desktop Runtime ' + desktop_version + ' license', desktop_license)
text += section('.NET app host ' + host_version + ' license', host_license)
if normalize(host_notices) != normalize(core_notices):
    text += section('.NET app host ' + host_version + ' third-party notices', host_notices)
else:
    text += '\nThe app host third-party notices are identical to those for the .NET Runtime above.\n'
output = Path(sys.argv[3])
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(text, encoding='utf-8', newline='\n')
print('Wrote', output, 'for runtime', core_version, 'desktop', desktop_version, 'host', host_version)
