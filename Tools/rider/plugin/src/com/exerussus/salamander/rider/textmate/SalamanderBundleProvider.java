package com.exerussus.salamander.rider.textmate;

import com.exerussus.salamander.rider.SalamanderPlugin;
import org.jetbrains.plugins.textmate.api.TextMateBundleProvider;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

/**
 * Грамматика Salamander для TextMate — из папки плагина (bundles/salamander),
 * та же, что у VS Code. Руками в Settings → TextMate Bundles больше ничего
 * добавлять не нужно.
 */
public final class SalamanderBundleProvider implements TextMateBundleProvider {
    @Override
    public List<PluginBundle> getBundles() {
        Path dir = SalamanderPlugin.dir();
        if (dir == null) return List.of();
        Path bundle = dir.resolve("bundles").resolve("salamander");
        return Files.isDirectory(bundle) ? List.of(new PluginBundle("Salamander", bundle)) : List.of();
    }
}
