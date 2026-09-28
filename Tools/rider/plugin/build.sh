#!/usr/bin/env bash
# ============================================================================
# Сборка плагина Salamander для Rider (и других IDE JetBrains) — без Gradle.
#
#   Tools/rider/plugin/build.sh          -> Tools/rider/plugin/build/Salamander-<версия>.zip
#
# Нужно на машине: Rider (из него берутся javac/java и jar-ы платформы),
# плагин LSP4IJ в этом Rider и .NET SDK 8 (сервер собирается в плагин).
# Всё ищется само; переопределить можно переменными:
#   RIDER_HOME   папка установки Rider (где lib/ и jbr/)
#   LSP4IJ_LIB   папка lib плагина LSP4IJ (…/plugins/lsp4ij/lib)
#   SERVER_FROM  готовая папка publish сервера вместо dotnet publish
# ============================================================================
set -euo pipefail
cd "$(dirname "$0")"
HERE="$(pwd)"
TOOLS="$(cd .. && cd .. && pwd)"          # <repo>/Tools
VERSION="$(sed -n 's:.*<version>\(.*\)</version>.*:\1:p' resources/META-INF/plugin.xml | head -1)"

case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) WIN=1; SEP=';' ;; *) WIN=0; SEP=':' ;; esac
native() { if [ "$WIN" = 1 ]; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
unixp()  { if [ "$WIN" = 1 ] && [ -n "$1" ]; then cygpath -u "$1"; else printf '%s' "$1"; fi; }
die()    { echo "build.sh: $*" >&2; exit 1; }

# ---- Rider -----------------------------------------------------------------
if [ -z "${RIDER_HOME:-}" ]; then
  for c in "$(unixp "${LOCALAPPDATA:-}")/Programs/Rider" "/c/Program Files/JetBrains/"*Rider* \
           "$HOME/Applications/Rider.app/Contents" "/Applications/Rider.app/Contents" \
           "$HOME/.local/share/JetBrains/Toolbox/apps/rider" /opt/rider*; do
    if [ -f "$c/lib/util.jar" ]; then RIDER_HOME="$c"; break; fi
  done
fi
[ -n "${RIDER_HOME:-}" ] && [ -d "$RIDER_HOME/lib" ] || die "не найден Rider — задайте RIDER_HOME"
RIDER_HOME="$(unixp "$RIDER_HOME")"

JAVAC="$(find "$RIDER_HOME/jbr" -path '*/bin/javac*' 2>/dev/null | head -1 || true)"
JAVA="$(find "$RIDER_HOME/jbr" -path '*/bin/java' -o -path '*/bin/java.exe' 2>/dev/null | head -1 || true)"
[ -n "$JAVAC" ] || JAVAC="$(command -v javac || true)"
[ -n "$JAVA" ]  || JAVA="$(command -v java || true)"
[ -n "$JAVAC" ] && [ -n "$JAVA" ] || die "нет javac/java (ни в $RIDER_HOME/jbr, ни в PATH)"

# ---- LSP4IJ ----------------------------------------------------------------
if [ -z "${LSP4IJ_LIB:-}" ]; then
  shopt -s nullglob                          # пути с пробелами — только глобами, не через ls
  for c in "$(unixp "${APPDATA:-}")/JetBrains/"Rider*/plugins/lsp4ij/lib \
           "$HOME/Library/Application Support/JetBrains/"Rider*/plugins/lsp4ij/lib \
           "$HOME/.local/share/JetBrains/"Rider*/lsp4ij/lib; do
    LSP4IJ_LIB="$c"                          # глоб сортирует по имени: последняя — новейшая Rider
  done
  shopt -u nullglob
fi
[ -n "${LSP4IJ_LIB:-}" ] && [ -d "$LSP4IJ_LIB" ] || die "не найден LSP4IJ — поставьте его в Rider или задайте LSP4IJ_LIB"
LSP4IJ_LIB="$(unixp "$LSP4IJ_LIB")"
TEXTMATE_LIB="$RIDER_HOME/plugins/textmate-plugin/lib"

echo "Rider:  $RIDER_HOME"
echo "LSP4IJ: $LSP4IJ_LIB"

# ---- чистая папка ----------------------------------------------------------
OUT="$HERE/build"
rm -rf "$OUT"
mkdir -p "$OUT/classes" "$OUT/Salamander/lib" "$OUT/Salamander/server" "$OUT/Salamander/bundles/salamander/syntaxes"

# ---- live templates из сниппетов VS Code (python необязателен: есть готовый xml) ----
PY="$(command -v python3 || command -v python || true)"
if [ -n "$PY" ] && "$PY" -c 'import sys; sys.exit(0 if sys.version_info[0] == 3 else 1)' 2>/dev/null; then
  "$PY" gen-live-templates.py
else
  echo "python3 нет — live templates из resources/liveTemplates/Salamander.xml как есть"
fi

# ---- ресурсы: значок один на все редакторы, живёт в Tools/rider/icons -----
cp -R resources/. "$OUT/classes/"
mkdir -p "$OUT/classes/icons"
# без блока <metadata> (C2PA от редактора картинок): IDE он не нужен, а весит больше рисунка
sed -E 's#<metadata>.*</metadata>##' "$TOOLS/rider/icons/salamander.svg" > "$OUT/classes/icons/salamander.svg"
# значок плагина в Settings → Plugins: тот же рисунок, крупнее
sed -E '1s/width="[0-9.]+"/width="40"/; 1s/height="[0-9.]+"/height="40"/' \
    "$OUT/classes/icons/salamander.svg" > "$OUT/classes/META-INF/pluginIcon.svg"

# ---- компиляция ------------------------------------------------------------
CP="$(native "$RIDER_HOME/lib")/*${SEP}$(native "$LSP4IJ_LIB")/*"
[ -d "$TEXTMATE_LIB" ] && CP="$CP${SEP}$(native "$TEXTMATE_LIB")/*${SEP}$(native "$TEXTMATE_LIB/modules")/*"
SOURCES=()
while IFS= read -r f; do SOURCES+=("$(native "$f")"); done < <(find "$HERE/src" -name '*.java' | sort)
"$JAVAC" --release 17 -encoding UTF-8 -proc:none -nowarn -cp "$CP" -d "$(native "$OUT/classes")" "${SOURCES[@]}"
"$JAVA" "$(native "$HERE/tools/Zip.java")" "$(native "$OUT/Salamander/lib/salamander.jar")" "$(native "$OUT/classes")" >/dev/null

# ---- сервер ----------------------------------------------------------------
if [ -n "${SERVER_FROM:-}" ]; then
  cp -R "$(unixp "$SERVER_FROM")/." "$OUT/Salamander/server/"
elif command -v dotnet >/dev/null 2>&1; then
  dotnet publish "$(native "$TOOLS/DslLsp")" -c Release --nologo -v q -o "$(native "$OUT/Salamander/server")"
elif [ -f "$TOOLS/DslLsp/publish/DslLsp.dll" ]; then
  echo "dotnet нет — беру готовый Tools/DslLsp/publish"
  cp -R "$TOOLS/DslLsp/publish/." "$OUT/Salamander/server/"
else
  die "нет ни dotnet, ни Tools/DslLsp/publish — сервер собрать не из чего"
fi
rm -f "$OUT/Salamander/server/"*.pdb
[ -f "$OUT/Salamander/server/DslLsp.dll" ] || die "в сервере нет DslLsp.dll"

# ---- грамматика TextMate: та же, что у VS Code -----------------------------
cp bundle/package.json "$OUT/Salamander/bundles/salamander/"
cp "$TOOLS/vscode-salamander/language-configuration.json" "$OUT/Salamander/bundles/salamander/"
cp "$TOOLS/vscode-salamander/syntaxes/"*.json "$OUT/Salamander/bundles/salamander/syntaxes/"

# ---- архив для Install Plugin from Disk ------------------------------------
ZIP="$OUT/Salamander-$VERSION.zip"
"$JAVA" "$(native "$HERE/tools/Zip.java")" "$(native "$ZIP")" "$(native "$OUT")" Salamander >/dev/null
echo "готово: ${ZIP#"$HERE/"}"
