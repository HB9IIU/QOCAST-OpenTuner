@echo off
setlocal

title Build QOCAST OpenTuner
cd /d "%~dp0"

echo.
echo ========================================
echo   Building QOCAST OpenTuner - Release
echo ========================================
echo.

tasklist /FI "IMAGENAME eq opentuner.exe" 2>NUL | find /I "opentuner.exe" >NUL
if not errorlevel 1 (
    echo ERROR: OpenTuner is currently running.
    echo.
    echo Close the OpenTuner window, then run this build again.
    echo Windows cannot replace opentuner.exe while the application is open.
    goto :failed
)

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

if not exist "%VSWHERE%" (
    echo ERROR: Visual Studio Build Tools were not found.
    echo Install Visual Studio 2022 Build Tools with the .NET desktop build tools workload.
    goto :failed
)

for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"

if not defined MSBUILD (
    echo ERROR: MSBuild was not found.
    echo Modify Visual Studio 2022 Build Tools and install .NET desktop build tools.
    goto :failed
)

echo Using:
echo %MSBUILD%
echo.

"%MSBUILD%" "opentuner.sln" /restore /t:Build /p:RestorePackagesConfig=true /p:Configuration=Release /p:Platform="Any CPU" /m /nologo /verbosity:minimal

if errorlevel 1 goto :failed

echo.
echo ========================================
echo   BUILD SUCCESSFUL
echo ========================================
echo.
echo Application:
echo %CD%\bin\Release\opentuner.exe
echo.
start "" explorer.exe "%CD%\bin\Release"
pause
exit /b 0

:failed
echo.
echo ========================================
echo   BUILD FAILED
echo ========================================
echo Review the error messages above.
echo.
pause
exit /b 1
