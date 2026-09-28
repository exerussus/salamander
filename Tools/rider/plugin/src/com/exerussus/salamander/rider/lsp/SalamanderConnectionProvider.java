package com.exerussus.salamander.rider.lsp;

import com.exerussus.salamander.rider.SalamanderPlugin;
import com.exerussus.salamander.rider.SalamanderProject;
import com.intellij.execution.configurations.GeneralCommandLine;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.vfs.VirtualFile;
import com.redhat.devtools.lsp4ij.server.OSProcessStreamConnectionProvider;

import java.nio.charset.StandardCharsets;
import java.nio.file.Path;

/**
 * «dotnet DslLsp.dll» в корне проекта. Путь к серверу и dotnet ищутся сами
 * (SalamanderProject.serverDll, SalamanderPlugin.dotnet) — кавычек и путей с
 * пробелами руками больше никто не пишет. Настройки уходят в initialize.
 */
final class SalamanderConnectionProvider extends OSProcessStreamConnectionProvider {
    private final Project project;

    SalamanderConnectionProvider(Project project) {
        this.project = project;

        String dotnet = SalamanderPlugin.dotnet();
        Path dll = SalamanderProject.serverDll(project);

        GeneralCommandLine cl = new GeneralCommandLine(
                dotnet != null ? dotnet : "dotnet",
                dll != null ? dll.toString() : "DslLsp.dll")
                .withCharset(StandardCharsets.UTF_8);
        String base = project.getBasePath();
        if (base != null) cl = cl.withWorkDirectory(base);
        setCommandLine(cl);

        SalamanderPlugin.LOG.info("salamander-lsp: " + cl.getCommandLineString());
    }

    @Override
    public Object getInitializationOptions(VirtualFile rootUri) {
        return SalamanderProject.initializationOptions(project);
    }
}
