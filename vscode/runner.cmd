@echo off
rem Task terminals cannot start Code.exe directly, so the task runs this through cmd.exe.
rem COMPILERS_NODE is VS Code's own executable, run as Node; runner.js reads its arguments from COMPILERS_RUNNER_ARGS.
set ELECTRON_RUN_AS_NODE=1
"%COMPILERS_NODE%" "%~dp0runner.js"
exit /b %ERRORLEVEL%
