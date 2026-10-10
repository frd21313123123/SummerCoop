"""Read local Unity files. Never patch or redistribute game resources."""
from pathlib import Path
import argparse
import json
import sys
from collections import Counter

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / '.tools' / 'python'))
import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('game')
    parser.add_argument('--output', default='.local/inspection')
    args = parser.parse_args()
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    base = Path(args.game) / 'mysummercar_Data'
    generator = TypeTreeGenerator('5.0.0f4')
    generator.load_local_dll_folder(str(base / 'Managed'))
    # Loading explicit files avoids unrelated texture packs and launcher DLLs.
    for filename in ['mainData', 'level0', 'level1', 'level2']:
        env = UnityPy.load(str(base / filename))
        env.typetree_generator = generator
        objects = list(env.objects)
        counts = Counter(x.type.name for x in objects)
        print(filename, counts, flush=True)
        names = {}
        transforms = {}
        fsms = []
        for obj in objects:
            if obj.type.name == 'GameObject':
                data = obj.read_typetree()
                names[obj.path_id] = data.get('m_Name', '')
            elif obj.type.name == 'Transform':
                data = obj.read_typetree()
                transforms[obj.path_id] = data
            elif obj.type.name == 'MonoBehaviour':
                try:
                    data = obj.read_typetree()
                except Exception:
                    continue
                if 'fsm' in data:
                    fsms.append({'id': obj.path_id, 'data': data})
        for fsm in fsms:
            go = fsm['data'].get('m_GameObject', {})
            fsm['object'] = names.get(go.get('m_PathID'), str(go))
        payload = {'file': filename, 'counts': counts, 'names': names,
                   'transforms': transforms, 'fsms': fsms}
        (output / (filename + '.json')).write_text(json.dumps(payload, ensure_ascii=False), encoding='utf-8')
        print('FSMs:', len(fsms), 'objects:', len(names), flush=True)

if __name__ == '__main__':
    main()
