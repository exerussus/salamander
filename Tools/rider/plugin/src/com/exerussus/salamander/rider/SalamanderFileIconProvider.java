package com.exerussus.salamander.rider;

import com.intellij.ide.FileIconProvider;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.util.IconLoader;
import com.intellij.openapi.vfs.VirtualFile;

import javax.swing.Icon;

/**
 * Значок саламандры у .sal — в дереве, во вкладках, в поиске. Тип файла не
 * трогает: какой бы он ни был (TextMate или текст), картинка наша.
 */
public final class SalamanderFileIconProvider implements FileIconProvider {
    public static final Icon ICON = IconLoader.getIcon("/icons/salamander.svg", SalamanderFileIconProvider.class);

    @Override
    public Icon getIcon(VirtualFile file, int flags, Project project) {
        if (file == null || file.isDirectory()) return null;
        return isSal(file.getName()) ? ICON : null;
    }

    public static boolean isSal(String name) {
        return name.regionMatches(true, name.length() - 4, ".sal", 0, 4);
    }
}
