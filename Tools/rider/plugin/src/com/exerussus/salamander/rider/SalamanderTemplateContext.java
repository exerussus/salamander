package com.exerussus.salamander.rider;

import com.intellij.codeInsight.template.TemplateActionContext;
import com.intellij.codeInsight.template.TemplateContextType;
import com.intellij.openapi.vfs.VirtualFile;
import com.intellij.psi.PsiFile;
import org.jetbrains.annotations.NotNull;

/** Контекст live templates «Salamander»: любой .sal, какого бы типа ни был файл. */
public final class SalamanderTemplateContext extends TemplateContextType {
    public SalamanderTemplateContext() {
        super("Salamander");
    }

    @Override
    public boolean isInContext(@NotNull TemplateActionContext context) {
        PsiFile file = context.getFile();
        VirtualFile v = file.getVirtualFile();
        return SalamanderFileIconProvider.isSal(v != null ? v.getName() : file.getName());
    }
}
