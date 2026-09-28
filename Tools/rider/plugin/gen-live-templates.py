#!/usr/bin/env python3
"""Live templates для JetBrains из сниппетов VS Code (единый источник правды).

    python3 gen-live-templates.py            # ../../vscode-salamander/snippets/salamander.json
                                             #   -> resources/liveTemplates/Salamander.xml

${1:Name} / $1 / $0 превращаются в переменные $NAME$ / $V1$ / $END$, табы — в 4 пробела.
Вложенные плейсхолдеры (${3: -> ${4:float}}) раскрываются: внешний текст с внутренней переменной.
"""
import json, os, re, sys
from xml.sax.saxutils import quoteattr

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, '..', '..', 'vscode-salamander', 'snippets', 'salamander.json')
DST = os.path.join(HERE, 'resources', 'liveTemplates', 'Salamander.xml')


def parse(body, vars_, names):
    """Разобрать тело сниппета: вернуть текст JetBrains-шаблона, заполнив vars_ (порядок = порядок табов)."""
    out, i = [], 0
    while i < len(body):
        c = body[i]
        if c == '$' and i + 1 < len(body) and body[i + 1] == '{':
            j, depth = i + 2, 1
            while j < len(body) and depth:
                if body[j] == '{': depth += 1
                elif body[j] == '}': depth -= 1
                j += 1
            inner = body[i + 2:j - 1]
            m = re.match(r'(\d+)(?::(.*))?$', inner, re.S)
            num, default = m.group(1), m.group(2) or ''
            if '$' in default:                       # вложенный плейсхолдер — раскрыть на месте
                out.append(parse(default, vars_, names))
            else:
                out.append(var(num, default, vars_, names))
            i = j
        elif c == '$' and i + 1 < len(body) and body[i + 1].isdigit():
            j = i + 1
            while j < len(body) and body[j].isdigit(): j += 1
            out.append(var(body[i + 1:j], '', vars_, names))
            i = j
        else:
            out.append('$$' if c == '$' else c)
            i += 1
    return ''.join(out)


def var(num, default, vars_, names):
    if num == '0':
        return '$END$'
    if num in names:
        return '$' + names[num] + '$'
    base = re.sub(r'[^A-Za-z0-9]', '_', default).upper().strip('_') if re.match(r'[A-Za-z_]', default or '') else ''
    name = base or 'V' + num
    while name in names.values() or name == 'END':
        name += '_'
    names[num] = name
    vars_.append((name, default))
    return '$' + name + '$'


def main():
    snippets = json.load(open(SRC, encoding='utf-8'))
    rows = ['<templateSet group="Salamander">']
    for title, s in snippets.items():
        body = '\n'.join(s['body']) if isinstance(s['body'], list) else s['body']
        body = body.replace('\t', '    ')
        vars_, names = [], {}
        text = parse(body, vars_, names)
        rows.append('  <template name=%s value=%s description=%s toReformat="false" toShortenFQNames="false">'
                    % (quoteattr(s['prefix']), quoteattr(text), quoteattr(s.get('description', title))))
        for name, default in vars_:
            rows.append('    <variable name=%s expression="" defaultValue=%s alwaysStopAt="true"/>'
                        % (quoteattr(name), quoteattr('"%s"' % default.replace('"', '\\"') if default else '')))
        rows.append('    <context><option name="SALAMANDER" value="true"/></context>')
        rows.append('  </template>')
    rows.append('</templateSet>')
    os.makedirs(os.path.dirname(DST), exist_ok=True)
    with open(DST, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(rows) + '\n')
    print('live templates:', len(snippets), '->', os.path.relpath(DST, HERE))


if __name__ == '__main__':
    sys.exit(main())
