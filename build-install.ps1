param(
    [string]$NtfyTopic = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Refresh-Path {
    $machine = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $user = [Environment]::GetEnvironmentVariable("Path", "User")
    $env:Path = "$machine;$user"
}

function Is-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ($env:OS -ne "Windows_NT") {
    throw "Este instalador deve ser executado no Windows."
}

$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectDir

if (-not (Is-Admin)) {
    Write-Host "Reabrindo PowerShell como Administrador..." -ForegroundColor Yellow

    $argList = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$($MyInvocation.MyCommand.Path)`""
    )

    if ($NtfyTopic) {
        $argList += @("-NtfyTopic", "`"$NtfyTopic`"")
    }

    Start-Process powershell.exe `
        -Verb RunAs `
        -ArgumentList ($argList -join " ")

    exit
}

Write-Step "Verificando WinGet"
if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
    throw "WinGet não encontrado. Instale/atualize 'App Installer' pela Microsoft Store."
}

Write-Step "Verificando .NET 8 SDK"
$dotnetOk = $false

if (Get-Command dotnet.exe -ErrorAction SilentlyContinue) {
    $dotnetOk = (dotnet --list-sdks) -match '^8\.'
}

if (-not $dotnetOk) {
    winget install `
        --id Microsoft.DotNet.SDK.8 `
        --exact `
        --source winget `
        --accept-source-agreements `
        --accept-package-agreements `
        --silent

    Refresh-Path
}

if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
    throw ".NET SDK foi instalado, mas dotnet ainda não está no PATH. Reabra o PowerShell e rode novamente."
}

Write-Step "Verificando Windows App Development CLI"
if (-not (Get-Command winapp.exe -ErrorAction SilentlyContinue)) {
    winget install `
        --id Microsoft.WinAppCLI `
        --exact `
        --source winget `
        --accept-source-agreements `
        --accept-package-agreements

    Refresh-Path
}

if (-not (Get-Command winapp.exe -ErrorAction SilentlyContinue)) {
    throw "WinApp CLI foi instalado, mas ainda não está no PATH. Reabra o PowerShell e rode novamente."
}

Write-Step "Configurando NTFY_TOPIC"

$currentTopic =
    [Environment]::GetEnvironmentVariable("NTFY_TOPIC", "User")

if (-not $NtfyTopic) {
    $NtfyTopic = $currentTopic
}

if (-not $NtfyTopic) {
    $NtfyTopic = Read-Host "Digite seu tópico ntfy (somente o nome, sem https://ntfy.sh/)"
}

if (-not $NtfyTopic) {
    throw "NTFY_TOPIC não pode ficar vazio."
}

[Environment]::SetEnvironmentVariable(
    "NTFY_TOPIC",
    $NtfyTopic.Trim(),
    "User")

$env:NTFY_TOPIC = $NtfyTopic.Trim()

Write-Host "NTFY_TOPIC configurado." -ForegroundColor Green

Write-Step "Atualizando versão do pacote"

$manifestPath = Join-Path $ProjectDir "Package.appxmanifest"
[xml]$manifest = Get-Content $manifestPath

$epoch = Get-Date "2025-01-01"
$now = Get-Date
$build = [int](($now.Date - $epoch).TotalDays)
$revision = (($now.Hour * 60 + $now.Minute) * 40) + [int]($now.Second / 2)

if ($build -lt 0 -or $build -gt 65535) { $build = 1 }
if ($revision -gt 65535) { $revision = 65535 }

$newVersion = "1.0.$build.$revision"

$ns = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$ns.AddNamespace(
    "x",
    "http://schemas.microsoft.com/appx/manifest/foundation/windows10")

$identity = $manifest.SelectSingleNode("//x:Identity", $ns)
$identity.SetAttribute("Version", $newVersion)

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)

$writer = [System.Xml.XmlWriter]::Create($manifestPath, $settings)
$manifest.Save($writer)
$writer.Close()

Write-Host "Versão: $newVersion"

Write-Step "Compilando watcher x64 self-contained"

dotnet restore "$ProjectDir\ChromeNotificationWatcher.csproj"

dotnet publish `
    "$ProjectDir\ChromeNotificationWatcher.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false

$publishDir =
    Join-Path $ProjectDir `
    "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"

if (-not (Test-Path $publishDir)) {
    throw "Pasta de publish não encontrada: $publishDir"
}

Write-Step "Preparando certificado local"

$certPath = Join-Path $ProjectDir "devcert.pfx"

if (-not (Test-Path $certPath)) {
    winapp cert generate `
        --manifest "$manifestPath" `
        --output "$certPath" `
        --install
}
else {
    try {
        winapp cert install "$certPath"
    }
    catch {
        Write-Host "Certificado antigo inválido; regenerando..." -ForegroundColor Yellow
        Remove-Item $certPath -Force

        winapp cert generate `
            --manifest "$manifestPath" `
            --output "$certPath" `
            --install
    }
}

Write-Step "Gerando e assinando MSIX"

Get-ChildItem "$ProjectDir\*.msix" -ErrorAction SilentlyContinue |
    Remove-Item -Force

winapp pack `
    "$publishDir" `
    --manifest "$manifestPath" `
    --cert "$certPath"

$msix =
    Get-ChildItem "$ProjectDir\*.msix" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $msix) {
    throw "WinApp CLI não gerou um arquivo .msix."
}

Write-Host "MSIX: $($msix.FullName)" -ForegroundColor Green

Write-Step "Instalando/atualizando aplicativo"

Add-AppxPackage `
    -Path $msix.FullName `
    -ForceUpdateFromAnyVersion

Start-Sleep -Seconds 1

$package =
    Get-AppxPackage -Name "Fernando.ChromeNotificationWatcher" |
    Sort-Object Version -Descending |
    Select-Object -First 1

if (-not $package) {
    throw "Pacote instalado, mas não encontrado por Get-AppxPackage."
}

Write-Host "Instalado: $($package.Name) $($package.Version)" -ForegroundColor Green

Write-Step "Abrindo Chrome Notification Watcher"

$aumid = "$($package.PackageFamilyName)!App"
Start-Process explorer.exe "shell:AppsFolder\$aumid"

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "PRONTO." -ForegroundColor Green
Write-Host ""
Write-Host "1. O app vai pedir acesso às notificações do Windows."
Write-Host "2. Clique em Permitir."
Write-Host "3. Clique em 'Testar alarme + ntfy'."
Write-Host "4. Depois gere uma notificação real no Chrome."
Write-Host ""
Write-Host "Ao fechar, o watcher continua na bandeja."
Write-Host "============================================================" -ForegroundColor Green
