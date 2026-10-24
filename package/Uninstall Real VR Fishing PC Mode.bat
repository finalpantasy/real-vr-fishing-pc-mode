@echo off
rem Removes Real VR Fishing PC Mode from the game folder this file sits in.
rem Your saves, settings and the game itself are not touched.
setlocal
cd /d "%~dp0"

if not exist "RealVrFishing.exe" (
    echo This file must be inside the Real VR Fishing game folder, next to RealVrFishing.exe.
    echo In Steam: right-click Real VR Fishing, Manage, Browse local files.
    pause
    exit /b 1
)

echo This removes Real VR Fishing PC Mode, so the game runs in VR again.
echo Your saves and the game itself are not touched.
echo.
choice /m "Remove it now"
if errorlevel 2 exit /b 0

del /q "BepInEx\plugins\RealVRFishingPCMode.dll" 2>nul
del /q "BepInEx\patchers\RealVRFishingPCMode.Patcher.dll" 2>nul
del /q "BepInEx\config\com.finalpantasy.realvrfishing.pcmode.cfg" 2>nul
del /q "BepInEx\plugins\RealVRFishingPCMode-LICENSE.txt" "BepInEx\plugins\RealVRFishingPCMode-THIRD-PARTY-NOTICES.txt" 2>nul
del /q "How to install.txt" 2>nul

rem BepInEx (the mod loader) goes too, unless other mods still use it
set "other="
if exist "BepInEx\plugins" for /r "BepInEx\plugins" %%f in (*.dll) do set "other=1"
if exist "BepInEx\patchers" for /r "BepInEx\patchers" %%f in (*.dll) do set "other=1"
if defined other (
    echo Other BepInEx mods were found, so the BepInEx mod loader stays installed.
) else (
    rmdir /s /q "BepInEx" 2>nul
    del /q "winhttp.dll" "doorstop_config.ini" ".doorstop_version" "changelog.txt" 2>nul
)

echo.
echo Done. Real VR Fishing PC Mode is removed.
pause
rem delete this uninstaller too
(goto) 2>nul & del "%~f0"
