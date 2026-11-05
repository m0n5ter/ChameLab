"""Check the website translations against the pages and en.json.

    python website/i18n/check.py

Every data-i18n key of index.html, download/index.html and guide/index.html must be in en.json, every translation
must have exactly the keys of en.json, and each translated string must keep the English
markup: the same tags with the same attributes (links, code, bold), in any order.
en.json is the English text of the pages; update it with the pages.
"""
import json
import pathlib
import re
import sys

SITE = pathlib.Path(__file__).resolve().parents[1]
TAG = re.compile(r'<(/?[a-z0-9]+)((?:\s+[a-z-]+="[^"]*")*)\s*/?>')


def page_keys():
    keys = set()
    for page in ('index.html', 'download/index.html', 'guide/index.html'):
        html = (SITE / page).read_text('utf-8')
        keys |= set(re.findall(r'data-i18n="([^"]+)"', html))
        for attrs in re.findall(r'data-i18n-attr="([^"]+)"', html):
            keys |= {pair.split(':')[1] for pair in attrs.split(',')}
        keys |= set(re.findall(r"\bt\('([^']+)'", html))
        keys |= set(re.findall(r"\['(js\.[^']+)'", html))
    return keys


def tags(html):
    return sorted(m.group(0) for m in TAG.finditer(html))


en = json.loads((SITE / 'i18n' / 'en.json').read_text('utf-8'))
problems = []
missing = page_keys() - set(en)
if missing:
    problems.append(f'en.json lacks {sorted(missing)}')
for path in sorted((SITE / 'i18n').glob('*.json')):
    if path.name == 'en.json':
        continue
    tr = json.loads(path.read_text('utf-8'))
    for key in sorted(set(en) - set(tr)):
        problems.append(f'{path.name}: missing {key}')
    for key in sorted(set(tr) - set(en)):
        problems.append(f'{path.name}: unknown {key}')
    for key in sorted(set(en) & set(tr)):
        if tags(en[key]) != tags(tr[key]):
            problems.append(f'{path.name}: {key} markup differs')
print('\n'.join(problems) or 'All translations complete.')
sys.exit(1 if problems else 0)
