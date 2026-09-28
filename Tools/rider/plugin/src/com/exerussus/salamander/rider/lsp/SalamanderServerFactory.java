package com.exerussus.salamander.rider.lsp;

import com.intellij.openapi.project.Project;
import com.redhat.devtools.lsp4ij.LanguageServerFactory;
import com.redhat.devtools.lsp4ij.server.StreamConnectionProvider;
import org.jetbrains.annotations.NotNull;

/** Сервер Salamander для LSP4IJ: объявлен в salamander-lsp4ij.xml, маска *.sal там же. */
public final class SalamanderServerFactory implements LanguageServerFactory {
    @Override
    public @NotNull StreamConnectionProvider createConnectionProvider(@NotNull Project project) {
        return new SalamanderConnectionProvider(project);
    }
}
