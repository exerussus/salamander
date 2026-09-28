package com.exerussus.salamander.rider.newfile;

import com.exerussus.salamander.rider.SalamanderPlugin;
import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.jetbrains.annotations.Nullable;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * То, что шаблонам нужно из salamander-api.json: виды архетипов (константы с
 * типами, дефолтами и доками, события с параметрами), события хоста для
 * триггеров и члены енумов — чтобы подставить в константу первый законный член.
 *
 * Манифест пишет игра, поэтому и шаблоны — игры: новый вид архетипа появится в
 * меню сам, как только игра выгрузит манифест.
 */
final class ApiModel {
    record Param(String name, String type) {
    }

    record Event(String name, String summary, List<Param> params) {
    }

    record Const(String name, String type, boolean required, String doc, @Nullable JsonElement def, boolean hasDefault) {
    }

    record Archetype(String name, String summary, List<Const> consts, List<Event> events, boolean eventsOptional) {
    }

    final Map<String, List<String>> enums = new HashMap<>();
    final List<Archetype> archetypes = new ArrayList<>();
    final List<Event> events = new ArrayList<>();

    private record Cached(long modified, long size, ApiModel model) {
    }

    private static final Map<Path, Cached> CACHE = new ConcurrentHashMap<>();

    /** Модель манифеста; перечитывается, только если файл изменился. null — не прочитался. */
    static @Nullable ApiModel load(@Nullable Path file) {
        if (file == null) return null;
        try {
            long modified = Files.getLastModifiedTime(file).toMillis();
            long size = Files.size(file);
            Cached c = CACHE.get(file);
            if (c != null && c.modified == modified && c.size == size) return c.model;
            ApiModel m = parse(Files.readString(file, StandardCharsets.UTF_8));
            CACHE.put(file, new Cached(modified, size, m));
            return m;
        } catch (Exception e) {
            SalamanderPlugin.LOG.warn("salamander-api.json не прочитан: " + file + " — " + e.getMessage());
            return null;
        }
    }

    private static ApiModel parse(String json) {
        if (!json.isEmpty() && json.charAt(0) == '﻿') json = json.substring(1);
        JsonObject root = JsonParser.parseString(json).getAsJsonObject();
        ApiModel m = new ApiModel();
        for (JsonElement e : arr(root, "enums")) {
            JsonObject o = e.getAsJsonObject();
            List<String> members = new ArrayList<>();
            for (JsonElement x : arr(o, "members")) members.add(x.getAsString());
            m.enums.put(str(o, "name"), members);
        }
        for (JsonElement e : arr(root, "events")) m.events.add(event(e.getAsJsonObject()));
        for (JsonElement e : arr(root, "archetypes")) {
            JsonObject o = e.getAsJsonObject();
            List<Const> consts = new ArrayList<>();
            for (JsonElement x : arr(o, "consts")) {
                JsonObject c = x.getAsJsonObject();
                JsonElement def = c.get("default");
                consts.add(new Const(str(c, "name"), str(c, "type"), bool(c, "required"), str(c, "doc"),
                        def == null || def.isJsonNull() ? null : def, bool(c, "hasDefault")));
            }
            List<Event> events = new ArrayList<>();
            for (JsonElement x : arr(o, "events")) events.add(event(x.getAsJsonObject()));
            m.archetypes.add(new Archetype(str(o, "name"), str(o, "summary"), consts, events, bool(o, "eventsOptional")));
        }
        m.archetypes.sort((a, b) -> a.name().compareToIgnoreCase(b.name()));
        return m;
    }

    private static Event event(JsonObject o) {
        List<Param> params = new ArrayList<>();
        for (JsonElement p : arr(o, "params")) {
            JsonObject po = p.getAsJsonObject();
            params.add(new Param(str(po, "name"), str(po, "type")));
        }
        return new Event(str(o, "name"), str(o, "summary"), params);
    }

    private static Iterable<JsonElement> arr(JsonObject o, String key) {
        JsonElement e = o.get(key);
        return e != null && e.isJsonArray() ? (JsonArray) e : Collections.emptyList();
    }

    private static String str(JsonObject o, String key) {
        JsonElement e = o.get(key);
        return e != null && e.isJsonPrimitive() ? e.getAsString() : "";
    }

    private static boolean bool(JsonObject o, String key) {
        JsonElement e = o.get(key);
        return e != null && e.isJsonPrimitive() && e.getAsBoolean();
    }
}
