"""Structural validation only; does not replace the .NET build/specifications."""
from pathlib import Path
import json
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
projects = list(root.rglob('*.csproj'))
packages = {p.attrib['Include'] for p in ET.parse(root/'Directory.Packages.props').iter('PackageVersion')}
for project in projects:
    tree = ET.parse(project)
    for ref in tree.iter('ProjectReference'):
        assert (project.parent/ref.attrib['Include']).resolve().is_file(), (project, ref.attrib)
    for ref in tree.iter('PackageReference'):
        assert ref.attrib['Include'] in packages, ref.attrib
for item in ET.parse(root/'MidiWorkbench.slnx').iter('Project'):
    assert (root/item.attrib['Path']).is_file(), item.attrib
for file in [root/'Directory.Build.props', root/'Directory.Packages.props']:
    ET.parse(file)
for file in root.rglob('*.json'):
    json.loads(file.read_text())
for file in root.rglob('*.md'):
    for url in re.findall(r'\]\(([^)]+)\)', file.read_text()):
        if '://' not in url and not url.startswith('#'):
            assert (file.parent/url.split('#')[0]).exists(), (file, url)
for project in ['Domain', 'Generation', 'Engine', 'Persistence']:
    for file in (root/'src'/f'MidiWorkbench.{project}').glob('*.cs'):
        assert 'using Avalonia' not in file.read_text(), file
print(f'PASS: {len(projects)} projects, XML/JSON, references, local documentation links, UI-independent libraries')
