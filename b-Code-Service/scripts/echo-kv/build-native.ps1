param(
    [string]$BuildRoot = (Join-Path ([System.IO.Path]::GetTempPath()) 'Ratio-native-build'),
    [string]$OutputDirectory = '',
    [string]$SourceArchive = '',
    [string]$CudaArchitectures = 'native',
    [switch]$CpuOnly
)
$ErrorActionPreference = 'Stop'
# RATIO-NATIVE-001. Build from the same pinned source and unchanged Echo patch.
# No installation, activation, model start, WSL shutdown or repository writes.
$revision = '9ff0d7e90f0faaa7c16017e6dcf9cf91c50a100e'
$digest = 'd8552e1b309c1571e49baf7a53d429b9709fa0c1d6907c446f6cecb87087d07b'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio C++ Build Tools (MSVC x64, Windows SDK and CMake).' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'MSVC x64 tools are missing. Install the Visual Studio C++ workload before building Ratio native DLL.' }
$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
$cmake = if ($cmakeCommand) { $cmakeCommand.Source } else { Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe' }
if (!(Test-Path -LiteralPath $cmake)) { throw 'CMake is missing; install the Visual Studio C++ CMake tools.' }
if (!$CpuOnly) {
    # A toolkit installed during this shell session is not yet in its inherited environment.
    foreach ($entry in [Environment]::GetEnvironmentVariables('Machine').GetEnumerator()) {
        if ($entry.Key -like 'CUDA_PATH*' -and ![Environment]::GetEnvironmentVariable($entry.Key, 'Process')) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
    if (!$env:CUDA_PATH) {
        $nvcc = Get-Command nvcc -ErrorAction SilentlyContinue
        if ($nvcc) { $env:CUDA_PATH = Split-Path (Split-Path $nvcc.Source -Parent) -Parent }
    }
    if (!$env:CUDA_PATH -or !(Test-Path -LiteralPath (Join-Path $env:CUDA_PATH 'bin/nvcc.exe'))) {
        throw 'CUDA Toolkit is missing. Install CUDA Toolkit for GPU builds; -CpuOnly is only for explicit CPU verification.'
    }
}
$python = (Get-Command python -ErrorAction Stop).Source
$BuildRoot = [System.IO.Path]::GetFullPath($BuildRoot)
if (!$OutputDirectory) { $OutputDirectory = Join-Path $BuildRoot 'bin' }
New-Item -ItemType Directory -Force -Path $BuildRoot | Out-Null
if (!$SourceArchive) {
    $SourceArchive = Join-Path $BuildRoot 'llama-source.tar.gz'
    if (!(Test-Path -LiteralPath $SourceArchive)) {
        Invoke-WebRequest "https://codeload.github.com/ggml-org/llama.cpp/tar.gz/$revision" -OutFile $SourceArchive
    }
}
if ((Get-FileHash -LiteralPath $SourceArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $digest) {
    throw 'Pinned llama.cpp source SHA256 mismatch.'
}
$source = Join-Path $BuildRoot "llama.cpp-$revision"
# Never repatch a dirty/previous source tree: callers choose a fresh build root.
if (Test-Path -LiteralPath $source) { throw 'Source tree already exists; choose a fresh -BuildRoot. No files were deleted.' }
& tar -xzf $SourceArchive -C $BuildRoot
if ($LASTEXITCODE) { throw 'Source extraction failed.' }
& $python (Join-Path $PSScriptRoot 'patch_server.py') $source
if ($LASTEXITCODE) { throw 'Echo integration patch failed.' }
& $python (Join-Path $PSScriptRoot 'patch_native.py') $source
if ($LASTEXITCODE) { throw 'Native ABI integration patch failed.' }
$cuda = if ($CpuOnly) { 'OFF' } else { 'ON' }
$build = Join-Path $BuildRoot 'build'
& $cmake -S $source -B $build -A x64 -DBUILD_SHARED_LIBS=ON "-DGGML_CUDA=$cuda" "-DCMAKE_CUDA_ARCHITECTURES=$CudaArchitectures" -DLLAMA_OPENSSL=OFF -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_EXAMPLES=OFF -DLLAMA_BUILD_TOOLS=ON -DLLAMA_BUILD_SERVER=ON -DLLAMA_BUILD_UI=OFF -DLLAMA_USE_PREBUILT_UI=OFF
if ($LASTEXITCODE) { throw 'Native CMake configuration failed.' }
& $cmake --build $build --config Release --target llama-server-impl --parallel
if ($LASTEXITCODE) { throw 'Native compilation failed.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Get-ChildItem (Join-Path $build 'bin/Release') -Filter '*.dll' | Copy-Item -Destination $OutputDirectory
$redist = Get-ChildItem (Join-Path $vs 'VC/Redist/MSVC') -Directory |
    Where-Object { $_.Name -match '^14\.' } | Sort-Object Name -Descending | Select-Object -First 1
if (!$redist) { throw 'MSVC redistributable runtime files are missing from the selected toolchain.' }
foreach ($runtime in @('Microsoft.VC143.CRT', 'Microsoft.VC143.OpenMP')) {
    $runtimePath = Join-Path $redist.FullName "x64/$runtime"
    if (!(Test-Path -LiteralPath $runtimePath)) { throw "Required runtime directory missing: $runtime" }
    Get-ChildItem $runtimePath -Filter '*.dll' | Copy-Item -Destination $OutputDirectory
}
if (!$CpuOnly -and $env:CUDA_PATH) {
    Get-ChildItem (Join-Path $env:CUDA_PATH 'bin') -Filter '*.dll' |
        Where-Object { $_.Name -match '^(cudart|cublas|nvrtc)' } | Copy-Item -Destination $OutputDirectory
}
if (!(Test-Path -LiteralPath (Join-Path $OutputDirectory 'ratio-inference.dll'))) { throw 'Build did not produce ratio-inference.dll.' }
Write-Output "Built Ratio native DLL and dependencies: $OutputDirectory"

