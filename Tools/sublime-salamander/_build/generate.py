#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Генератор файлов пакета Sublime Text из исходников расширения VS Code.

Единственный источник правды — Tools/vscode-salamander:
  syntaxes/salamander.tmLanguage.json  ->  Salamander.sublime-syntax
  snippets/salamander.json             ->  Salamander.sublime-completions

Правите грамматику или сниппеты — правьте их там и перезапускайте:
  python Tools/sublime-salamander/_build/generate.py

Скрипт лежит в подпапке нарочно: Sublime грузит как плагины только .py из
корня пакета, а этот файл плагином не является.
"""
import json
import os
import re
import sys

try:
    import yaml
except ImportError:
    sys.exit("нужен PyYAML: pip install pyyaml")

HERE = os.path.dirname(os.path.abspath(__file__))
PKG = os.path.dirname(HERE)
VSCODE = os.path.join(os.path.dirname(PKG), "vscode-salamander")

GRAMMAR = os.path.join(VSCODE, "syntaxes", "salamander.tmLanguage.json")
SNIPPETS = os.path.join(VSCODE, "snippets", "salamander.json")
OUT_SYNTAX = os.path.join(PKG, "Salamander.sublime-syntax")
OUT_COMPLETIONS = os.path.join(PKG, "Salamander.sublime-completions")

HEADER = (
    "%YAML 1.2\n"
    "---\n"
    "# СГЕНЕРИРОВАНО из Tools/vscode-salamander/syntaxes/salamander.tmLanguage.json\n"
    "# скриптом _build/generate.py — руками не править, правки потеряются.\n"
)


# ---------------------------------------------------------------------------
# tmLanguage -> sublime-syntax
# ---------------------------------------------------------------------------

def ctx_name(include):
    if not include.startswith("#"):
        raise ValueError("поддержаны только локальные include (#name): " + include)
    return include[1:].replace("-", "_")


def captures(caps):
    out = {}
    for k, v in (caps or {}).items():
        if "name" in v:
            out[int(k)] = v["name"]
        if "patterns" in v:
            raise ValueError("patterns внутри captures не поддержаны")
    return out


def convert_rule(rule, anon):
    """Одно правило tmLanguage -> список правил контекста Sublime."""
    if "include" in rule:
        return [{"include": ctx_name(rule["include"])}]

    if "match" in rule:
        r = {"match": rule["match"]}
        if "name" in rule:
            r["scope"] = rule["name"]
        if rule.get("captures"):
            r["captures"] = captures(rule["captures"])
        return [r]

    if "begin" in rule:
        body = []
        if "name" in rule:
            body.append({"meta_scope": rule["name"]})
        end = {"match": rule["end"], "pop": True}
        end_caps = rule.get("endCaptures") or rule.get("captures")
        if end_caps:
            end["captures"] = captures(end_caps)
        # конец проверяем раньше вложенных правил — как в TextMate
        # (applyEndPatternLast у нас нигде не выставлен)
        body.append(end)
        for sub in rule.get("patterns", []):
            body.extend(convert_rule(sub, anon))
        r = {"match": rule["begin"], "push": body}
        begin_caps = rule.get("beginCaptures") or rule.get("captures")
        if begin_caps:
            r["captures"] = captures(begin_caps)
        return [r]

    if "patterns" in rule:
        out = []
        for sub in rule["patterns"]:
            out.extend(convert_rule(sub, anon))
        return out

    raise ValueError("неизвестный вид правила: " + json.dumps(rule, ensure_ascii=False))


def build_syntax(tm):
    anon = []
    contexts = {"main": []}
    for rule in tm["patterns"]:
        contexts["main"].extend(convert_rule(rule, anon))
    for name, rule in tm["repository"].items():
        contexts[name.replace("-", "_")] = convert_rule(rule, anon)

    return {
        "name": tm["name"],
        "scope": tm["scopeName"],
        "version": 2,
        "file_extensions": list(tm.get("fileTypes", ["sal"])),
        "contexts": contexts,
    }


# ---------------------------------------------------------------------------
# сниппеты VS Code -> sublime-completions
# ---------------------------------------------------------------------------

CHOICE = re.compile(r"\$\{(\d+)\|([^|}]*)\|\}")


def to_sublime_snippet(body):
    text = "\n".join(body) if isinstance(body, list) else body
    # ${1|a,b|} Sublime не понимает — оставляем первый вариант как заглушку
    text = CHOICE.sub(lambda m: "${%s:%s}" % (m.group(1), m.group(2).split(",")[0]), text)
    # отступы сниппетов VS Code — табы; Sublime сам приведёт их к настройкам вида
    return text


def build_completions(snippets):
    items = []
    for title, s in snippets.items():
        prefixes = s["prefix"] if isinstance(s["prefix"], list) else [s["prefix"]]
        for prefix in prefixes:
            items.append({
                "trigger": prefix,
                "annotation": title,
                "contents": to_sublime_snippet(s["body"]),
                "kind": "snippet",
                "details": s.get("description", ""),
            })
    return {"scope": "source.salamander", "completions": items}


def write(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("записан", os.path.relpath(path, os.path.dirname(os.path.dirname(PKG))))


def main():
    with open(GRAMMAR, encoding="utf-8") as f:
        tm = json.load(f)
    syntax = build_syntax(tm)
    write(OUT_SYNTAX, HEADER + yaml.safe_dump(
        syntax, allow_unicode=True, sort_keys=False, width=1000, default_flow_style=False))

    with open(SNIPPETS, encoding="utf-8") as f:
        snippets = json.load(f)
    write(OUT_COMPLETIONS, json.dumps(build_completions(snippets), ensure_ascii=False, indent=4) + "\n")


if __name__ == "__main__":
    main()
