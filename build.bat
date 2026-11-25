@echo off
echo Smart derleniyor (.NET Framework 4.8)...
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo HATA: .NET Framework C# derleyicisi bulunamadi: %CSC%
    exit /b 1
)

set "TARGET=%~dp0SmartServer.exe"
set "TMPEXE=%~dp0SmartServer_build.tmp.exe"
set "STAGED=%~dp0SmartServer_update.exe"

del /f /q "%TMPEXE%" >nul 2>&1

"%CSC%" /nologo /utf8output /optimize+ /target:exe /out:"%TMPEXE%" ^
    /reference:System.Web.Extensions.dll ^
    /reference:System.Net.Http.dll ^
    /reference:System.Xml.Linq.dll ^
    /reference:System.Security.dll ^
    /reference:System.Drawing.dll ^
    "%~dp0backend\Models.cs" ^
    "%~dp0backend\Security.cs" ^
    "%~dp0backend\ComplianceGuard.cs" ^
    "%~dp0backend\ImageHostService.cs" ^
    "%~dp0backend\Storage.cs" ^
    "%~dp0backend\TunnelService.cs" ^
    "%~dp0backend\MarketplaceSchema.cs" ^
    "%~dp0backend\CompetitorAnalyzer.cs" ^
    "%~dp0backend\VisionAnalyzer.cs" ^
    "%~dp0backend\TrendyolCategoryEngine.cs" ^
    "%~dp0backend\MarketplaceResearchEngine.cs" ^
    "%~dp0backend\AiContentEngine.cs" ^
    "%~dp0backend\MarketplaceConnectors.cs" ^
    "%~dp0backend\Program.cs"

if errorlevel 1 (
    del /f /q "%TMPEXE%" >nul 2>&1
    echo Derleme sirasinda hata olustu!
    exit /b 1
)

move /y "%TMPEXE%" "%TARGET%" >nul 2>&1
if errorlevel 1 (
    move /y "%TMPEXE%" "%STAGED%" >nul 2>&1
    echo Guncel surum hazirlandi [SmartServer_update.exe] - calisan Smart otomatik guncellenecektir.
    exit /b 0
)
del /f /q "%STAGED%" >nul 2>&1
echo SmartServer.exe basariyla guncellendi.