# Compila BeatTag.exe (single-file autocontenido, win-x64) desde EtiquetadorNet.
# Uso:  .\Compilar-BeatTag.ps1            -> compila (pasando los tests antes)
#       .\Compilar-BeatTag.ps1 -SinTests  -> compila sin pasar los tests
#       .\Compilar-BeatTag.ps1 -Ejecutar  -> compila y abre la app al terminar
param(
    [switch]$SinTests,
    [switch]$Ejecutar,
    [switch]$CerrarApp   # cierra BeatTag si esta abierto, sin preguntar
)

$ErrorActionPreference = 'Stop'
if ($PSScriptRoot) { $dir = $PSScriptRoot } else { $dir = Split-Path -Parent $MyInvocation.MyCommand.Path }
Set-Location $dir

$sln     = Join-Path $dir 'EtiquetadorNet'
$proyecto= Join-Path $sln 'Etiquetador.App\Etiquetador.App.csproj'
$tests   = Join-Path $sln 'Etiquetador.Tests\Etiquetador.Tests.csproj'
$salida  = Join-Path $sln 'publicado'
$exe     = Join-Path $salida 'BeatTag.exe'

Write-Host ""
Write-Host "===== Compilar BeatTag =====" -ForegroundColor Cyan
Write-Host ""

# --- Comprobaciones previas ---
if (-not (Test-Path $proyecto)) {
    Write-Host "No encuentro el proyecto: $proyecto" -ForegroundColor Red
    Read-Host "Enter para salir"; exit 1
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host "No esta instalado el SDK de .NET." -ForegroundColor Red
    Write-Host "Descargalo (NET 9) en: https://dotnet.microsoft.com/download" -ForegroundColor Yellow
    Read-Host "Enter para salir"; exit 1
}
Write-Host ("SDK .NET " + (dotnet --version)) -ForegroundColor DarkGray

# La app abierta bloquea el .exe: hay que cerrarla antes de sobrescribirlo.
$abierta = Get-Process -Name 'BeatTag' -ErrorAction SilentlyContinue
if ($abierta) {
    Write-Host "BeatTag esta abierto y bloquea el ejecutable." -ForegroundColor Yellow
    $cerrar = $CerrarApp
    if (-not $cerrar) {
        try { $cerrar = (Read-Host "Cerrarlo para continuar? (S/N)") -match '^[SsYy]' }
        catch { Write-Host "  (modo no interactivo: usa -CerrarApp)" -ForegroundColor DarkGray; $cerrar = $false }
    }
    if ($cerrar) {
        $abierta | Stop-Process -Force
        Start-Sleep -Milliseconds 800
        Write-Host "  Cerrado." -ForegroundColor Green
    } else {
        Write-Host "Compilacion cancelada (cierra BeatTag y vuelve a intentarlo)." -ForegroundColor Red
        exit 1
    }
}

$crono = [Diagnostics.Stopwatch]::StartNew()

# --- Tests ---
if (-not $SinTests) {
    Write-Host ""
    Write-Host "[1/2] Pasando los tests..." -ForegroundColor Cyan
    dotnet test $tests -c Release --verbosity quiet --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "HAY TESTS QUE FALLAN. No se compila." -ForegroundColor Red
        Write-Host "Usa -SinTests si quieres compilar de todos modos." -ForegroundColor Yellow
        Read-Host "Enter para salir"; exit 1
    }
    Write-Host "  Tests en verde." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[1/2] Tests omitidos (-SinTests)." -ForegroundColor DarkYellow
}

# --- Publicacion ---
Write-Host ""
Write-Host "[2/2] Compilando el ejecutable..." -ForegroundColor Cyan
if (Test-Path $salida) { Remove-Item "$salida\*" -Recurse -Force -ErrorAction SilentlyContinue }

dotnet publish $proyecto -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $salida --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) {
    Write-Host ""
    Write-Host "ERROR al compilar." -ForegroundColor Red
    Read-Host "Enter para salir"; exit 1
}

# Los paquetes de Skia y HarfBuzz dejan sus simbolos de depuracion (.pdb) junto al ejecutable:
# unos 100 MB que no sirven para nada en una version publicada y que, si se comparte la carpeta,
# se irian detras. El ejecutable es autonomo y no los necesita.
$pdb = Get-ChildItem $salida -Filter *.pdb -File -ErrorAction SilentlyContinue
if ($pdb) {
    $pdbMb = [math]::Round((($pdb | Measure-Object Length -Sum).Sum / 1MB), 1)
    $pdb | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host "  ($($pdb.Count) archivos de simbolos eliminados, $pdbMb MB)" -ForegroundColor DarkGray
}

$crono.Stop()
$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ""
Write-Host "LISTO en $([math]::Round($crono.Elapsed.TotalSeconds,1))s" -ForegroundColor Green
Write-Host "  $exe  ($mb MB)" -ForegroundColor Green
Write-Host ""

if ($Ejecutar) {
    Write-Host "Abriendo BeatTag..." -ForegroundColor Cyan
    Start-Process $exe
}
elseif ([Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
    # Solo si hay alguien delante: lanzado desde un script o una tarea automatica, esperar una
    # tecla hacia fallar la ejecucion entera pese a haber compilado correctamente.
    Read-Host "Enter para salir"
}
