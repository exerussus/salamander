package com.exerussus.salamander.rider.newfile;

import com.exerussus.salamander.rider.SalamanderFileIconProvider;
import com.intellij.openapi.actionSystem.ActionUpdateThread;
import com.intellij.openapi.actionSystem.AnAction;
import com.intellij.openapi.actionSystem.AnActionEvent;
import com.intellij.openapi.command.WriteCommandAction;
import com.intellij.openapi.fileEditor.FileEditorManager;
import com.intellij.openapi.fileEditor.OpenFileDescriptor;
import com.intellij.openapi.project.DumbAware;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.ui.Messages;
import com.intellij.openapi.vfs.VfsUtil;
import com.intellij.openapi.vfs.VirtualFile;
import org.jetbrains.annotations.NotNull;

import java.io.IOException;

/** Один пункт меню: создать .sal по шаблону, открыть и поставить курсор в тело первого события. */
final class NewSalamanderFileAction extends AnAction implements DumbAware {
    private final Template template;

    NewSalamanderFileAction(Template template) {
        super(template.title(), template.description(), SalamanderFileIconProvider.ICON);
        this.template = template;
    }

    @Override
    public @NotNull ActionUpdateThread getActionUpdateThread() {
        return ActionUpdateThread.BGT;
    }

    @Override
    public void actionPerformed(@NotNull AnActionEvent e) {
        Project project = e.getProject();
        VirtualFile dir = SalamanderNewGroup.targetDir(e);
        if (project == null || dir == null) return;

        NewFileDialog dialog = new NewFileDialog(project, template, dir);
        if (!dialog.showAndGet()) return;

        String name = dialog.fileName();
        Template.Result r = template.render(name, dialog.selectedConsts(), dialog.selectedEvents());
        String sep = lineSeparator(dir, project.getBasePath());
        String text = r.text().replace("\n", sep);
        int caret = r.text().substring(0, r.caret()).replace("\n", sep).length();

        VirtualFile[] created = new VirtualFile[1];
        try {
            WriteCommandAction.writeCommandAction(project).withName("New Salamander " + template.title()).run(() -> {
                VirtualFile f = dir.createChildData(NewSalamanderFileAction.class, name + ".sal");
                VfsUtil.saveText(f, text);
                created[0] = f;
            });
        } catch (IOException ex) {
            Messages.showErrorDialog(project, "Не удалось создать " + name + ".sal: " + ex.getMessage(), "Salamander");
            return;
        }
        if (created[0] != null) {
            FileEditorManager.getInstance(project).openTextEditor(new OpenFileDescriptor(project, created[0], caret), true);
        }
    }

    /** Переводы строк — как у соседних .sal (в Unity-проектах обычно CRLF), иначе системные. */
    private static String lineSeparator(VirtualFile dir, String basePath) {
        for (VirtualFile d = dir; d != null; d = d.getParent()) {
            for (VirtualFile child : d.getChildren()) {
                if (!child.isDirectory() && SalamanderFileIconProvider.isSal(child.getName())) {
                    try {
                        String s = VfsUtil.loadText(child);
                        return s.contains("\r\n") ? "\r\n" : "\n";
                    } catch (IOException ignored) {
                        break;
                    }
                }
            }
            if (d.findChild("module.json") != null) break; // выше модуля не ходим
            if (basePath != null && d.getPath().equals(basePath)) break; // и выше проекта
        }
        return System.lineSeparator();
    }
}
