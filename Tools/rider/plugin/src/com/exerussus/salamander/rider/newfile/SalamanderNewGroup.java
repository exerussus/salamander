package com.exerussus.salamander.rider.newfile;

import com.exerussus.salamander.rider.SalamanderProject;
import com.intellij.openapi.actionSystem.ActionGroup;
import com.intellij.openapi.actionSystem.ActionUpdateThread;
import com.intellij.openapi.actionSystem.AnAction;
import com.intellij.openapi.actionSystem.AnActionEvent;
import com.intellij.openapi.actionSystem.CommonDataKeys;
import com.intellij.openapi.actionSystem.Separator;
import com.intellij.openapi.project.DumbAware;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.vfs.VirtualFile;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/**
 * «Add / New → Salamander File» в проводнике Rider и в New обычного Project view.
 * Состав меню — из манифеста API ближайшего вверх от папки (или проекта):
 * язык (триггер, подписка, класс, енум) и все виды архетипов игры.
 * Видно только там, где есть Salamander.
 */
public final class SalamanderNewGroup extends ActionGroup implements DumbAware {
    @Override
    public @NotNull ActionUpdateThread getActionUpdateThread() {
        return ActionUpdateThread.BGT;
    }

    @Override
    public void update(@NotNull AnActionEvent e) {
        Project project = e.getProject();
        VirtualFile dir = targetDir(e);
        boolean visible = project != null && dir != null
                && (manifestFor(project, dir) != null || SalamanderProject.hasMarkers(project));
        e.getPresentation().setEnabledAndVisible(visible);
    }

    @Override
    public AnAction @NotNull [] getChildren(@Nullable AnActionEvent e) {
        if (e == null || e.getProject() == null) return AnAction.EMPTY_ARRAY;
        VirtualFile dir = targetDir(e);
        if (dir == null) return AnAction.EMPTY_ARRAY;

        ApiModel api = ApiModel.load(manifestFor(e.getProject(), dir));
        List<AnAction> out = new ArrayList<>();
        out.add(new NewSalamanderFileAction(new Template(Template.Kind.TRIGGER, api, null)));
        out.add(new NewSalamanderFileAction(new Template(Template.Kind.LISTENER, api, null)));
        out.add(new NewSalamanderFileAction(new Template(Template.Kind.CLASS, api, null)));
        out.add(new NewSalamanderFileAction(new Template(Template.Kind.ENUM, api, null)));
        if (api != null && !api.archetypes.isEmpty()) {
            out.add(Separator.create("Архетипы игры"));
            for (ApiModel.Archetype a : api.archetypes) {
                out.add(new NewSalamanderFileAction(new Template(Template.Kind.ARCHETYPE, api, a)));
            }
        }
        return out.toArray(AnAction.EMPTY_ARRAY);
    }

    /** Папка, в которой создаём: выделенная папка или папка выделенного файла. */
    static @Nullable VirtualFile targetDir(AnActionEvent e) {
        VirtualFile f = e.getData(CommonDataKeys.VIRTUAL_FILE);
        if (f == null) {
            VirtualFile[] many = e.getData(CommonDataKeys.VIRTUAL_FILE_ARRAY);
            if (many != null && many.length > 0) f = many[0];
        }
        if (f == null || !f.isInLocalFileSystem()) return null;
        return f.isDirectory() ? f : f.getParent();
    }

    private static @Nullable Path manifestFor(Project project, VirtualFile dir) {
        Path base = SalamanderProject.base(project);
        Path near = SalamanderProject.findManifestUp(dir.toNioPath(), base);
        return near != null ? near : SalamanderProject.findManifest(project);
    }
}
