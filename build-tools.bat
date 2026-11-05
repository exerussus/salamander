@echo off
rem ===========================================================================
rem  Salamander: rebuild LSP server, CLI checker, Rider plugin, VS Code extension.
rem
rem    build-tools.bat                        everything
rem    build-tools.bat rider                  LSP server + Rider plugin
rem    build-tools.bat rider -InstallRider    ... and install into Rider (Rider closed)
rem    build-tools.bat vscode -InstallVsCode  ... VS Code extension + install via `code`
rem    build-tools.bat -Tests                 ... plus engine tests
rem
rem  Targets: lsp, check, rider, vscode, all. Details: Tools\build-tools.ps1
rem ===========================================================================
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\build-tools.ps1" %*
set "RC=%ERRORLEVEL%"
rem started by double click (cmd /c "...build-tools.bat") - keep the window open
echo %CMDCMDLINE% | find /i "%~nx0" >nul && pause
exit /b %RC%
