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
        synchronize: {
            // сервер перечитывает манифест и module.json при каждом рефреше,
            // но события об их изменении ускоряют реакцию
            fileEvents: vscode.workspace.createFileSystemWatcher('**/{salamander-api.json,module.json}'),
        },
    };

    client = new LanguageClient('salamander', 'Salamander LSP', serverOptions, clientOptions);
    context.subscriptions.push({ dispose: () => client?.stop() });
    await client.start();
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
