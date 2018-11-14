@echo off
REM TODO: pass -updatesdk to Build.cmd
CALL %~dp0..\Build.cmd -test -sign -pack -publish -ci %*
exit /b %ErrorLevel%