# Russian texts of the wallet's code that have no English in src/MinterWallet/Translations.cs: C# literals (an
# interpolation becomes {0}, {1}, ...; strings inside interpolations count too) and XAML attribute values.
# Texts put together from pieces show up as their pieces; those need a whole template in Translations.cs, which the
# English UI check (tests/MinterWallet.UiChecks) catches on screen. Run: py tools/i18n-missing.py
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CYR = re.compile('[А-Яа-яЁё]')
DICTIONARY = os.path.join(ROOT, 'src', 'MinterWallet', 'Translations.cs')


def cs_strings(text):
    out = []
    i = 0
    while i < len(text):
        c = text[i]
        if text.startswith('//', i):
            j = text.find('\n', i)
            i = len(text) if j < 0 else j
            continue
        interp = verbatim = False
        if c == '$' and i + 1 < len(text) and text[i + 1] == '"':
            interp = True
            i += 1
        elif c == '@' and i + 1 < len(text) and text[i + 1] == '"':
            verbatim = True
            i += 1
        elif c == "'":
            j = i + 1
            while j < len(text) and text[j] != "'":
                j += 2 if text[j] == '\\' else 1
            i = j + 1
            continue
        if i < len(text) and text[i] == '"':
            if text.startswith('"""', i):
                i = text.find('"""', i + 3) + 3
                continue
            j = i + 1
            buf, depth, n, expr = '', 0, 0, ''
            while j < len(text):
                ch = text[j]
                if interp and depth == 0 and ch == '{':
                    if j + 1 < len(text) and text[j + 1] == '{':
                        buf += '{'
                        j += 2
                        continue
                    depth, expr = 1, ''
                    j += 1
                    continue
                if interp and depth > 0:
                    if ch == '"':
                        k = j + 1
                        while k < len(text) and text[k] != '"':
                            k += 2 if text[k] == '\\' else 1
                        expr += text[j:k + 1]
                        j = k + 1
                        continue
                    if ch == '{':
                        depth += 1
                    elif ch == '}':
                        depth -= 1
                        if depth == 0:
                            buf += '{' + str(n) + '}'
                            n += 1
                            j += 1
                            out.extend(cs_strings(expr))
                            continue
                    expr += ch
                    j += 1
                    continue
                if interp and ch == '}' and j + 1 < len(text) and text[j + 1] == '}':
                    buf += '}'
                    j += 2
                    continue
                if not verbatim and ch == '\\':
                    nxt = text[j + 1]
                    buf += {'n': '\n', 't': '\t', '"': '"', '\\': '\\'}.get(nxt, '\\' + nxt)
                    j += 2
                    continue
                if ch == '"':
                    if verbatim and j + 1 < len(text) and text[j + 1] == '"':
                        buf += '"'
                        j += 2
                        continue
                    break
                buf += ch
                j += 1
            out.append(buf)
            i = j + 1
            continue
        i += 1
    return out


def axaml_strings(text):
    out = []
    for m in re.finditer(r'(\w[\w.:]*)="([^"]*)"', text):
        value = m.group(2)
        if not CYR.search(value):
            continue
        fmt = re.search(r"StringFormat='([^']*)'", value)
        if fmt:
            out.append(fmt.group(1))
        elif not value.startswith('{'):
            out.append(value.replace('&#10;', '\n'))
    return out


known = set()
pairs = cs_strings(open(DICTIONARY, encoding='utf-8').read())
for k in range(0, len(pairs) - 1, 2):
    known.add(pairs[k])

missing = {}
for folder in ('src/MinterWallet', 'src/MinterWallet.Core', 'src/MinterWallet.Api'):
    for base, dirs, names in os.walk(os.path.join(ROOT, folder)):
        dirs[:] = [d for d in dirs if d not in ('bin', 'obj')]
        for name in names:
            path = os.path.join(base, name)
            if path == DICTIONARY or not (name.endswith('.cs') or name.endswith('.axaml')):
                continue
            text = open(path, encoding='utf-8').read()
            for s in cs_strings(text) if name.endswith('.cs') else axaml_strings(text):
                if CYR.search(s) and s not in known:
                    missing.setdefault(s, os.path.relpath(path, ROOT))

for s, where in sorted(missing.items()):
    print(f'{where}: {s!r}')
print(f'{len(missing)} without English', file=sys.stderr)
sys.exit(1 if missing else 0)
