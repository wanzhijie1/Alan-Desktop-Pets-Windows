@echo off
setlocal
set "fengge=%~dp0GoodThingPet\GoodThingPet.exe"
set "sunpet=%~dp0SunPet\SunPet.exe"
set "shaolan=%~dp0ShaolanPet\ShaolanPet.exe"
if not exist "%fengge%" set "fengge=%~dp0GoodThingPet\build\windows\GoodThingPet.exe"
if not exist "%sunpet%" set "sunpet=%~dp0SunPet\build\windows\SunPet.exe"
if not exist "%shaolan%" set "shaolan=%~dp0ShaolanPet\build\windows\ShaolanPet.exe"
if not exist "%fengge%" goto missing
if not exist "%sunpet%" goto missing
if not exist "%shaolan%" goto missing
start "" "%fengge%" --corner-offset 0
start "" "%sunpet%" --corner-offset 284
start "" "%shaolan%" --corner-offset 488
exit /b 0
:missing
echo Build first: powershell -NoProfile -ExecutionPolicy Bypass -File build-all.ps1 -SelfTest
exit /b 1
