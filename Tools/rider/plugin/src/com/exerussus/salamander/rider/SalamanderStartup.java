package com.exerussus.salamander.rider;

import com.intellij.ide.BrowserUtil;
import com.intellij.notification.NotificationAction;
import com.intellij.notification.NotificationType;
import com.intellij.openapi.application.ApplicationManager;
import com.intellij.openapi.application.ModalityState;
import com.intellij.openapi.application.WriteAction;
import com.intellij.openapi.fileTypes.FileNameMatcher;
import com.intellij.openapi.fileTypes.FileType;
import com.intellij.openapi.fileTypes.FileTypeManager;
import com.intellij.openapi.fileTypes.PlainTextFileType;
import com.intellij.openapi.fileTypes.UnknownFileType;
import com.intellij.openapi.options.ShowSettingsUtil;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.startup.ProjectActivity;
import kotlin.Unit;
import kotlin.coroutines.Continuation;
import org.jetbrains.annotations.NotNull;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Открыли проект — наводим порядок, чтобы руками в настройках не лазить:
 *
 * 1. ТИП ФАЙЛА .sal (раз на запуск IDE). С TextMate — тип «textmate»: он
 *    берёт нашу грамматику из плагина, даёт мгновенную подсветку, Ctrl+/,
 *    парные скобки. Без TextMate — обычный текст. Главное — не «unknown»:
 *    у неизвестного типа уровень подсветки выключен (OFF), и сервер красить
 *    было бы некуда. Ручную ассоциацию *.sal с текстом (шаг 4 старой
 *    инструкции) снимаем: пока она есть, TextMate файл не получит.
 *    Чужой тип, забравший .sal, не трогаем — только пишем в лог.
 *
 * 2. ЧЕГО НЕ ХВАТАЕТ (только в проектах с Salamander): LSP4IJ, dotnet,
 *    сервер. Каждое — уведомлением с кнопкой, а не молчанием.
 */
public final class SalamanderStartup implements ProjectActivity {
    private static final AtomicBoolean FILE_TYPE_DONE = new AtomicBoolean();

    @Override
    public Object execute(@NotNull Project project, @NotNull Continuation<? super Unit> continuation) {
        if (FILE_TYPE_DONE.compareAndSet(false, true)) {
            ApplicationManager.getApplication().invokeLater(SalamanderStartup::ensureFileType, ModalityState.nonModal());
        }
        if (SalamanderProject.usesSalamander(project)) checkEnvironment(project);
        return Unit.INSTANCE;
    }

    static void ensureFileType() {
        FileTypeManager ftm = FileTypeManager.getInstance();
        FileType textmate = SalamanderPlugin.isPluginEnabled(SalamanderPlugin.TEXTMATE_ID)
                ? ftm.findFileTypeByName("textmate") : null;
        FileType target = textmate != null ? textmate : PlainTextFileType.INSTANCE;
        FileType current = ftm.getFileTypeByFileName("probe.sal");
        if (current == target) return;

        boolean ours = current == UnknownFileType.INSTANCE || current == PlainTextFileType.INSTANCE
                || current == textmate || "textmate".equals(current.getName());
        if (!ours) {
            SalamanderPlugin.LOG.info(".sal занят типом «" + current.getName() + "» — не трогаем");
            return;
        }

        WriteAction.run(() -> {
            // снимаем ТОЛЬКО точные *.sal / sal — широкие маски чужие
            List<FileNameMatcher> drop = new ArrayList<>();
            for (FileNameMatcher m : ftm.getAssociations(current)) {
                String s = m.getPresentableString();
                if ("*.sal".equalsIgnoreCase(s) || "sal".equalsIgnoreCase(s)) drop.add(m);
            }
            for (FileNameMatcher m : drop) ftm.removeAssociation(current, m);
            ftm.associateExtension(target, "sal");
        });
        SalamanderPlugin.LOG.info(".sal: «" + current.getName() + "» → «" + target.getName() + "»");
        SalamanderNotifier.show(null, textmate != null
                ? ".sal теперь открываются с грамматикой Salamander (TextMate): подсветка сразу, Ctrl+/ и парные скобки."
                : ".sal теперь открываются как текст: подсветку даёт сервер Salamander.",
                NotificationType.INFORMATION);
    }

    private static void checkEnvironment(Project project) {
        if (!SalamanderPlugin.isPluginEnabled(SalamanderPlugin.LSP4IJ_ID)) {
            SalamanderNotifier.create(
                    "Для ошибок и автодополнения в .sal нужен плагин <b>LSP4IJ</b> (Red Hat). "
                            + "Установите его — сервер Salamander подключится сам.",
                    NotificationType.WARNING)
                    .addAction(NotificationAction.createSimpleExpiring("Открыть Plugins",
                            () -> ShowSettingsUtil.getInstance().showSettingsDialog(project, "Plugins")))
                    .notify(project);
            return;
        }
        if (SalamanderPlugin.dotnet() == null) {
            SalamanderNotifier.create(
                    "Не найден <b>dotnet</b> — без него сервер Salamander не запустить. "
                            + "Нужен .NET 8 Runtime (или SDK); после установки перезапустите IDE.",
                    NotificationType.WARNING)
                    .addAction(NotificationAction.createSimpleExpiring("Скачать .NET 8",
                            () -> BrowserUtil.browse("https://dotnet.microsoft.com/download/dotnet/8.0")))
                    .notify(project);
        }
        Path dll = SalamanderProject.serverDll(project);
        if (dll == null || !Files.isRegularFile(dll)) {
            SalamanderNotifier.show(project,
                    dll == null
                            ? "Сервер Salamander не найден: в плагине его нет. Пересоберите плагин "
                              + "(Tools/rider/plugin/build.sh) или укажите salamander.server.path в " + SalamanderProject.SETTINGS + "."
                            : "Сервер Salamander не найден по пути " + dll + ". Проверьте salamander.server.path "
                              + "в " + SalamanderProject.SETTINGS + " или переменную SALAMANDER_LSP_DLL.",
                    NotificationType.WARNING);
        }
    }
}
