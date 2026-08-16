from pathlib import Path

p = Path('LocalPfScanner.cs')
s = p.read_text(encoding='utf-8')
old = 'addon->AtkUnitBase'
new = 'addon->AddonLookingForGroupBase.AtkUnitBase'
count = s.count(old)
if count != 6:
    raise SystemExit(f'expected 6 AddonLookingForGroup AtkUnitBase accesses, found {count}')
s = s.replace(old, new)
p.write_text(s, encoding='utf-8')
