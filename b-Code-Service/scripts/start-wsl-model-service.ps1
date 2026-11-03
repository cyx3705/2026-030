$ErrorActionPreference = 'Stop'
# Resolve this script's own location (<drive>:\ModuleServicesData\Ratio\scripts) to its WSL path.
$full = (Resolve-Path (Join-Path $PSScriptRoot 'wsl-model-service.sh')).Path
$script = '/mnt/' + $full.Substring(0, 1).ToLowerInvariant() + $full.Substring(2).Replace('\', '/')

# The unified service owns 18769. The Windows app uses the same F:\Models\Ollama
# store through a junction, so it must be stopped before the WSL process binds the port.
Get-Service -Name 'Ollama' -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Status -eq 'Running') { Stop-Service -Name 'Ollama' -Force }
    if ($_.StartType -eq 'Automatic') { Set-Service -Name 'Ollama' -StartupType Manual }
}
Get-CimInstance Win32_Process -Filter "Name = 'ollama.exe'" | ForEach-Object {
    if ($_.ExecutablePath -like '*\Programs\Ollama\ollama.exe') {
        Stop-Process -Id $_.ProcessId -Force
    }
}
Start-Sleep -Seconds 1
wsl -d Ubuntu -e bash $script start
if ($LASTEXITCODE -ne 0) { throw "WSL model service failed to start ($LASTEXITCODE)" }
