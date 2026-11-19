<#
.SYNOPSIS
    Remove o CaboCam do Windows.
.DESCRIPTION
    Cancela o registro da câmera virtual (regsvr32 /u), remove C:\Program Files\CaboCam\
    e o atalho do Menu Iniciar. Requer administrador (eleva automaticamente).
#>
[CmdletBinding()]
param(
    [switch]$RemoveSettings,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$InstallDir   = Join-Path $env:ProgramFiles 'CaboCam'
$VcamDll      = Join-Path $InstallDir 'CaboCam.VirtualCamera.dll'
$ShortcutPath = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\CaboCam.lnk'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "    AVISO: $msg" -ForegroundColor Yellow }
function Wait-Exit {
    if (-not $NoPause) { Write-Host ''; Read-Host 'Pressione Enter para fechar' | Out-Null }
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Solicitando permissão de administrador...' -ForegroundColor Yellow
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($RemoveSettings) { $argList += '-RemoveSettings' }
    if ($NoPause) { $argList += '-NoPause' }
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs
    } catch {
        Write-Host 'Desinstalação cancelada: permissão de administrador negada.' -ForegroundColor Red
    }
    exit
}

try {
    Write-Host ''
    Write-Host 'CaboCam - desinstalação' -ForegroundColor White
    Write-Host ''

    Write-Step 'Encerrando o CaboCam'
    Get-Process -Name 'CaboCam' -ErrorAction SilentlyContinue | ForEach-Object {
        $_.CloseMainWindow() | Out-Null
        if (-not $_.WaitForExit(3000)) { $_.Kill() }
    }
    Write-Ok 'OK'

    Write-Step 'Cancelando o registro da câmera virtual'
    if (Test-Path $VcamDll) {
        $p = Start-Process -FilePath 'regsvr32.exe' -ArgumentList '/s', '/u', "`"$VcamDll`"" -Wait -PassThru
        if ($p.ExitCode -ne 0) { Write-Warn2 "regsvr32 /u retornou $($p.ExitCode)" } else { Write-Ok 'Registro removido' }
        $fs = Get-Service -Name 'FrameServer' -ErrorAction SilentlyContinue
        if ($fs -and $fs.Status -eq 'Running') {
            try { Stop-Service -Name 'FrameServer' -Force -ErrorAction Stop } catch { Write-Warn2 'Não foi possível parar o FrameServer (a DLL pode continuar em uso até reiniciar).' }
        }
    } else {
        Write-Ok 'DLL não encontrada; nada a cancelar'
    }

    Write-Step 'Removendo o atalho do Menu Iniciar'
    if (Test-Path $ShortcutPath) { Remove-Item $ShortcutPath -Force; Write-Ok 'Atalho removido' } else { Write-Ok 'Atalho não encontrado' }

    Write-Step "Removendo $InstallDir"
    if (Test-Path $InstallDir) {
        $runningFromInstall = $PSScriptRoot -and ((Resolve-Path $PSScriptRoot).Path.TrimEnd('\') -ieq $InstallDir.TrimEnd('\'))
        if ($runningFromInstall) {
            # O script está dentro da pasta: remover depois que ele terminar.
            Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', "timeout /t 3 /nobreak >nul & rmdir /s /q `"$InstallDir`"" -WindowStyle Hidden
            Write-Ok 'A pasta será removida em instantes'
        } else {
            try { Remove-Item $InstallDir -Recurse -Force -ErrorAction Stop; Write-Ok 'Pasta removida' }
            catch { Write-Warn2 "Alguns arquivos estão em uso ($($_.Exception.Message)). Reinicie o computador e remova a pasta manualmente." }
        }
    } else {
        Write-Ok 'Pasta não encontrada'
    }

    if ($RemoveSettings) {
        Write-Step 'Removendo configurações e logs do usuário atual'
        foreach ($d in @((Join-Path $env:APPDATA 'CaboCam'), (Join-Path $env:LOCALAPPDATA 'CaboCam'))) {
            if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
        }
        Write-Ok 'OK'
    }

    Write-Host ''
    Write-Host 'CaboCam removido.' -ForegroundColor Green
    Wait-Exit
}
catch {
    Write-Host ''
    Write-Host "ERRO: $($_.Exception.Message)" -ForegroundColor Red
    Wait-Exit
    exit 1
}
