<#
  Пересборка инструментов Salamander одной командой (Windows).

    build-tools.bat                         LSP-сервер, чекер, плагин Rider, расширение VS Code
    build-tools.bat rider                   только то, что нужно плагину Rider (сервер + плагин)
    build-tools.bat rider -InstallRider     ... и сразу поставить плагин в Rider (Rider должен быть закрыт)
    build-tools.bat vscode -InstallVsCode   ... расширение VS Code и установка через `code`
    build-tools.bat -Tests                  ... плюс тесты движка (dotnet test Tools/DslTests)

  Цели: lsp, check, rider, vscode (по умолчанию — все). Плагину Rider и
  расширению VS Code нужен свежий сервер, поэтому lsp добавляется к ним сам.

  Что нужно на машине:
    lsp, check, тесты  — .NET SDK 8 (dotnet);
    rider              — Rider с плагином LSP4IJ и Git for Windows (bash: build.sh
                         плагина — bash-скрипт; java/javac он берёт из самого Rider);
    vscode             — Node.js (npm); vsce ставится через npx при первом запуске.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]] $Targets,
    [switch] $Tests,
    [switch] $InstallRider,
    [switch] $InstallVsCode
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Repo  = Split-Path -Parent $PSScriptRoot
$Tools = Join-Path $Repo 'Tools'
$LspDir     = Join-Path $Tools 'DslLsp'
$LspPublish = Join-Path $LspDir 'publish'
$RiderDir   = Join-Path $Tools 'rider\plugin'
$VsCodeDir  = Join-Path $Tools 'vscode-salamander'

# ---------------------------------------------------------------------------
# цели
# ---------------------------------------------------------------------------
$known = @('lsp', 'check', 'rider', 'vscode')
$want = @()
foreach ($t in @($Targets)) {
    if ([string]::IsNullOrWhiteSpace($t)) { continue }
    foreach ($p in ($t -split ',')) {
        $x = $p.Trim().ToLowerInvariant()
        if ($x -eq '') { continue }
        if ($x -eq 'all') { $want += $known; continue }
        if ($known -notcontains $x) {
            Write-Host "Неизвестная цель '$p'. Есть: $($known -join ', '), all." -ForegroundColor Red
            exit 2
        }
        $want += $x
    }
}
if ($want.Count -eq 0) { $want = $known }
if ($InstallRider -and $want -notcontains 'rider') { $want += 'rider' }
if ($InstallVsCode -and $want -notcontains 'vscode') { $want += 'vscode' }
# сервер едет в плагин Rider и нужен расширению VS Code — без свежего смысла нет
if (($want -contains 'rider' -or $want -contains 'vscode') -and $want -notcontains 'lsp') { $want = @('lsp') + $want }
$want = $want | Select-Object -Unique

# ---------------------------------------------------------------------------
# шаги
# ---------------------------------------------------------------------------
$results = New-Object System.Collections.Generic.List[object]
$failed = $false

function Invoke-Step([string] $Name, [scriptblock] $Body) {
    Write-Host ''
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $artifact = $null
    $status = 'OK'
    try { $artifact = & $Body }
    catch {
        $status = 'ОШИБКА'
        $script:failed = $true
        Write-Host $_.Exception.Message -ForegroundColor Red
    }
    $sw.Stop()
    $script:results.Add([pscustomobject]@{
        Шаг      = $Name
        Итог     = $status
        Секунд   = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        Результат = $artifact
    })
    return ($status -eq 'OK')
}

function Require([string] $Cmd, [string] $Hint) {
    if (-not (Get-Command $Cmd -ErrorAction SilentlyContinue)) { throw "Не найден '$Cmd' — $Hint" }
}

function Exec([string] $What) {
    # внешняя команда: ненулевой код возврата — ошибка шага
    if ($LASTEXITCODE -ne 0) { throw "$What завершился с кодом $LASTEXITCODE" }
}

function Find-GitBash {
    # bash из Git for Windows. Не C:\Windows\System32\bash.exe: это WSL, там другие пути
    $cands = @()
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        $gitRoot = Split-Path -Parent (Split-Path -Parent $git.Source)   # ...\Git\cmd\git.exe -> ...\Git
        $cands += (Join-Path $gitRoot 'bin\bash.exe')
        $cands += (Join-Path $gitRoot 'usr\bin\bash.exe')
    }
    if ($env:ProgramFiles) { $cands += (Join-Path $env:ProgramFiles 'Git\bin\bash.exe') }
    if (${env:ProgramFiles(x86)}) { $cands += (Join-Path ${env:ProgramFiles(x86)} 'Git\bin\bash.exe') }
    if ($env:LOCALAPPDATA) { $cands += (Join-Path $env:LOCALAPPDATA 'Programs\Git\bin\bash.exe') }
    foreach ($c in $cands) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

function Find-RiderPluginsDir {
    # куда Rider распаковывает плагины, поставленные с диска: %APPDATA%\JetBrains\Rider<версия>\plugins
    $base = Join-Path $env:APPDATA 'JetBrains'
    if (-not (Test-Path $base)) { return $null }
    $dirs = @(Get-ChildItem -Path $base -Directory -Filter 'Rider*' | Sort-Object Name)
    if ($dirs.Count -eq 0) { return $null }
    # та версия Rider, где стоит LSP4IJ (плагину он нужен); иначе новейшая
    $withLsp = @($dirs | Where-Object { Test-Path (Join-Path $_.FullName 'plugins\lsp4ij') })
    $pick = if ($withLsp.Count -gt 0) { $withLsp[-1] } else { $dirs[-1] }
    return (Join-Path $pick.FullName 'plugins')
}

# ---------------------------------------------------------------------------
# LSP-сервер
# ---------------------------------------------------------------------------
if ($want -contains 'lsp') {
    $ok = Invoke-Step 'LSP-сервер (Tools/DslLsp/publish)' {
        Require 'dotnet' 'поставьте .NET SDK 8'
        & dotnet publish $LspDir -c Release -o $LspPublish --nologo -v q | Out-Host
        Exec 'dotnet publish'
        if (-not (Test-Path (Join-Path $LspPublish 'DslLsp.dll'))) { throw 'после publish нет DslLsp.dll' }
        $LspPublish
    }
    if (-not $ok) {
        Write-Host 'Сервер не собрался — плагин Rider и расширение VS Code собирать не из чего.' -ForegroundColor Red
        $want = $want | Where-Object { $_ -ne 'rider' -and $_ -ne 'vscode' }
    }
}

# ---------------------------------------------------------------------------
# CLI-чекер
# ---------------------------------------------------------------------------
if ($want -contains 'check') {
    [void](Invoke-Step 'CLI-чекер (Tools/DslCheck)' {
        Require 'dotnet' 'поставьте .NET SDK 8'
        & dotnet build (Join-Path $Tools 'DslCheck') -c Release --nologo -v q | Out-Host
        Exec 'dotnet build DslCheck'
        Join-Path $Tools 'DslCheck\bin\Release'
    })
}

# ---------------------------------------------------------------------------
# тесты движка
# ---------------------------------------------------------------------------
if ($Tests) {
    [void](Invoke-Step 'Тесты движка (Tools/DslTests)' {
        Require 'dotnet' 'поставьте .NET SDK 8'
        & dotnet test (Join-Path $Tools 'DslTests') --nologo -v q | Out-Host
        Exec 'dotnet test'
        'все прошли'
    })
}

# ---------------------------------------------------------------------------
# плагин Rider
# ---------------------------------------------------------------------------
if ($want -contains 'rider') {
    $riderZip = $null
    $ok = Invoke-Step 'Плагин Rider (Tools/rider/plugin/build)' {
        $bash = Find-GitBash
        if (-not $bash) { throw 'не найден bash из Git for Windows (build.sh плагина — bash-скрипт). Поставьте Git for Windows.' }
        $script = (Join-Path $RiderDir 'build.sh') -replace '\\', '/'
        # сервер только что собран — плагин берёт его, а не публикует второй раз
        $env:SERVER_FROM = $LspPublish
        try { & $bash $script | Out-Host; Exec 'build.sh' }
        finally { Remove-Item Env:\SERVER_FROM -ErrorAction SilentlyContinue }
        $zip = Get-ChildItem -Path (Join-Path $RiderDir 'build') -Filter 'Salamander-*.zip' |
               Sort-Object LastWriteTime | Select-Object -Last 1
        if (-not $zip) { throw 'build.sh отработал, но zip плагина не найден' }
        $script:riderZip = $zip.FullName
        $zip.FullName
    }

    if ($ok -and $InstallRider) {
        [void](Invoke-Step 'Установка плагина в Rider' {
            if (Get-Process -Name 'rider64', 'rider' -ErrorAction SilentlyContinue) {
                throw 'Rider запущен — закройте его (плагины ставятся только в закрытый Rider) и повторите с -InstallRider, либо поставьте zip через Settings → Plugins → ⚙ → Install Plugin from Disk.'
            }
            $plugins = Find-RiderPluginsDir
            if (-not $plugins) { throw "не найдена папка настроек Rider в $env:APPDATA\JetBrains" }
            New-Item -ItemType Directory -Force -Path $plugins | Out-Null
            $target = Join-Path $plugins 'Salamander'
            if (Test-Path $target) { Remove-Item -Recurse -Force $target }
            # в архиве верхняя папка Salamander/ — ровно так её распаковывает и сам Rider
            Expand-Archive -Path $riderZip -DestinationPath $plugins -Force
            if (-not (Test-Path (Join-Path $target 'lib\salamander.jar'))) { throw "после распаковки нет $target\lib\salamander.jar" }
            $target
        })
    }
}

# ---------------------------------------------------------------------------
# расширение VS Code
# ---------------------------------------------------------------------------
if ($want -contains 'vscode') {
    $vsix = $null
    $ok = Invoke-Step 'Расширение VS Code (Tools/vscode-salamander)' {
        Require 'npm.cmd' 'поставьте Node.js'
        Push-Location $VsCodeDir
        try {
            if (-not (Test-Path 'node_modules')) { & npm.cmd install --no-audit --no-fund | Out-Host; Exec 'npm install' }
            & npm.cmd run compile | Out-Host; Exec 'npm run compile (tsc)'
            $version = (Get-Content 'package.json' -Raw -Encoding UTF8 | ConvertFrom-Json).version
            $out = Join-Path $VsCodeDir "salamander-$version.vsix"
            & npx.cmd --yes '@vscode/vsce' package --allow-missing-repository --skip-license -o $out | Out-Host
            Exec 'vsce package'
            $script:vsix = $out
            $out
        }
        finally { Pop-Location }
    }

    if ($ok -and $InstallVsCode) {
        [void](Invoke-Step 'Установка расширения в VS Code' {
            Require 'code' 'нет команды code в PATH (VS Code → Command Palette → Shell Command: Install ''code'' command)'
            & code --install-extension $vsix --force | Out-Host
            Exec 'code --install-extension'
            'установлено — выполните Reload Window'
        })
    }
}

# ---------------------------------------------------------------------------
# итог
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== Итог ===' -ForegroundColor Cyan
$results | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Host
if ($failed) {
    Write-Host 'Есть ошибки — см. выше.' -ForegroundColor Red
    exit 1
}
if ($want -contains 'rider' -and -not $InstallRider) {
    Write-Host 'Rider: Settings → Plugins → ⚙ → Install Plugin from Disk → zip выше, затем перезапуск. Или запустите с -InstallRider при закрытом Rider.'
}
if ($want -contains 'vscode' -and -not $InstallVsCode) {
    Write-Host 'VS Code: Extensions → … → Install from VSIX → файл выше, затем Reload Window. Или запустите с -InstallVsCode.'
}
exit 0
