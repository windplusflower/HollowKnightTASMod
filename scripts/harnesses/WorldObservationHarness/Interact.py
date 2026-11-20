"""Send one bounded input/observation command to the opt-in interactive harness."""
import argparse
import importlib.util
import json
import time
from pathlib import Path

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('directory', type=Path)
p.add_argument('--frame', type=int)
p.add_argument('--source', type=Path)
p.add_argument('--prefix', type=int)
p.add_argument('--segments', nargs='*', default=[])
p.add_argument('--details', nargs='*', default=[])
p.add_argument('--all', action='store_true')
p.add_argument('--inactive', action='store_true')
p.add_argument('--quit', action='store_true')
a = p.parse_args()
number = 1
while (a.directory / f'command-{number:04}.json').exists():
    number += 1
stem = f'command-{number:04}'
command = {'detailNames': a.details, 'view': 'all' if a.all else 'world', 'includeInactive': a.inactive}
if a.quit:
    command['quit'] = True
if a.frame is not None:
    command['frame'] = a.frame
if a.source:
    if a.prefix is None:
        p.error('--source requires --prefix')
    spec = importlib.util.spec_from_file_location('author', Path(__file__).with_name('Author-Fixture.py'))
    author = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(author)
    movie = author.build(a.source, a.prefix, a.segments + ['15000:-'])
    destination = a.directory / f'{stem}-movie.hktas'
    destination.write_text('\n'.join(json.dumps(r, separators=(',', ':')) for r in movie) + '\n', encoding='utf-8', newline='\n')
    command['moviePath'] = str(destination.resolve())
temporary = a.directory / f'{stem}.tmp'
temporary.write_text(json.dumps(command), encoding='utf-8')
temporary.replace(a.directory / f'{stem}.json')
if a.quit:
    print('quit requested')
    raise SystemExit(0)
for _ in range(600):
    if (a.directory / f'{stem}-done.json').exists():
        break
    time.sleep(.1)
else:
    raise SystemExit('Command still pending; inspect the harness report before issuing more input.')
world = json.loads((a.directory / f'{stem}-world.json').read_text())
print(stem, 'frame', world['movieFrame'], 'scene', world['metadata']['activeScene']['name'])
for o in world['objects']:
    hero = o.get('hero')
    custom = any(c.get('assembly') == 'EnviousMarmu' for c in o.get('components', []))
    interesting = hero or custom or (world['metadata']['activeScene']['name'] == 'GG_Workshop' and any(n in o['name'] for n in ('Bench', 'Inventory', 'Charms', 'Cursor')))
    if not interesting:
        continue
    result = {'name': o['name'], 'id': o.get('id', o.get('objectId')), 'position': o.get('transform', {}).get('position')}
    if hero:
        result.update(resources=hero['resources'], actor=hero['actor'], charms=hero['charms'],
                      flags=[f['name'] for f in hero.get('cState', {}).get('fields', []) if f.get('value') is True])
    if custom:
        result.update(health=[(h.get('hp'), h.get('isInvincible')) for h in o.get('health',[])], velocity=[b.get('velocity') for b in o.get('rigidbodies',[])])
    result['fsms'] = [{'name': f['name'], 'state': f.get('activeState'),
                      'variables': {v['name']: v.get('raw', {}).get('value') for v in f.get('variables', [])
                                    if 'FsmInt' in v.get('type', '')} if not (hero or custom) else None} for f in o.get('fsms', [])]
    print(json.dumps(result, ensure_ascii=False))
