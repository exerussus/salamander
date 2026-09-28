package com.exerussus.salamander.rider.lsp;

import com.exerussus.salamander.rider.SalamanderNotifier;
import com.intellij.notification.NotificationAction;
import com.intellij.notification.NotificationType;
import com.intellij.openapi.fileTypes.FileNameMatcher;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.startup.ProjectActivity;
import com.intellij.openapi.util.Pair;
import com.redhat.devtools.lsp4ij.LanguageServersRegistry;
import com.redhat.devtools.lsp4ij.server.definition.LanguageServerDefinition;
import com.redhat.devtools.lsp4ij.server.definition.launching.UserDefinedLanguageServerDefinition;
import kotlin.Unit;
import kotlin.coroutines.Continuation;
import org.jetbrains.annotations.NotNull;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/**
 * Ручной сервер Salamander из старой инструкции (LSP4IJ → New Language Server)
 * теперь лишний: плагин объявляет свой. Два сервера на одни .sal — двойные
 * ошибки и двойные подсказки. Находим ручные и предлагаем удалить одной кнопкой.
 */
public final class SalamanderLspStartup implements ProjectActivity {
    @Override
    public Object execute(@NotNull Project project, @NotNull Continuation<? super Unit> continuation) {
        LanguageServersRegistry registry = LanguageServersRegistry.getInstance();
        List<LanguageServerDefinition> manual = new ArrayList<>();
        for (LanguageServerDefinition d : registry.getServerDefinitions()) {
            if (d instanceof UserDefinedLanguageServerDefinition && isSalamander(d)) manual.add(d);
        }
        if (manual.isEmpty()) return Unit.INSTANCE;

        StringBuilder names = new StringBuilder();
        for (LanguageServerDefinition d : manual) {
            if (names.length() > 0) names.append(", ");
            names.append('«').append(d.getDisplayName()).append('»');
        }
        SalamanderNotifier.create(
                "Сервер Salamander теперь подключает плагин. Ручной сервер LSP4IJ " + names
                        + " дублирует его: ошибки и подсказки будут двоиться.",
                NotificationType.WARNING)
                .addAction(NotificationAction.createSimpleExpiring("Удалить ручной сервер", () -> {
                    for (LanguageServerDefinition d : manual) registry.removeServerDefinition(project, d);
                    SalamanderNotifier.show(project, "Ручной сервер удалён — работает сервер из плагина.",
                            NotificationType.INFORMATION);
                }))
                .notify(project);
        return Unit.INSTANCE;
    }

    private static boolean isSalamander(LanguageServerDefinition d) {
        String name = d.getDisplayName();
        if (name != null && name.toLowerCase(Locale.ROOT).contains("salamander")) return true;
        for (Pair<List<FileNameMatcher>, String> mapping : d.getFilenameMatcherMappings()) {
            for (FileNameMatcher m : mapping.first) {
                if (m.acceptsCharSequence("probe.sal")) return true;
            }
        }
        return false;
    }
}
