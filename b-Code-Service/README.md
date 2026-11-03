# 本机推理服务（Ratio 自带）

WSL 里一个 llama-server（127.0.0.1:18769，带 Echo 共享 KV 扩展）前面挂 C++ 代理 echo-proxy（127.0.0.1:11435，OpenAI 兼容）。
同一时间只装一个模型，Echo、Codex 和 Ratio 共用。2026-11-03 起归 Ratio 所有，不再有单独的推理区（原 `F:\LinuxModuleServices` 已撤销）。

## 三处位置

| 什么 | 在哪 | 说明 |
| --- | --- | --- |
| 源码（本目录） | `b-Code-Service/` | 唯一真源：`scripts/`、`config/`、`scripts/echo-kv/` C++ 扩展源码 |
| 运行区 | `F:\ModuleServicesData\Ratio`（WSL `/mnt/f/ModuleServicesData/Ratio`） | 部署后的脚本与配置，加上可重建的：`linux-cache/echo-kv-build`（编译产物）、`linux-ollama/`（CUDA/ggml 运行库）、`prefix-cache/`（KV 快照，40 GB 上限）、`status/`、`linux-logs/`、`linux-tmp/` |
| 模型 | `F:\Models` | 运行区的上两级目录下的 `Models/`（`Ollama/` 清单与 blob、`GGUF/`） |

运行区是 F 盘特例（用户定），不在宿主 `ModuleData` 下：KV 快照太大，且原生启动 Ubuntu 时也要能从
`/media/cyx3705/系统/ModuleServicesData/Ratio` 跑同一份脚本。脚本都按自身位置找根目录（`scripts/service_root.py`、`scripts/lib/service-root.sh`），不写死盘符。

## 部署

`ratio.local.start` 在服务没在跑时先把本目录同步到运行区（内容相同的文件不动，只覆盖变了的，从不删运行区的数据），再执行
`scripts/start-wsl-model-service.ps1`。本目录的文件以嵌入资源随 Ratio.dll 发布，所以运行区的脚本永远来自已发布的 Ratio 版本；
**不要直接改运行区的脚本**，下次启动会被覆盖。

编译产物不随包走：`bash scripts/echo-kv/build.sh`（在运行区执行）按固定上游提交下载、校验、打补丁、构建，库用相对软链接接到 `linux-ollama/`。

## 验证

```bash
cd /mnt/f/ModuleServicesData/Ratio/scripts
python3 -m unittest test_model_switch test_kv_disk_cache test_local_ai_cache_proxy
python3 echo-kv/test_proxy.py
```

`服务历史.md` 是并入前的完整记录（共享 KV、换模、C++ 代理的取舍与实测），路径已改成新位置；其中的实测数字是当时的单次样例。
