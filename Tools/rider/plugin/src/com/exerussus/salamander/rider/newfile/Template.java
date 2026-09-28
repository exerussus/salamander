package com.exerussus.salamander.rider.newfile;

import org.jetbrains.annotations.Nullable;

import java.math.BigDecimal;
import java.util.List;

/**
 * Что создаём и как это выглядит в тексте. Отступ — 4 пробела, как в
 * скриптах игры; переводы строк подставляет вызывающий (как у соседних .sal).
 */
final class Template {
    enum Kind { TRIGGER, LISTENER, CLASS, ENUM, ARCHETYPE }

    final Kind kind;
    final @Nullable ApiModel api;
    final @Nullable ApiModel.Archetype archetype;

    Template(Kind kind, @Nullable ApiModel api, @Nullable ApiModel.Archetype archetype) {
        this.kind = kind;
        this.api = api;
        this.archetype = archetype;
    }

    String title() {
        return switch (kind) {
            case TRIGGER -> "Trigger";
            case LISTENER -> "Listener";
            case CLASS -> "Class";
            case ENUM -> "Enum";
            case ARCHETYPE -> archetype.name();
        };
    }

    String description() {
        return switch (kind) {
            case TRIGGER -> "Глобальный триггер: реагирует на события хоста";
            case LISTENER -> "Подписка на события одной сущности: Engine.Attach(Имя, сущность)";
            case CLASS -> "Статический класс: константы, поля, функции";
            case ENUM -> "Скриптовый енум";
            case ARCHETYPE -> firstLine(archetype.summary());
        };
    }

    /** События, из которых выбирают в диалоге: у архетипа — его, у триггера и подписки — события хоста. */
    List<ApiModel.Event> events() {
        if (kind == Kind.ARCHETYPE) return archetype.events();
        if ((kind == Kind.TRIGGER || kind == Kind.LISTENER) && api != null) return api.events;
        return List.of();
    }

    List<ApiModel.Const> consts() {
        return kind == Kind.ARCHETYPE ? archetype.consts() : List.of();
    }

    /** Нужно ли хотя бы одно событие: триггер без него не компилируется, архетип — если вид не разрешил пустые. */
    boolean needsEvent() {
        if (kind == Kind.TRIGGER) return !events().isEmpty();
        if (kind == Kind.ARCHETYPE) return !archetype.eventsOptional() && !archetype.events().isEmpty();
        return false;
    }

    record Result(String text, int caret) {
    }

    Result render(String name, List<ApiModel.Const> consts, List<ApiModel.Event> events) {
        StringBuilder sb = new StringBuilder();
        int caret = -1;
        switch (kind) {
            case CLASS -> {
                sb.append("class ").append(name).append("\n{\n    ");
                caret = sb.length();
                sb.append("\n}\n");
                return new Result(sb.toString(), caret);
            }
            case ENUM -> {
                sb.append("enum ").append(name).append(" { ");
                caret = sb.length();
                sb.append("A, B }\n");
                return new Result(sb.toString(), caret);
            }
            case TRIGGER -> sb.append("trigger ");
            case LISTENER -> sb.append("listener ");
            case ARCHETYPE -> sb.append(archetype.name()).append(' ');
        }
        sb.append(name).append("\n{\n");

        for (ApiModel.Const c : consts) {
            sb.append("    ").append(c.type()).append(' ').append(c.name()).append(" = ").append(value(c)).append(";\n");
        }

        boolean first = true;
        boolean listener = kind == Kind.LISTENER;
        if (listener) {
            if (!consts.isEmpty()) sb.append('\n');
            sb.append("    event OnSubscribe() { }\n");
            first = false;
        }
        for (ApiModel.Event e : events) {
            if (!first || !consts.isEmpty()) sb.append('\n');
            sb.append("    event ").append(e.name()).append('(');
            for (int i = 0; i < e.params().size(); i++) {
                if (i > 0) sb.append(", ");
                sb.append(e.params().get(i).type()).append(' ').append(e.params().get(i).name());
            }
            sb.append(")\n    {\n        ");
            if (caret < 0) caret = sb.length();
            sb.append("\n    }\n");
            first = false;
        }
        if (events.isEmpty() && kind == Kind.TRIGGER) {
            sb.append(consts.isEmpty() ? "" : "\n").append("    event OnEvent()\n    {\n        ");
            caret = sb.length();
            sb.append("\n    }\n");
        }
        if (listener) sb.append("\n    event OnUnsubscribe() { }\n");
        if (caret < 0) caret = sb.length();
        sb.append("}\n");
        return new Result(sb.toString(), caret);
    }

    /** Значение константы: дефолт из манифеста, а без него — нейтральное значение своего типа. */
    String value(ApiModel.Const c) {
        try {
            return valueOrThrow(c);
        } catch (RuntimeException e) {
            // дефолт в манифесте не того вида (строка вместо числа и т.п.) — не повод ронять диалог
            return neutral(c.type());
        }
    }

    private String valueOrThrow(ApiModel.Const c) {
        String t = c.type();
        if (c.hasDefault() && c.def() != null) {
            switch (t) {
                case "string":
                    return quote(c.def().getAsString());
                case "float":
                case "double":
                    return decimal(c.def().getAsBigDecimal());
                case "int":
                    return c.def().getAsBigDecimal().toBigInteger().toString();
                case "bool":
                    return String.valueOf(c.def().getAsBoolean());
                default:
                    if (api != null && api.enums.containsKey(t)) return t + "." + c.def().getAsString();
                    return c.def().toString();
            }
        }
        return neutral(t);
    }

    private String neutral(String t) {
        switch (t) {
            case "string":
                return "\"\"";
            case "float":
            case "double":
                return "0.0";
            case "int":
                return "0";
            case "bool":
                return "false";
            default:
                List<String> members = api != null ? api.enums.get(t) : null;
                return members != null && !members.isEmpty() ? t + "." + members.get(0) : "/* " + t + " */";
        }
    }

    static String decimal(BigDecimal d) {
        String s = d.stripTrailingZeros().toPlainString();
        return s.contains(".") ? s : s + ".0";
    }

    static String quote(String s) {
        return "\"" + s.replace("\\", "\\\\").replace("\"", "\\\"")
                .replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t") + "\"";
    }

    static String firstLine(String s) {
        if (s == null) return "";
        int nl = s.indexOf('\n');
        String line = nl >= 0 ? s.substring(0, nl) : s;
        return line.length() > 160 ? line.substring(0, 157) + "…" : line;
    }
}
