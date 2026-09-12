// ===========================================================================
// Salamander для VS Code — тонкий LSP-клиент.
// Все мозги (диагностика, комплишены, hover, go-to, символы) живут в
// LSP-сервере (Tools/DslLsp) — том же компиляторе, что использует игра.
// Здесь только: найти сервер, запустить, подключить. Подсветка — TextMate-
// грамматика, сниппеты — snippets/salamander.json (см. package.json).
// ===========================================================================

import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { LanguageClient, LanguageClientOptions, ServerOptions, TransportKind } from 'vscode-languageclient/node';

let client: LanguageClient | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    const found = await findServer();
    if (!found.dll) {
        vscode.window.showWarningMessage(found.error!);
        return;
    }

    const serverOptions: ServerOptions = {
        command: 'dotnet',
        args: [found.dll],
        transport: TransportKind.stdio,
    };
    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ language: 'salamander' }],
        // сервер знает только корень воркспейса, а в Unity-проекте модули лежат
        // глубоко в StreamingAssets — эти настройки позволяют указать всё явно
        initializationOptions: serverSettings(),
        synchronize: {
            // сервер перечитывает манифест и module.json при каждом рефреше,
            // но события об их изменении ускоряют реакцию
            fileEvents: vscode.workspace.createFileSystemWatcher('**/{salamander-api.json,module.json}'),
        },
    };

    client = new LanguageClient('salamander', 'Salamander LSP', serverOptions, clientOptions);
    context.subscriptions.push({ dispose: () => client?.stop() });
    await client.start();

    // правка настроек не должна требовать перезапуска окна
    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(async e => {
        if (!e.affectsConfiguration('salamander')) return;
        client?.sendNotification('workspace/didChangeConfiguration', { settings: serverSettings() });

        // путь к серверу — исключение: процесс запускается один раз при
        // активации, новый путь без перезагрузки окна не подхватится. Молча
        // проигнорировать правку хуже, чем спросить: человек будет думать, что
        // настройку приняли, и искать причину в другом месте.
        if (e.affectsConfiguration('salamander.server.path')) {
            const reload = 'Перезагрузить окно';
            const pick = await vscode.window.showInformationMessage(
                'Salamander: путь к LSP-серверу изменён — чтобы применить, нужно перезагрузить окно.',
                reload);
            if (pick === reload) await vscode.commands.executeCommand('workbench.action.reloadWindow');
        }
    }));
}

// Настройки, которые понимает сервер. Пути — абсолютные либо относительно
// корня воркспейса; ${workspaceFolder} поддержан для единообразия с задачами
// VS Code. Дальше их нормализует сам сервер (ModuleLoader.NormalizeUserPath).
function serverSettings(): Record<string, string> {
    const cfg = vscode.workspace.getConfiguration('salamander');
    const out: Record<string, string> = {};
    for (const key of ['apiManifest', 'modulesRoot', 'buildFile']) {
        const value = cfg.get<string>(key);
        if (value) out[key] = expand(value);
    }
    return out;
}

function workspaceRoot(): string {
    return vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '';
}

function expand(value: string): string {
    return value.replace(/\$\{workspaceFolder\}/g, workspaceRoot());
}

/**
 * Путь из настройки → путь, который понимает файловая система.
 * Снимает кавычки, разворачивает ${workspaceFolder} и file://, срезает ведущий
 * слэш перед буквой диска и достраивает относительный путь от корня воркспейса.
 *
 * Про ведущий слэш: "\C:\...\DslLsp.dll" Проводник Windows открывает, а
 * fs.existsSync молча отвечает false — ведущий слэш означает корень текущего
 * диска, после чего "C:" становится именем папки с двоеточием, которого на
 * диске быть не может. Человек видит «файл не найден» про путь, который он
 * только что открыл в Проводнике, и отладить это со своей стороны не может.
 */
function resolvePathSetting(value: string): string {
    let v = expand(value).trim().replace(/^"(.*)"$/, '$1').trim();
    if (/^file:\/\//i.test(v)) {
        try { v = vscode.Uri.parse(v).fsPath; } catch { /* не разобралось как URI */ }
    }
    v = v.replace(/^[\\/](?=[a-zA-Z]:)/, '');
    if (!v) return '';
    return path.isAbsolute(v) ? path.normalize(v) : path.resolve(workspaceRoot(), v);
}

// Поиск DslLsp.dll: настройка -> типовые места сборки в воркспейсе.
async function findServer(): Promise<{ dll?: string; error?: string }> {
    const build = 'dotnet publish Tools/DslLsp -c Release -o Tools/DslLsp/publish';
    const raw = vscode.workspace.getConfiguration('salamander').get<string>('server.path');

    // Сказали путь явно — работаем по нему и только по нему. Тихо свалиться на
    // автопоиск значит соврать: человек будет уверен, что запущен указанный им
    // сервер (а запущен другой — или никакой).
    if (raw && raw.trim()) {
        const configured = resolvePathSetting(raw);
        const shown = configured === raw.trim() ? configured : `${configured} (из «${raw.trim()}»)`;
        if (!fs.existsSync(configured)) {
            return {
                error: `Salamander: по пути из настройки salamander.server.path файла нет — ${shown}. `
                    + `Проверьте путь либо соберите сервер: ${build}`,
            };
        }
        if (!configured.toLowerCase().endsWith('.dll')) {
            return {
                error: 'Salamander: в salamander.server.path нужен именно DslLsp.dll — '
                    + `клиент запускает «dotnet <путь>», а не исполняемый файл. Указано: ${shown}`,
            };
        }
        return { dll: configured };
    }

    for (const folder of vscode.workspace.workspaceFolders ?? []) {
        const root = folder.uri.fsPath;
        for (const rel of [
            'Tools/DslLsp/publish/DslLsp.dll',
            'Tools/DslLsp/bin/Release/net8.0/DslLsp.dll',
            'Tools/DslLsp/bin/Debug/net8.0/DslLsp.dll',
        ]) {
            const p = path.join(root, rel);
            if (fs.existsSync(p)) return { dll: p };
        }
    }
    const found = await vscode.workspace.findFiles('**/DslLsp.dll', '**/node_modules/**', 1);
    if (found.length > 0) return { dll: found[0].fsPath };

    return {
        error: `Salamander: LSP-сервер не найден. Соберите его один раз: ${build} — `
            + 'или укажите полный путь к DslLsp.dll в настройке salamander.server.path.',
    };
}

export function deactivate(): Thenable<void> | undefined {
    return client?.stop();
}
