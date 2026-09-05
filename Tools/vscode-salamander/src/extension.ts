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
    const dll = await findServer();
    if (!dll) {
        const build = 'dotnet build Tools/DslLsp -c Release';
        vscode.window.showWarningMessage(
            `Salamander: LSP-сервер не найден. Соберите его один раз: ${build} — ` +
            'или укажите путь к DslLsp.dll в настройке salamander.server.path.');
        return;
    }

    const serverOptions: ServerOptions = {
        command: 'dotnet',
        args: [dll],
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
    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(e => {
        if (!e.affectsConfiguration('salamander')) return;
        client?.sendNotification('workspace/didChangeConfiguration', { settings: serverSettings() });
    }));
}

// Настройки, которые понимает сервер. Пути — абсолютные либо относительно
// корня воркспейса; ${workspaceFolder} поддержан для единообразия с задачами VS Code.
function serverSettings(): Record<string, string> {
    const cfg = vscode.workspace.getConfiguration('salamander');
    const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '';
    const out: Record<string, string> = {};
    for (const key of ['apiManifest', 'modulesRoot', 'buildFile']) {
        const value = cfg.get<string>(key);
        if (value) out[key] = value.replace(/\$\{workspaceFolder\}/g, root);
    }
    return out;
}

export function deactivate(): Thenable<void> | undefined {
    return client?.stop();
}

// Поиск DslLsp.dll: настройка -> типовые места сборки в воркспейсе.
async function findServer(): Promise<string | null> {
    const configured = vscode.workspace.getConfiguration('salamander').get<string>('server.path');
    if (configured && fs.existsSync(configured)) return configured;

    for (const folder of vscode.workspace.workspaceFolders ?? []) {
        const root = folder.uri.fsPath;
        for (const rel of [
            'Tools/DslLsp/bin/Release/net8.0/DslLsp.dll',
            'Tools/DslLsp/bin/Debug/net8.0/DslLsp.dll',
            'Tools/DslLsp/publish/DslLsp.dll',
        ]) {
            const p = path.join(root, rel);
            if (fs.existsSync(p)) return p;
        }
    }
    const found = await vscode.workspace.findFiles('**/DslLsp.dll', '**/node_modules/**', 1);
    return found.length > 0 ? found[0].fsPath : null;
}
