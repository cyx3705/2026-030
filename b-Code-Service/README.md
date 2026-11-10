# Ratio 自有本机推理

RATIO-NATIVE-002（0.12.0）：本机推理仅通过定制 Windows DLL。旧 WSL 服务、代理、Linux 构建与启停实现已退役删除。模型、既有快照与远端供应商配置保留。

源码在本目录；运行区为 `RATIO_LOCAL_ROOT`（默认 F 盘 ModuleServicesData/Ratio），DLL 在 `native/bin`、快照在 `native/slots`；模型根由 `RATIO_MODEL_ROOT` 指定。不要编辑运行区中的部署副本。

## Windows 原生 DLL（RATIO-NATIVE-001）

先安装 MSVC x64、Windows SDK、CMake 与 CUDA Toolkit 12.8+。构建复用 WSL 版同一个上游提交和 SHA256、原封不动的 `echo-kv.h` 与 `patch_server.py`，再追加 `patch_native.py` 的 C ABI；不重写批量选义、KV 续接和推理队列。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File b-Code-Service/scripts/echo-kv/build-native.ps1
# 上一命令输出 DLL 目录；下面只生成候选，不进行部署：
dotnet build b-Code/Ratio/Ratio.csproj -c Release -p:RatioNativeRoot=<DLL目录> -p:RequireNativeRuntime=true
```

DLL 随 Ratio.dll 嵌入并在首次启动时解出到运行区 `native/bin`，运行机无需安装编译工具。`-SourceArchive` 可使用已有的、校验匹配的上游源码包。`-BuildRoot` 应为新的临时构建目录；脚本不删除既有源码目录。`-CpuOnly` 只用于编译验证，不代表通过 CUDA 验收。

模块直接调用 C ABI；现有 JSON/SSE 处理器在同进程内继续向原推理队列提交任务。C# 的 `HttpRequestMessage` / `HttpResponseMessage` 在这里仅为内部请求和响应对象，不产生网络请求。远端供应商保持原 HTTP 传输。原生模式不提供外部 OpenAI HTTP 监听；外部消费者通过宿主 `ratio.*` 接入。

`ratio.local.start/status/stop/use` 管理本进程模型；自动加载、单模型限制、换模失败回退、请求取消与 DLL 安全卸载由 Ratio 负责。其他本地模型服务仍在运行时，原生启动明确拒绝同时加载第二份生产模型。Linux 模型快照保留，Windows 原生快照使用独立命名空间。

真实 DLL 验收（显式选择独立测试模型，测试会加载该模型；不指向正在使用的生产模型）：

```powershell
$env:RATIO_NATIVE_LIVE_DLL = '<完整 DLL 路径>'
$env:RATIO_NATIVE_LIVE_MODEL = '<GGUF 路径>'
$env:RATIO_NATIVE_LIVE_DEVICE = 'CUDA0' # CPU 验收用 none
$env:RATIO_NATIVE_ONLY = '1'
dotnet run --project b-Code/Ratio.Tests/Ratio.Tests.csproj -c Release -p:NuGetAudit=false
```

验收覆盖真实批处理与逐个标签一致、append、流式生成、零租约、双槽快照和 DLL 内重启；CUDA 模式另验管理层启动/换模/停止/重载。离线验证时清除上述环境变量。大型原生依赖按文件流式解出，不通过源文件清单构造整套 CUDA 库的内存副本。快照身份包含整套 DLL 和模型 blob 摘要，依赖或模型更换后不会复用旧状态。


固定 32 项选义基准：设置 `RATIO_NATIVE_BENCH=dll` 与 DLL/GGUF 环境变量后运行 commands.test。1 次预热、5 次计时，仅测推理链路，不包含网页 DOM。历史同模型对照保存在现行约定。
