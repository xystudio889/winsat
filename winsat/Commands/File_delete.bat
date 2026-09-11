@echo off
setlocal enabledelayedexpansion

:: 检查是否提供了文件参数
if "%~1"=="" (
    exit /b 1
)

:: 显示将要删除的文件列表
echo 将删除以下文件：
for %%f in (%*) do (
    echo   %%f
)
echo.

echo.
echo 开始删除...
for %%f in (%*) do (
    set "file=%%~f"
    echo "!file!"
    if exist "!file!" (
        del /f /q "!file!"
    ) else (
        echo 文件不存在，跳过: !file!
    )
)
echo 删除操作完成。

endlocal
exit /b 0