// Расширение VS Code для Salamander (.script).
// Три возможности:
//  1) диагностика: на сохранение запускается DslCheck (наш же компилятор),
//     ошибки подсвечиваются прямо в файлах;
//  2) автодополнение: манифест API хоста (salamander-api.json, игра выгружает его
//     при Play) + символы из .script-файлов воркспейса; контекстные подсказки
//     после "Engine.", "ИмяApi.", "ИмяЕнума." и после "event ";
//  3) hover: сигнатуры методов API и событий.

import * as vscode from 'vscode';
import * as cp from 'child_process';
import * as fs from 'fs';
import * as path from 'path';

// ===========================================================================
// Модель манифеста API (salamander-api.json)
// ===========================================================================

interface ApiParam { name: string; type: string; doc?: string; }
interface ApiMethod { name: string; summary?: string; params: ApiParam[]; returns: string; }
interface ApiClass { name: string; methods: ApiMethod[]; }
interface PropDef { name: string; type: string; readOnly: boolean; doc?: string; }
interface ClassDef { name: string; summary?: string; props: PropDef[]; }
interface EnumDef { name: string; summary?: string; members: string[]; }
interface EventDef { name: string; summary?: string; params: ApiParam[]; }
interface Manifest {
    apiVersion: number;
    enums: EnumDef[];
    classes: ClassDef[];
    apis: ApiClass[];
    events: EventDef[];
    archetypes?: { name: string; summary?: string; knownIds?: string[]; events: EventDef[] }[];
}

// Встроенный класс Engine стабилен и не входит в манифест — таблица зашита.
const ENGINE_METHODS: ApiMethod[] = [
    { name: 'EnableTrigger', summary: 'Включить триггер.', params: [{ name: 'trigger', type: 'trigger' }], returns: 'void' },
    { name: 'DisableTrigger', summary: 'Выключить триггер.', params: [{ name: 'trigger', type: 'trigger' }], returns: 'void' },
    { name: 'IsTriggerEnabled', summary: 'Включён ли триггер.', params: [{ name: 'trigger', type: 'trigger' }], returns: 'bool' },
    { name: 'ActivateTrigger', summary: 'Запустить action Do триггера отдельным файбером.', params: [{ name: 'trigger', type: 'trigger' }], returns: 'Fiber' },
    { name: 'KillAll', summary: 'Убить все файберы триггера.', params: [{ name: 'trigger', type: 'trigger' }], returns: 'void' },
    { name: 'Kill', summary: 'Убить файбер.', params: [{ name: 'fiber', type: 'Fiber' }], returns: 'void' },
    { name: 'IsAlive', summary: 'Жив ли файбер.', params: [{ name: 'fiber', type: 'Fiber' }], returns: 'bool' },
    { name: 'EnableModule', summary: 'Включить модуль.', params: [{ name: 'name', type: 'string' }], returns: 'void' },
    { name: 'DisableModule', summary: 'Выключить модуль.', params: [{ name: 'name', type: 'string' }], returns: 'void' },
    { name: 'IsModuleEnabled', summary: 'Включён ли модуль.', params: [{ name: 'name', type: 'string' }], returns: 'bool' },
    { name: 'IsModuleLoaded', summary: 'Загружен ли модуль.', params: [{ name: 'name', type: 'string' }], returns: 'bool' },
    { name: 'Time', summary: 'Время с загрузки, сек.', params: [], returns: 'float' },
    { name: 'DeltaTime', summary: 'Длительность последнего тика, сек.', params: [], returns: 'float' },
    { name: 'Log', summary: 'Сообщение в лог.', params: [{ name: 'message', type: 'string' }], returns: 'void' },
    { name: 'Warn', summary: 'Предупреждение в лог.', params: [{ name: 'message', type: 'string' }], returns: 'void' },
    { name: 'Error', summary: 'Ошибка в лог.', params: [{ name: 'message', type: 'string' }], returns: 'void' },
    { name: 'IsValid', summary: 'Жив ли хэндл сущности.', params: [{ name: 'entity', type: 'entity' }], returns: 'bool' },
    { name: 'Attach', summary: 'Подписать listener на сущность; возвращает хэндл подписки.', params: [{ name: 'listener', type: 'listener' }, { name: 'entity', type: 'entity' }], returns: 'Subscription' },
    { name: 'Detach', summary: 'Снять одну подписку по хэндлу.', params: [{ name: 'sub', type: 'Subscription' }], returns: 'void' },
    { name: 'DetachAll', summary: 'Снять все подписки этого listener с сущности.', params: [{ name: 'listener', type: 'listener' }, { name: 'entity', type: 'entity' }], returns: 'void' },
    { name: 'IsSubscribed', summary: 'Жива ли подписка.', params: [{ name: 'sub', type: 'Subscription' }], returns: 'bool' },
    { name: 'TriggerExists', summary: 'Существует ли триггер с таким именем.', params: [{ name: 'name', type: 'string' }], returns: 'bool' },
    { name: 'ClassExists', summary: 'Существует ли класс с таким именем.', params: [{ name: 'name', type: 'string' }], returns: 'bool' },
];

const KEYWORDS = [
    'trigger', 'class', 'enum', 'listener', 'self', 'pass', 'disabled', 'func', 'action', 'event', 'const', 'var',
    'if', 'else', 'while', 'for', 'in', 'break', 'continue', 'return',
    'wait', 'until', 'spawn', 'new', 'true', 'false', 'null',
];
const TYPES = ['int', 'float', 'bool', 'string', 'Fiber', 'Subscription', 'List', 'Map'];

// ===========================================================================
// Состояние
// ===========================================================================

let manifest: Manifest | null = null;
let manifestPath: string | null = null;

interface WsClass { funcs: ApiMethod[]; fields: { name: string; type: string }[]; }
interface WsFileSymbols {
    triggers: string[];
    classes: Map<string, WsClass>;
    enums: Map<string, string[]>;
}
const wsIndex = new Map<string, WsFileSymbols>(); // uri.toString() -> символы

let diagnostics: vscode.DiagnosticCollection;
let output: vscode.OutputChannel;
let checkTimer: ReturnType<typeof setTimeout> | undefined;
let autoCheckerPath: string | undefined; // найденный в воркспейсе DslCheck, если путь не задан

// Подстановка ${workspaceFolder} в путях из настроек — чтобы .vscode/settings.json
// модкита был портируемым (без абсолютных путей на конкретной машине).
function resolveVars(p: string): string {
    if (!p) return p;
    const folders = vscode.workspace.workspaceFolders;
    const ws = folders && folders.length > 0 ? folders[0].uri.fsPath : '';
    return ws ? p.split('${workspaceFolder}').join(ws) : p;
}

// ===========================================================================
// Активация
// ===========================================================================

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    output = vscode.window.createOutputChannel('Salamander');
    diagnostics = vscode.languages.createDiagnosticCollection('salamander');
    context.subscriptions.push(output, diagnostics);

    await loadManifest();
    await locateChecker();
    await indexWorkspace();

    // следим за манифестом API: игра перезаписывает его при Play
    const mw = vscode.workspace.createFileSystemWatcher('**/salamander-api.json');
    mw.onDidChange(() => { loadManifest(); scheduleCheck(); });
    mw.onDidCreate(() => { loadManifest(); scheduleCheck(); });
    context.subscriptions.push(mw);

    // индекс символов воркспейса
    context.subscriptions.push(
        vscode.workspace.onDidOpenTextDocument(d => { if (d.languageId === 'salamander') indexDocument(d); }),
        vscode.workspace.onDidChangeTextDocument(e => {
            if (e.document.languageId === 'salamander') indexDocument(e.document);
        }),
        vscode.workspace.onDidSaveTextDocument(d => {
            const cfg = vscode.workspace.getConfiguration('salamander');
            if (!cfg.get<boolean>('checkOnSave', true)) return;
            if (d.languageId === 'salamander' || d.fileName.endsWith('module.json')) scheduleCheck();
        }),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('salamander.check', () => runCheck()),
        vscode.languages.registerCompletionItemProvider('salamander', { provideCompletionItems }, '.', ' '),
        vscode.languages.registerHoverProvider('salamander', { provideHover }),
    );

    scheduleCheck();
}

export function deactivate(): void { /* ничего */ }

// ===========================================================================
// Манифест API
// ===========================================================================

async function loadManifest(): Promise<void> {
    const cfg = vscode.workspace.getConfiguration('salamander');
    let p = resolveVars(cfg.get<string>('apiManifest', ''));
    if (!p) {
        const found = await vscode.workspace.findFiles('**/salamander-api.json', '**/node_modules/**', 1);
        p = found.length > 0 ? found[0].fsPath : '';
    }
    manifestPath = p || null;
    manifest = null;
    if (p && fs.existsSync(p)) {
        try {
            manifest = JSON.parse(fs.readFileSync(p, 'utf8')) as Manifest;
            output.appendLine(`salamander-api.json загружен: ${p}`);
        } catch (e) {
            output.appendLine(`salamander-api.json не читается: ${e}`);
        }
    } else {
        output.appendLine('salamander-api.json не найден — автодополнение работает без API хоста ' +
            '(запустите игру в редакторе Unity один раз, чтобы манифест выгрузился).');
    }
}

// ===========================================================================
// Индекс символов воркспейса (.script)
// ===========================================================================

// Если salamander.checkerPath не задан — ищем DslCheck в воркспейсе (модкит кладёт
// его в bin/). Так расширение работает «из коробки» без ручной настройки пути.
async function locateChecker(): Promise<void> {
    const cfg = vscode.workspace.getConfiguration('salamander');
    if (resolveVars(cfg.get<string>('checkerPath', ''))) return; // путь задан явно

    const dll = await vscode.workspace.findFiles('**/DslCheck.dll', '**/node_modules/**', 1);
    if (dll.length > 0) { autoCheckerPath = dll[0].fsPath; output.appendLine(`Чекер найден: ${autoCheckerPath}`); return; }

    const exe = await vscode.workspace.findFiles('**/DslCheck{,.exe}', '**/node_modules/**', 1);
    if (exe.length > 0) { autoCheckerPath = exe[0].fsPath; output.appendLine(`Чекер найден: ${autoCheckerPath}`); }
}


async function indexWorkspace(): Promise<void> {
    const files = await vscode.workspace.findFiles('**/*.script', '**/node_modules/**', 200);
    for (const f of files) {
        try {
            const doc = await vscode.workspace.openTextDocument(f);
            indexDocument(doc);
        } catch { /* пропускаем нечитаемые */ }
    }
}

function indexDocument(doc: vscode.TextDocument): void {
    const syms: WsFileSymbols = { triggers: [], classes: new Map(), enums: new Map() };
    const text = doc.getText();
    const lines = text.split(/\r?\n/);

    let current: { kind: string; name: string } | null = null;
    let depth = 0;

    for (const raw of lines) {
        const line = raw.replace(/\/\/.*$/, ''); // срезаем строчные комментарии

        let decl = /^\s*(?:disabled\s+)?(trigger|class|enum|listener)\s+([A-Za-z_]\w*)/.exec(line);
        if (!decl) {
            const arch = /^([A-Za-z_]\w*)\s+("[^"]*"|[A-Za-z_]\w*)\s*\{/.exec(line);
            if (arch && !KEYWORDS.includes(arch[1])) decl = [arch[0], arch[1], arch[2]] as unknown as RegExpExecArray;
        }
        if (decl && depth === 0) {
            current = { kind: decl[1], name: decl[2] };
            if (decl[1] === 'trigger') syms.triggers.push(decl[2]);
            if (decl[1] === 'class') syms.classes.set(decl[2], { funcs: [], fields: [] });
            if (decl[1] === 'enum') {
                const inline = /\{([^}]*)\}/.exec(line);
                const members = inline
                    ? inline[1].split(',').map(s => s.trim()).filter(s => /^[A-Za-z_]\w*$/.test(s))
                    : [];
                syms.enums.set(decl[2], members);
            }
        }

        if (current && depth >= 1 && current.kind === 'class') {
            const cls = syms.classes.get(current.name);
            const fn = /^\s*func\s+([A-Za-z_]\w*)\s*\(([^)]*)\)(?:\s*->\s*([^\s{]+))?/.exec(line);
            if (fn && cls) {
                const params: ApiParam[] = fn[2].trim()
                    ? fn[2].split(',').map(s => {
                        const parts = s.trim().split(/\s+/);
                        return parts.length >= 2
                            ? { name: parts[parts.length - 1], type: parts.slice(0, -1).join(' ') }
                            : { name: parts[0] || 'arg', type: '' };
                    })
                    : [];
                cls.funcs.push({ name: fn[1], params, returns: fn[3] ? fn[3].trim() : 'void' });
            }
            const field = /^\s*(?:const\s+)?([A-Za-z_][\w<>\[\], ]*?)\s+([A-Za-z_]\w*)\s*[=;]/.exec(line);
            if (field && cls && !KEYWORDS.includes(field[1].trim())) {
                cls.fields.push({ name: field[2], type: field[1].trim() });
            }
        }

        if (current && current.kind === 'enum' && depth >= 1) {
            const members = syms.enums.get(current.name);
            if (members) {
                for (const m of line.matchAll(/\b([A-Za-z_]\w*)\b/g)) {
                    if (!members.includes(m[1])) members.push(m[1]);
                }
            }
        }

        // грубый трекинг вложенности (строки/интерполяция не учитываются — для индекса достаточно)
        for (const ch of line) {
            if (ch === '{') depth++;
            else if (ch === '}') { depth--; if (depth <= 0) { depth = 0; current = null; } }
        }
    }

    wsIndex.set(doc.uri.toString(), syms);
}

function allWs<T>(pick: (s: WsFileSymbols) => T[]): T[] {
    const out: T[] = [];
    for (const s of wsIndex.values()) out.push(...pick(s));
    return out;
}

function findWsClass(name: string): WsClass | undefined {
    for (const s of wsIndex.values()) {
        const c = s.classes.get(name);
        if (c) return c;
    }
    return undefined;
}

function findEnumMembers(name: string): string[] | undefined {
    if (manifest) {
        const e = manifest.enums?.find(x => x.name === name);
        if (e) return e.members;
    }
    for (const s of wsIndex.values()) {
        const m = s.enums.get(name);
        if (m) return m;
    }
    return undefined;
}

// ===========================================================================
// Автодополнение
// ===========================================================================

function provideCompletionItems(doc: vscode.TextDocument, pos: vscode.Position):
    vscode.CompletionItem[] {
    const line = doc.lineAt(pos.line).text.slice(0, pos.character);

    const dotted = /([A-Za-z_]\w*)\.\s*\w*$/.exec(line);
    if (dotted) return memberCompletions(dotted[1]);

    if (/\bevent\s+\w*$/.test(line)) return eventCompletions(doc, pos.line);

    return generalCompletions(doc, pos);
}

function methodItem(m: ApiMethod, ownerDetail: string): vscode.CompletionItem {
    const item = new vscode.CompletionItem(m.name, vscode.CompletionItemKind.Method);
    const sig = m.params.map(p => `${p.type} ${p.name}`).join(', ');
    item.detail = `${ownerDetail}${m.name}(${sig})${m.returns && m.returns !== 'void' ? ' -> ' + m.returns : ''}`;
    const args = m.params.map((p, i) => `\${${i + 1}:${p.name}}`).join(', ');
    item.insertText = new vscode.SnippetString(`${m.name}(${args})`);
    if (m.summary || m.params.some(p => p.doc)) {
        const md = new vscode.MarkdownString();
        if (m.summary) md.appendText(m.summary);
        for (const p of m.params) if (p.doc) md.appendMarkdown(`\n\n- \`${p.name}\` — ${p.doc}`);
        item.documentation = md;
    }
    return item;
}

function memberCompletions(qualifier: string): vscode.CompletionItem[] {
    const items: vscode.CompletionItem[] = [];

    if (qualifier === 'Engine') {
        for (const m of ENGINE_METHODS) items.push(methodItem(m, 'Engine.'));
        return items;
    }

    const api = manifest?.apis?.find(a => a.name === qualifier);
    if (api) {
        for (const m of api.methods) items.push(methodItem(m, qualifier + '.'));
        return items;
    }

    const enumMembers = findEnumMembers(qualifier);
    if (enumMembers) {
        for (const m of enumMembers) {
            const it = new vscode.CompletionItem(m, vscode.CompletionItemKind.EnumMember);
            it.detail = `${qualifier}.${m}`;
            items.push(it);
        }
        return items;
    }

    const wsClass = findWsClass(qualifier);
    if (wsClass) {
        for (const f of wsClass.funcs) items.push(methodItem(f, qualifier + '.'));
        for (const fl of wsClass.fields) {
            const it = new vscode.CompletionItem(fl.name, vscode.CompletionItemKind.Field);
            it.detail = `${fl.type} ${qualifier}.${fl.name}`;
            items.push(it);
        }
        return items;
    }

    // квалификатор неизвестен — вероятно, переменная-сущность: предлагаем
    // свойства всех хостовых классов + встроенные члены коллекций
    if (manifest) {
        for (const c of manifest.classes ?? []) {
            for (const p of c.props ?? []) {
                const it = new vscode.CompletionItem(p.name, vscode.CompletionItemKind.Property);
                it.detail = `${c.name}.${p.name}: ${p.type}${p.readOnly ? ' (readonly)' : ''}`;
                if (p.doc) it.documentation = new vscode.MarkdownString(p.doc);
                items.push(it);
            }
        }
    }
    for (const coll of [
        { name: 'length', detail: 'int — длина массива' },
        { name: 'count', detail: 'int — размер List/Map' },
    ]) {
        const it = new vscode.CompletionItem(coll.name, vscode.CompletionItemKind.Property);
        it.detail = coll.detail;
        items.push(it);
    }
    const collMethods: ApiMethod[] = [
        { name: 'Add', summary: 'Добавить элемент в List.', params: [{ name: 'value', type: '' }], returns: 'void' },
        { name: 'Clear', summary: 'Очистить List.', params: [], returns: 'void' },
        { name: 'Has', summary: 'Есть ли ключ в Map.', params: [{ name: 'key', type: '' }], returns: 'bool' },
        { name: 'Remove', summary: 'Удалить ключ из Map.', params: [{ name: 'key', type: '' }], returns: 'void' },
    ];
    for (const m of collMethods) items.push(methodItem(m, ''));
    return items;
}

// Вид охватывающего блока-архетипа (spell/item/...), если курсор внутри такого.
// Эвристика: ближайшая выше декларация верхнего уровня.
function enclosingArchetypeKind(doc: vscode.TextDocument, fromLine: number): string | null {
    const kinds = manifest?.archetypes;
    if (!kinds || kinds.length === 0) return null;
    const names = new Set(kinds.map(a => a.name));
    const declRe = /^([A-Za-z_]\w*)\s+("[^"]*"|[A-Za-z_]\w*)\s*\{/;
    const otherDecl = /^\s*(?:disabled\s+)?(?:trigger|class|enum|listener)\b/;
    for (let i = fromLine; i >= 0; i--) {
        const text = doc.lineAt(i).text;
        if (otherDecl.test(text)) return null;          // внутри обычной декларации
        const m = declRe.exec(text);
        if (m) return names.has(m[1]) ? m[1] : null;    // внутри блока-архетипа?
    }
    return null;
}

function eventCompletions(doc: vscode.TextDocument, fromLine: number): vscode.CompletionItem[] {
    const items: vscode.CompletionItem[] = [];
    const kind = enclosingArchetypeKind(doc, fromLine);
    const source = kind
        ? (manifest?.archetypes?.find(a => a.name === kind)?.events ?? [])
        : (manifest?.events ?? []);
    for (const ev of source) {
        const sig = ev.params.map(p => `${p.type} ${p.name}`).join(', ');
        const it = new vscode.CompletionItem(ev.name, vscode.CompletionItemKind.Event);
        it.detail = `event ${ev.name}(${sig})`;
        it.insertText = new vscode.SnippetString(`${ev.name}(${sig})\n{\n\t$0\n}`);
        if (ev.summary || ev.params.some(p => p.doc)) {
            const md = new vscode.MarkdownString();
            if (ev.summary) md.appendText(ev.summary);
            for (const p of ev.params) if (p.doc) md.appendMarkdown(`\n\n- \`${p.name}\` — ${p.doc}`);
            it.documentation = md;
        }
        items.push(it);
    }
    return items;
}

function generalCompletions(doc: vscode.TextDocument, pos: vscode.Position): vscode.CompletionItem[] {
    const items: vscode.CompletionItem[] = [];

    for (const k of KEYWORDS)
        items.push(new vscode.CompletionItem(k, vscode.CompletionItemKind.Keyword));
    for (const t of TYPES)
        items.push(new vscode.CompletionItem(t, vscode.CompletionItemKind.Struct));

    const engine = new vscode.CompletionItem('Engine', vscode.CompletionItemKind.Class);
    engine.detail = 'встроенный класс движка скриптов';
    items.push(engine);

    if (manifest) {
        for (const a of manifest.apis ?? []) {
            const it = new vscode.CompletionItem(a.name, vscode.CompletionItemKind.Class);
            it.detail = 'API хоста';
            items.push(it);
        }
        for (const c of manifest.classes ?? []) {
            const it = new vscode.CompletionItem(c.name, vscode.CompletionItemKind.Class);
            it.detail = 'сущность хоста';
            items.push(it);
        }
        for (const e of manifest.enums ?? []) {
            const it = new vscode.CompletionItem(e.name, vscode.CompletionItemKind.Enum);
            it.detail = 'енум хоста';
            items.push(it);
        }
    }

    for (const t of allWs(s => s.triggers)) {
        const it = new vscode.CompletionItem(t, vscode.CompletionItemKind.Class);
        it.detail = 'триггер';
        items.push(it);
    }
    for (const s of wsIndex.values()) {
        for (const name of s.classes.keys()) {
            const it = new vscode.CompletionItem(name, vscode.CompletionItemKind.Class);
            it.detail = 'скриптовый класс';
            items.push(it);
        }
        for (const name of s.enums.keys()) {
            const it = new vscode.CompletionItem(name, vscode.CompletionItemKind.Enum);
            it.detail = 'скриптовый енум';
            items.push(it);
        }
    }

    // члены охватывающего класса/триггера — по грубому скану вверх
    const enclosing = findEnclosingDecl(doc, pos);
    if (enclosing) {
        const cls = findWsClass(enclosing);
        if (cls) {
            for (const f of cls.funcs) items.push(methodItem(f, ''));
            for (const fl of cls.fields) {
                const it = new vscode.CompletionItem(fl.name, vscode.CompletionItemKind.Field);
                it.detail = fl.type;
                items.push(it);
            }
        }
    }

    return items;
}

function findEnclosingDecl(doc: vscode.TextDocument, pos: vscode.Position): string | null {
    for (let l = pos.line; l >= 0; l--) {
        const m = /^\s*(?:disabled\s+)?(?:trigger|class)\s+([A-Za-z_]\w*)/.exec(doc.lineAt(l).text);
        if (m) return m[1];
    }
    return null;
}

// ===========================================================================
// Hover: сигнатуры Api.Method / Engine.Method / событий
// ===========================================================================

function provideHover(doc: vscode.TextDocument, pos: vscode.Position): vscode.Hover | null {
    const range = doc.getWordRangeAtPosition(pos, /[A-Za-z_]\w*/);
    if (!range) return null;
    const word = doc.getText(range);
    const before = doc.lineAt(pos.line).text.slice(0, range.start.character);
    const qual = /([A-Za-z_]\w*)\.\s*$/.exec(before)?.[1];

    const methodHover = (owner: string, m: ApiMethod): vscode.Hover => {
        const sig = m.params.map(p => `${p.type} ${p.name}`).join(', ');
        const mk = new vscode.MarkdownString();
        mk.appendCodeblock(`${owner}${m.name}(${sig})${m.returns !== 'void' ? ' -> ' + m.returns : ''}`, 'salamander');
        if (m.summary) mk.appendText('\n' + m.summary);
        for (const p of m.params) if (p.doc) mk.appendMarkdown(`\n\n- \`${p.name}\` — ${p.doc}`);
        return new vscode.Hover(mk);
    };

    if (qual === 'Engine') {
        const m = ENGINE_METHODS.find(x => x.name === word);
        if (m) return methodHover('Engine.', m);
    }
    if (qual && manifest) {
        const api = manifest.apis?.find(a => a.name === qual);
        const m = api?.methods.find(x => x.name === word);
        if (m) return methodHover(qual + '.', m);

        const cls = manifest.classes?.find(c => c.props?.some(p => p.name === word));
        if (cls) {
            const p = cls.props.find(x => x.name === word)!;
            const mk = new vscode.MarkdownString();
            mk.appendCodeblock(`${cls.name}.${p.name}: ${p.type}${p.readOnly ? '  // readonly' : ''}`, 'salamander');
            if (p.doc) mk.appendText('\n' + p.doc);
            return new vscode.Hover(mk);
        }
    }
    const ev = manifest?.events?.find(e => e.name === word);
    if (ev) {
        const sig = ev.params.map(p => `${p.type} ${p.name}`).join(', ');
        const mk = new vscode.MarkdownString();
        mk.appendCodeblock(`event ${ev.name}(${sig})`, 'salamander');
        if (ev.summary) mk.appendText('\n' + ev.summary);
        return new vscode.Hover(mk);
    }

    return null;
}

// ===========================================================================
// Диагностика через DslCheck
// ===========================================================================

interface CheckDiag {
    file: string; line: number; column: number;
    severity: string; code: string; message: string;
}

function scheduleCheck(): void {
    if (checkTimer) clearTimeout(checkTimer);
    checkTimer = setTimeout(() => runCheck(), 400);
}

function resolveModsRoot(): string | null {
    const cfg = vscode.workspace.getConfiguration('salamander');
    const configured = resolveVars(cfg.get<string>('modsRoot', ''));
    if (configured) return configured;
    if (manifestPath) return path.dirname(manifestPath);
    const folders = vscode.workspace.workspaceFolders;
    return folders && folders.length > 0 ? folders[0].uri.fsPath : null;
}

function runCheck(): void {
    const cfg = vscode.workspace.getConfiguration('salamander');
    let checker = resolveVars(cfg.get<string>('checkerPath', ''));
    if (!checker) checker = autoCheckerPath ?? '';
    if (!checker) {
        output.appendLine('DslCheck не найден: задайте salamander.checkerPath или положите DslCheck в воркспейс ' +
            '(модкит кладёт его в bin/). Сборка чекера: dotnet publish Tools/DslCheck.');
        return;
    }
    const root = resolveModsRoot();
    if (!root) return;

    const isDll = checker.toLowerCase().endsWith('.dll');
    const cmd = isDll ? 'dotnet' : checker;
    const args = isDll ? [checker, root, '--json'] : [root, '--json'];
    if (manifestPath) args.push('--api', manifestPath);

    cp.execFile(cmd, args, { maxBuffer: 8 * 1024 * 1024 }, (err, stdout, stderr) => {
        // код возврата 1 = "есть ошибки" — это штатный ответ, не сбой запуска
        if (err && (err as { code?: unknown }).code !== 1) {
            output.appendLine(`DslCheck не запустился: ${err.message}`);
            if (stderr) output.appendLine(stderr);
            return;
        }
        let diags: CheckDiag[];
        try {
            diags = JSON.parse(stdout) as CheckDiag[];
        } catch {
            output.appendLine('DslCheck вернул нечитаемый вывод:');
            output.appendLine(stdout);
            return;
        }
        publishDiagnostics(diags);
    });
}

function publishDiagnostics(list: CheckDiag[]): void {
    diagnostics.clear();
    const byFile = new Map<string, vscode.Diagnostic[]>();

    for (const d of list) {
        if (!d.file || !fs.existsSync(d.file)) {
            output.appendLine(`${d.severity} ${d.code}: ${d.message}`);
            continue;
        }
        const line = Math.max(0, d.line - 1);
        const col = Math.max(0, d.column - 1);

        // подчёркиваем до конца строки, если файл открыт; иначе один символ
        let endCol = col + 1;
        const open = vscode.workspace.textDocuments.find(t => t.uri.fsPath === d.file);
        if (open && line < open.lineCount) endCol = Math.max(endCol, open.lineAt(line).text.length);

        const sev = d.severity === 'error' ? vscode.DiagnosticSeverity.Error
            : d.severity === 'warning' ? vscode.DiagnosticSeverity.Warning
            : vscode.DiagnosticSeverity.Information;

        const diag = new vscode.Diagnostic(
            new vscode.Range(line, col, line, endCol), d.message, sev);
        diag.code = d.code;
        diag.source = 'salamander';

        const key = d.file;
        if (!byFile.has(key)) byFile.set(key, []);
        byFile.get(key)!.push(diag);
    }

    for (const [file, ds] of byFile)
        diagnostics.set(vscode.Uri.file(file), ds);
}
