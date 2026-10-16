import re

with open('launcher/ru.inc', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace('MWCoop', 'SummerCoop')
content = content.replace('MWCOOP', 'SUMMERCOOP')
content = content.replace('Ready for a winter together?', 'Ready for a summer together?')

target = '{ L"Kept in summercoop-launcher.ini and SummerCoop\\\\mwcoop.ini",'
replacement = '{ L"Kept in summercoop-launcher.ini", L"Хранятся в summercoop-launcher.ini" },\n    ' + target
content = content.replace(target, replacement)

with open('launcher/ru.inc', 'w', encoding='utf-8', newline='\n') as f:
    f.write(content)

print('Updated ru.inc successfully')
