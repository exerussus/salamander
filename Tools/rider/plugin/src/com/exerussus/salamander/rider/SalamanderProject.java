package com.exerussus.salamander.rider;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.intellij.openapi.project.Project;
import org.jetbrains.annotations.Nullable;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.stream.Stream;

/**
 * Что Salamander знает о проекте: где манифест API игры, где модули, какие
 * настройки передать серверу.
 *
 * НАСТРОЙКИ ПРОЕКТА — ФАЙЛ salamander-lsp.json В КОРНЕ. Если его нет, всё
 * угадывается само: в Unity-проекте манифест ищется в Assets/StreamingAssets,
 * и его папка становится корнем модулей. Ключи те же, что у VS Code
 * (salamander.apiManifest, salamander.modulesRoot, salamander.buildFile,
 * salamander.referencePaths, salamander.server.path).
 */
public final class SalamanderProject {
    public static final String MANIFEST = "salamander-api.json";
    public static final String SETTINGS = "salamander-lsp.json";

    private SalamanderProject() {
    }

    public static @Nullable Path base(Project project) {
        String b = project.getBasePath();
        return b != null ? Paths.get(b) : null;
    }

    /** Файл настроек проекта или null. Битый JSON — пишем в лог и живём без него. */
    public static @Nullable JsonObject settings(Project project) {
        Path base = base(project);
        if (base == null) return null;
        Path file = base.resolve(SETTINGS);
        if (!Files.isRegularFile(file)) return null;
        try {
            JsonElement e = JsonParser.parseString(Files.readString(file, StandardCharsets.UTF_8));
            if (e.isJsonObject()) return e.getAsJsonObject();
            SalamanderPlugin.LOG.warn(SETTINGS + ": ожидался JSON-объект");
        } catch (IOException | RuntimeException e) {
            SalamanderPlugin.LOG.warn(SETTINGS + " не прочитан: " + e.getMessage());
        }
        return null;
    }

    /** Строковая настройка: и плоский ключ "salamander.x", и вложенный {"salamander": {"x": ...}}. */
    public static @Nullable String setting(@Nullable JsonObject s, String key) {
        if (s == null) return null;
        JsonElement e = s.get("salamander." + key);
        if (e == null && s.get("salamander") != null && s.get("salamander").isJsonObject())
            e = s.getAsJsonObject("salamander").get(key);
        if (e == null) e = s.get(key);
        return e != null && e.isJsonPrimitive() && !e.getAsString().isBlank() ? e.getAsString() : null;
    }

    /**
     * Манифест API для проекта: корень → Assets/StreamingAssets (вглубь на 4
     * уровня). Библиотеку и Temp Unity не трогаем — там их не бывает, а
     * обход был бы долгим.
     */
    private record Found(long at, @Nullable Path path) {
    }

    private static final java.util.Map<Path, Found> FOUND = new java.util.concurrent.ConcurrentHashMap<>();

    /** То же, что findManifestNow, но не чаще раза в 5 с на проект: меню обновляется часто. */
    public static @Nullable Path findManifest(Project project) {
        Path base = base(project);
        if (base == null) return null;
        long now = System.currentTimeMillis();
        Found f = FOUND.get(base);
        if (f != null && now - f.at < 5_000) return f.path;
        Path p = findManifestNow(base);
        FOUND.put(base, new Found(now, p));
        return p;
    }

    private static @Nullable Path findManifestNow(Path base) {
        Path direct = base.resolve(MANIFEST);
        if (Files.isRegularFile(direct)) return direct;
        Path sa = base.resolve("Assets").resolve("StreamingAssets");
        if (!Files.isDirectory(sa)) return null;
        try (Stream<Path> s = Files.walk(sa, 4)) {
            return s.filter(p -> p.getFileName().toString().equals(MANIFEST)).sorted().findFirst().orElse(null);
        } catch (IOException | RuntimeException e) {
            return null;
        }
    }

    /** Ближайший манифест вверх от папки — для шаблонов нового файла. */
    public static @Nullable Path findManifestUp(@Nullable Path dir, @Nullable Path stopAt) {
        for (Path d = dir; d != null; d = d.getParent()) {
            Path m = d.resolve(MANIFEST);
            if (Files.isRegularFile(m)) return m;
            if (stopAt != null && d.equals(stopAt)) break;
        }
        return null;
    }

    /** Похоже ли, что в проекте есть Salamander: манифест, настройки или исходники языка. */
    public static boolean usesSalamander(Project project) {
        return findManifest(project) != null || hasMarkers(project);
    }

    /** Дешёвая часть проверки, без обхода папок: файл настроек или исходники языка в корне. */
    public static boolean hasMarkers(Project project) {
        Path base = base(project);
        return base != null && (Files.isRegularFile(base.resolve(SETTINGS)) || Files.isDirectory(base.resolve("Dsl.Core")));
    }

    /**
     * initializationOptions для сервера. Настройки проекта — как есть; иначе
     * апи-манифест и его папка как корень модулей, если манифест лежит не в
     * корне (в корне сервер найдёт его сам).
     */
    public static JsonObject initializationOptions(Project project) {
        JsonObject s = settings(project);
        if (s != null) {
            JsonObject copy = s.deepCopy();
            copy.remove("salamander.server.path"); // это настройка клиента, серверу незачем
            return copy;
        }
        JsonObject o = new JsonObject();
        Path base = base(project);
        Path manifest = findManifest(project);
        if (base != null && manifest != null && !manifest.getParent().equals(base)) {
            o.addProperty("salamander.apiManifest", manifest.toString());
            o.addProperty("salamander.modulesRoot", manifest.getParent().toString());
        }
        return o;
    }

    /**
     * DslLsp.dll: настройка salamander.server.path → переменная
     * SALAMANDER_LSP_DLL → сборка в открытом репозитории Salamander
     * (Tools/DslLsp/publish) → сервер, приехавший с плагином.
     */
    public static @Nullable Path serverDll(Project project) {
        Path base = base(project);
        String configured = setting(settings(project), "server.path");
        if (configured == null) configured = System.getenv("SALAMANDER_LSP_DLL");
        if (configured != null) {
            Path p = Paths.get(configured.trim().replace("\"", ""));
            if (!p.isAbsolute() && base != null) p = base.resolve(p);
            return p.normalize();
        }
        if (base != null) {
            Path repo = base.resolve("Tools").resolve("DslLsp").resolve("publish").resolve("DslLsp.dll");
            if (Files.isRegularFile(repo)) return repo;
        }
        return SalamanderPlugin.bundledServer();
    }
}
