@echo off
title Smart - Pazaryeri Urun Yukleme ve Stok Otomasyonu
set FIRST_RUN=1

taskkill /F /IM SmartServer_new.exe >nul 2>&1
del /f /q "%~dp0SmartServer_new.exe" >nul 2>&1

:START_LOOP
if exist "%~dp0SmartServer_update.exe" (
    taskkill /F /IM SmartServer.exe >nul 2>&1
    timeout /t 1 /nobreak >nul
    move /y "%~dp0SmartServer_update.exe" "%~dp0SmartServer.exe" >nul 2>&1
)

if not exist "%~dp0SmartServer.exe" (
    call "%~dp0build.bat"
    if errorlevel 1 (
        pause
        exit /b 1
    )
)

if "%FIRST_RUN%"=="1" (
    set FIRST_RUN=0
    "%~dp0SmartServer.exe" --supervised
) else (
    "%~dp0SmartServer.exe" --supervised --no-browser
)

if errorlevel 42 (
    if not errorlevel 43 (
        echo [Smart] Yeni surum uygulanip otomatik yeniden baslatiliyor...
        timeout /t 1 /nobreak >nul
        move /y "%~dp0SmartServer_update.exe" "%~dp0SmartServer.exe" >nul 2>&1
        goto START_LOOP
    )
)

pause
