Write-Host "=== Iniciando processo de publicacao: NouzertGames ===" -ForegroundColor Cyan

if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "SDK do .NET nao encontrado. Por favor, instale o .NET 8 SDK."
    pause
    exit
}

Write-Host "Limpando pastas antigas..."
Get-Process -Name "NouzertGames" -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($folder in @("./App", "./dist", "./publish", "./Aplicativo")) {
    if (Test-Path $folder) { Remove-Item -Recurse -Force $folder -ErrorAction SilentlyContinue }
}
New-Item -ItemType Directory -Path "./App" -Force | Out-Null
dotnet clean -c Release

Write-Host "Gerando executavel (Single File)..." -ForegroundColor Yellow
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false --output ./App

if ($LASTEXITCODE -eq 0) {
    Get-ChildItem "./App" -Force | Where-Object { $_.Name -ne "NouzertGames.exe" } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($folder in @("./bin", "./obj")) {
        if (Test-Path $folder) { Remove-Item -Recurse -Force $folder -ErrorAction SilentlyContinue }
    }
    Write-Host "`n[SUCESSO] Seu exe foi gerado na pasta 'App'." -ForegroundColor Green
    explorer ./App
} else {
    Write-Host "`n[ERRO] Falha na geracao do executavel." -ForegroundColor Red
}
pause
