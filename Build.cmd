@echo off
REM TODO: Passing -updatesdk to this script propagates all the way to eng/common/build.ps1 and causes msbuild error. Possibly remove the param once read?
REM for %%A IN (%*) do (
  REM if "%%A" == "-updatesdk" (
    powershell -ExecutionPolicy ByPass -NoProfile -command "& """%~dp0eng\updatesdk.ps1""""
    if %ErrorLevel% NEQ 0 (
      exit /b 1
    )
  REM )
REM )
powershell -ExecutionPolicy ByPass -NoProfile -command "& """%~dp0eng\common\Build.ps1""" -restore -build %*"
exit /b %ErrorLevel%
