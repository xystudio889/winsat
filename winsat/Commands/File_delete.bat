@echo off
setlocal enabledelayedexpansion

:: 检查是否提供了文件参数
if "%~1"=="" (
    exit /b 1
)

for %%f in (%*) do (
    set "file=%%~f"
    if exist "!file!" (
        del /f /q "!file!"
    )
)

endlocal
exit /b 0