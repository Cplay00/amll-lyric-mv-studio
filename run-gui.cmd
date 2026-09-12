@echo off
REM AMLL Lyric MV Studio - build and launch the WinUI 3 application
REM 用法：
REM   run-gui.cmd                      仅启动
REM   run-gui.cmd --audio 歌曲.flac    启动并预载素材
setlocal

cd /d "%~dp0"

set GUI_EXE=gui\bin\Release\net8.0-windows10.0.19041.0\win-x64\AmllMvStudio.exe

REM 1) 工作台网页（GUI 预览用 WebView2 加载 dist 目录）尚未构建时自动构建
if not exist "dist\index.html" (
    echo [1/3] 首次运行，正在构建歌词工作台网页...
    if not exist "node_modules" (
        echo      正在安装前端依赖...
        call npm install || goto :fail
    )
    call npm run build || goto :fail
)

REM 2) 检查 .NET SDK
where dotnet >nul 2>nul || (
    echo 未找到 .NET SDK，请先安装：https://dotnet.microsoft.com/download
    goto :fail
)

REM 3) 构建并启动 GUI（Release 模式首次构建需要数分钟）
if not exist "%GUI_EXE%" (
    echo [2/3] 正在构建图形界面（首次需要几分钟）...
    dotnet build gui\AmllMvStudio.csproj -c Release -v q || goto :fail
)

echo [3/3] 启动 AMLL Lyric MV Studio...
"%GUI_EXE%" %*
goto :eof

:fail
echo.
echo 构建失败，请查看上方错误信息。
pause
exit /b 1
