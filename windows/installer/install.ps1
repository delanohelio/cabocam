<#
.SYNOPSIS
    Instala o CaboCam (iPhone como webcam) no Windows 11.
.DESCRIPTION
    - Copia os arquivos para C:\Program Files\CaboCam\
    - Registra a câmera virtual (CaboCam.VirtualCamera.dll) com regsvr32
    - Verifica o Windows 11 e o Serviço de Dispositivos Móveis da Apple
    - Cria o atalho no Menu Iniciar
    Requer administrador (eleva automaticamente).
#>
[CmdletBinding()]
param(
    [string]$SourceDir = $PSScriptRoot,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$InstallDir   = Join-Path $env:ProgramFiles 'CaboCam'
$VcamDll      = Join-Path $InstallDir 'CaboCam.VirtualCamera.dll'
$ExePath      = Join-Path $InstallDir 'CaboCam.exe'
$ShortcutPath = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\CaboCam.lnk'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "    AVISO: $msg" -ForegroundColor Yellow }

function Wait-Exit {
    if (-not $NoPause) { Write-Host ''; Read-Host 'Pressione Enter para fechar' | Out-Null }
}

# ---- Elevação ---------------------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Solicitando permissão de administrador...' -ForegroundColor Yellow
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-SourceDir', "`"$SourceDir`"")
    if ($NoPause) { $argList += '-NoPause' }
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs
    } catch {
        Write-Host 'Instalação cancelada: permissão de administrador negada.' -ForegroundColor Red
    }
    exit
}

try {
    Write-Host ''
    Write-Host 'CaboCam - instalação' -ForegroundColor White
    Write-Host ''

    # ---- Versão do Windows --------------------------------------------------------
    Write-Step 'Verificando a versão do Windows'
    $build = [int](Get-CimInstance Win32_OperatingSystem).BuildNumber
    if ($build -lt 22000) {
        Write-Warn2 "Windows build $build detectado. A câmera virtual requer o Windows 11 (build 22000 ou superior). A instalação continuará, mas a câmera 'CaboCam' não funcionará."
    } else {
        Write-Ok "Windows build $build (OK)"
    }

    # ---- Arquivos de origem ---------------------------------------------------------
    if (-not (Test-Path (Join-Path $SourceDir 'CaboCam.exe'))) {
        throw "CaboCam.exe não encontrado em '$SourceDir'. Execute o install.ps1 de dentro da pasta extraída do CaboCam."
    }
    if (-not (Test-Path (Join-Path $SourceDir 'CaboCam.VirtualCamera.dll'))) {
        throw "CaboCam.VirtualCamera.dll não encontrado em '$SourceDir'."
    }

    # ---- Encerrar instância em execução e liberar a DLL ------------------------------
    Write-Step 'Preparando a instalação'
    Get-Process -Name 'CaboCam' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Ok 'Encerrando o CaboCam em execução...'
        $_.CloseMainWindow() | Out-Null
        if (-not $_.WaitForExit(3000)) { $_.Kill() }
    }
    if (Test-Path $VcamDll) {
        # Versão anterior: remover o registro e liberar a DLL do Windows Camera Frame Server.
        Start-Process -FilePath 'regsvr32.exe' -ArgumentList '/s', '/u', "`"$VcamDll`"" -Wait | Out-Null
        $fs = Get-Service -Name 'FrameServer' -ErrorAction SilentlyContinue
        if ($fs -and $fs.Status -eq 'Running') {
            try { Stop-Service -Name 'FrameServer' -Force -ErrorAction Stop; Write-Ok 'Windows Camera Frame Server reiniciado' }
            catch { Write-Warn2 "Não foi possível parar o FrameServer: $($_.Exception.Message)" }
        }
    }

    # ---- Copiar arquivos -----------------------------------------------------------
    Write-Step "Copiando arquivos para $InstallDir"
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    $srcFull = (Resolve-Path $SourceDir).Path.TrimEnd('\')
    if ($srcFull -ine $InstallDir.TrimEnd('\')) {
        Copy-Item -Path (Join-Path $srcFull '*') -Destination $InstallDir -Recurse -Force
        Write-Ok 'Arquivos copiados'
    } else {
        Write-Ok 'Já está na pasta de instalação'
    }

    # O Frame Server roda como LocalService: garantir leitura/execução para "Usuários" e serviços locais.
    try {
        & icacls.exe $InstallDir /grant '*S-1-5-32-545:(OI)(CI)RX' '*S-1-5-19:(OI)(CI)RX' /T /C /Q | Out-Null
    } catch {
        Write-Warn2 "Não foi possível ajustar permissões da pasta: $($_.Exception.Message)"
    }

    # ---- Registrar a câmera virtual ------------------------------------------------
    Write-Step 'Registrando a câmera virtual (regsvr32)'
    $reg = Start-Process -FilePath 'regsvr32.exe' -ArgumentList '/s', "`"$VcamDll`"" -Wait -PassThru
    if ($reg.ExitCode -ne 0) {
        throw "regsvr32 falhou (código $($reg.ExitCode)). Verifique se o Microsoft Visual C++ Redistributable x64 está instalado."
    }
    Write-Ok 'CaboCam.VirtualCamera.dll registrada'

    # ---- Atalho no Menu Iniciar -----------------------------------------------------
    Write-Step 'Criando atalho no Menu Iniciar'
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($ShortcutPath)
    $lnk.TargetPath       = $ExePath
    $lnk.WorkingDirectory = $InstallDir
    $lnk.Description      = 'CaboCam - iPhone como webcam'
    $lnk.IconLocation     = "$ExePath,0"
    $lnk.Save()
    Write-Ok $ShortcutPath

    # ---- Serviço de Dispositivos Móveis da Apple ---------------------------------------
    Write-Step 'Verificando o Serviço de Dispositivos Móveis da Apple (usbmuxd)'
    $appleNames = @('Apple Mobile Device Service', 'AppleMobileDeviceService')
    $appleSvc = $null
    foreach ($n in $appleNames) {
        $appleSvc = Get-Service -Name $n -ErrorAction SilentlyContinue
        if (-not $appleSvc) { $appleSvc = Get-Service -DisplayName $n -ErrorAction SilentlyContinue }
        if ($appleSvc) { break }
    }
    if ($appleSvc) {
        Write-Ok "Serviço encontrado: $($appleSvc.DisplayName) ($($appleSvc.Status))"
        if ($appleSvc.Status -ne 'Running') {
            try { Start-Service -InputObject $appleSvc; Write-Ok 'Serviço iniciado' }
            catch { Write-Warn2 "Não foi possível iniciar o serviço: $($_.Exception.Message)" }
        }
    } else {
        Write-Warn2 'O Serviço de Dispositivos Móveis da Apple não foi encontrado.'
        Write-Host "    Instale o app 'Dispositivos Apple' (Apple Devices) pela Microsoft Store" -ForegroundColor Yellow
        Write-Host '    (https://apps.microsoft.com/detail/9np83lwlpz9k) ou o iTunes, e reconecte o iPhone.' -ForegroundColor Yellow
        Write-Host '    Sem ele o CaboCam não consegue se comunicar com o iPhone pelo cabo USB.' -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host 'Instalação concluída.' -ForegroundColor Green
    Write-Host "Abra o CaboCam pelo Menu Iniciar. No iPhone, instale e abra o app CaboCam e toque em 'Confiar' ao conectar o cabo." -ForegroundColor White
    Wait-Exit
}
catch {
    Write-Host ''
    Write-Host "ERRO: $($_.Exception.Message)" -ForegroundColor Red
    Wait-Exit
    exit 1
}
