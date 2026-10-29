@echo off
chcp 936 >nul
cd /d "%~dp0"

if not exist "Songyu.exe" goto missing

echo 正在启动凇语 MCloader ...
echo.

"Songyu.exe"

set code=%ERRORLEVEL%
if not "%code%"=="0" goto failed
exit /b 0

:missing
echo 找不到 Songyu.exe，请确认它与本文件位于同一目录。
pause
exit /b 1

:failed
echo.
echo 程序已退出，退出码 %code%
echo 若反复失败，请把上面的信息发给我。
pause
exit /b %code%
