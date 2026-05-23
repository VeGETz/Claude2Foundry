@echo off
set C2F_WRAPPER=1
set BIN=%C2F_BIN%
if "%BIN%"=="" set BIN=Claude2Foundry.exe
:loop
"%BIN%" %*
if errorlevel 75 if not errorlevel 76 (
  echo [c2f] adapter exited 75 ^(restart requested^); relaunching...
  goto loop
)
exit /b %ERRORLEVEL%
