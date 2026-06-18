@echo off
echo === Iniciando processo de publicacao: NouzertGames ===

:: Verifica se o dotnet esta instalado
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo ERRO: SDK do .NET nao encontrado. Instale o .NET 8 SDK.
    pause
    exit /b
)

echo Limpando pastas antigas...
taskkill /F /IM NouzertGames.exe >nul 2>&1
rd /s /q "App" >nul 2>&1
rd /s /q "dist" >nul 2>&1
rd /s /q "publish" >nul 2>&1
rd /s /q "Aplicativo" >nul 2>&1
if not exist "App" mkdir "App"
dotnet clean -c Release

echo Gerando executavel (Single File)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false --output ./App

if %errorlevel% neq 0 (
    echo.
    echo [ERRO] Falha na geracao do executavel! 
    echo Verifique se o app ou o Visual Studio estao bloqueando os arquivos.
    pause
) else (
    del /q "App\*.pdb" >nul 2>&1
    del /q "App\*.xml" >nul 2>&1
    del /q "App\*.json" >nul 2>&1
    for %%F in ("App\*") do (
        if /i not "%%~nxF"=="NouzertGames.exe" del /q "%%~fF" >nul 2>&1
    )
    for /d %%D in ("App\*") do rd /s /q "%%~fD" >nul 2>&1
    rd /s /q "bin" >nul 2>&1
    rd /s /q "obj" >nul 2>&1
    echo.
    echo [SUCESSO] Seu exe foi gerado na pasta 'App'.
    start ./App
    pause
)
