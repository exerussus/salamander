import java.io.IOException;
import java.io.OutputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.List;
import java.util.stream.Collectors;
import java.util.stream.Stream;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

/**
 * Упаковать папку в zip/jar: java tools/Zip.java <архив> <база> [пути от базы...]
 * В JBR у Rider есть java и javac, но нет jar — а zip в Git Bash по умолчанию нет.
 * Пути в архиве — всегда через «/», порядок стабильный (сортировка).
 */
public class Zip {
    public static void main(String[] args) throws IOException {
        Path out = Paths.get(args[0]).toAbsolutePath();
        Path base = Paths.get(args[1]).toAbsolutePath();
        String[] roots = args.length > 2 ? java.util.Arrays.copyOfRange(args, 2, args.length) : new String[]{"."};
        Files.createDirectories(out.getParent());
        try (OutputStream os = Files.newOutputStream(out); ZipOutputStream zip = new ZipOutputStream(os)) {
            for (String r : roots) {
                List<Path> all;
                try (Stream<Path> s = Files.walk(base.resolve(r))) {
                    all = s.sorted().collect(Collectors.toList());
                }
                for (Path p : all) {
                    if (p.equals(out)) continue;
                    String name = base.relativize(p).toString().replace('\\', '/');
                    if (name.isEmpty() || name.equals(".")) continue;
                    if (Files.isDirectory(p)) {
                        zip.putNextEntry(new ZipEntry(name + "/"));
                    } else {
                        zip.putNextEntry(new ZipEntry(name));
                        Files.copy(p, zip);
                    }
                    zip.closeEntry();
                }
            }
        }
        System.out.println("zip: " + out);
    }
}
