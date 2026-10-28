@echo off
setlocal
rem ============================================================
rem  Glink quick launcher (double-click to run)
rem    (no args)   launch the latest x64 Release build
rem    build       rebuild Release first, then launch
rem  NOTE: "dotnet run" does not work in this project (the path
rem        contains non-ASCII chars and parens), so the exe is
rem        launched directly.
rem ============================================================

set "BASE=%~dp0Moodia\bin\x64\Release"

if /i "%~1"=="build" (
    echo [Glink] Building x64 Release ...
    dotnet build "%~dp0Moodia\Glink.csproj" -c Release -p:Platform=x64 -v q -nologo
    if errorlevel 1 (
        echo [Glink] Build failed, launch cancelled.
        pause
        exit /b 1
    )
)

rem The TFM folder (net10.0-...) changes with SDK upgrades,
rem so pick the newest one that contains win-x64\Glink.exe.
set "EXE="
set "EXEDIR="
for /f "delims=" %%D in ('dir /b /ad /o-d "%BASE%" 2^>nul') do (
    if not defined EXE if exist "%BASE%\%%D\win-x64\Glink.exe" (
        set "EXE=%BASE%\%%D\win-x64\Glink.exe"
        set "EXEDIR=%BASE%\%%D\win-x64"
    )
)

if not defined EXE (
    echo [Glink] Release build not found. Run: "%~nx0" build
    pause
    exit /b 1
)

echo [Glink] Starting: %EXE%
start "" /d "%EXEDIR%" "%EXE%"
endlocal
