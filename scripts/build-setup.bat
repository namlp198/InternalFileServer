@echo off
setlocal EnableExtensions

set "ROOT=%~dp0.."
for %%I in ("%ROOT%") do set "ROOT=%%~fI"

set "PROJECT=%ROOT%\InternalFileServer.csproj"
set "ARTIFACTS=%ROOT%\artifacts"
set "PUBLISH_DIR=%ARTIFACTS%\publish"
set "INSTALLER_DIR=%ARTIFACTS%\installer"
set "INNO_SCRIPT=%ROOT%\installer\InternalFileServer.iss"
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"

if not exist "%PROJECT%" (
    echo Project file not found: "%PROJECT%"
    exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo dotnet SDK was not found in PATH.
    exit /b 1
)

if not exist "%ISCC%" (
    for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do (
        set "ISCC=%%I"
        goto :found_iscc
    )
)

:found_iscc
if not exist "%ISCC%" (
    echo Inno Setup compiler was not found.
    echo Install Inno Setup 6 or add ISCC.exe to PATH.
    exit /b 1
)

echo Cleaning artifacts...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%INSTALLER_DIR%" rmdir /s /q "%INSTALLER_DIR%"
mkdir "%PUBLISH_DIR%" >nul 2>nul
mkdir "%INSTALLER_DIR%" >nul 2>nul

echo Publishing self-contained win-x64 app...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o "%PUBLISH_DIR%"
if errorlevel 1 exit /b 1

echo Creating runtime helper files...
(
    echo @echo off
    echo cd /d "%%~dp0"
    echo echo Internal File Server is starting on http://localhost:5088
    echo echo Press Ctrl+C to stop.
    echo InternalFileServer.exe --urls http://0.0.0.0:5088
) > "%PUBLISH_DIR%\start-server.bat"

(
    echo Set shell = CreateObject^("WScript.Shell"^)
    echo shell.CurrentDirectory = CreateObject^("Scripting.FileSystemObject"^).GetParentFolderName^(WScript.ScriptFullName^)
    echo shell.Run "start-server.bat", 0, False
) > "%PUBLISH_DIR%\start-server-hidden.vbs"

if not exist "%PUBLISH_DIR%\uploads" mkdir "%PUBLISH_DIR%\uploads"

echo Building installer with Inno Setup...
"%ISCC%" /DSourceDir="%PUBLISH_DIR%" "%INNO_SCRIPT%"
if errorlevel 1 exit /b 1

echo.
echo Done.
echo Installer output:
dir /b "%INSTALLER_DIR%\*.exe"

endlocal
