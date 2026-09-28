package com.exerussus.salamander.rider;

import com.intellij.ide.plugins.IdeaPluginDescriptor;
import com.intellij.ide.plugins.PluginManagerCore;
import com.intellij.openapi.diagnostic.Logger;
import com.intellij.openapi.extensions.PluginId;
import com.intellij.openapi.util.SystemInfo;
import org.jetbrains.annotations.Nullable;

import java.io.File;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;

/**
 * Где лежит сам плагин и что ему нужно снаружи: папка установки (сервер и
 * бандл TextMate лежат в ней файлами, а не в jar) и dotnet для запуска сервера.
 */
public final class SalamanderPlugin {
    public static final String ID = "com.exerussus.salamander.icon";
    public static final String LSP4IJ_ID = "com.redhat.devtools.lsp4ij";
    public static final String TEXTMATE_ID = "org.jetbrains.plugins.textmate";
    public static final String NOTIFICATIONS = "Salamander";

    public static final Logger LOG = Logger.getInstance("#salamander");

    private SalamanderPlugin() {
    }

    /** Папка установки плагина: <plugins>/Salamander. null — плагин грузится не из папки (тесты, отладка). */
    public static @Nullable Path dir() {
        IdeaPluginDescriptor d = PluginManagerCore.getPlugin(PluginId.getId(ID));
        return d != null ? d.getPluginPath() : null;
    }

    /** Сервер, собранный вместе с плагином: <plugin>/server/DslLsp.dll. */
    public static @Nullable Path bundledServer() {
        Path dir = dir();
        if (dir == null) return null;
        Path dll = dir.resolve("server").resolve("DslLsp.dll");
        return Files.isRegularFile(dll) ? dll : null;
    }

    public static boolean isPluginEnabled(String id) {
        PluginId pid = PluginId.getId(id);
        return PluginManagerCore.getPlugin(pid) != null && !PluginManagerCore.isDisabled(pid);
    }

    /**
     * dotnet для запуска сервера. Порядок: DOTNET_ROOT → PATH → стандартные
     * места установки. null — не нашли: сервер не поднять, о чём скажет
     * уведомление при открытии проекта.
     */
    public static @Nullable String dotnet() {
        String exe = SystemInfo.isWindows ? "dotnet.exe" : "dotnet";

        String root = System.getenv("DOTNET_ROOT");
        if (root != null && !root.isBlank()) {
            Path p = Paths.get(root, exe);
            if (Files.isRegularFile(p)) return p.toString();
        }

        String path = System.getenv("PATH");
        if (path != null) {
            for (String part : path.split(File.pathSeparator)) {
                if (part.isBlank()) continue;
                try {
                    Path p = Paths.get(part.trim().replace("\"", ""), exe);
                    if (Files.isRegularFile(p)) return p.toString();
                } catch (RuntimeException ignored) {
                    // мусор в PATH — не повод падать
                }
            }
        }

        String[] known = SystemInfo.isWindows
                ? new String[]{System.getenv("ProgramFiles") + "\\dotnet\\dotnet.exe",
                               System.getProperty("user.home") + "\\.dotnet\\dotnet.exe"}
                : new String[]{"/usr/local/share/dotnet/dotnet", "/usr/share/dotnet/dotnet", "/usr/lib/dotnet/dotnet",
                               "/opt/homebrew/bin/dotnet", System.getProperty("user.home") + "/.dotnet/dotnet"};
        for (String k : known) {
            if (k != null && !k.startsWith("null") && Files.isRegularFile(Paths.get(k))) return k;
        }
        return null;
    }
}
