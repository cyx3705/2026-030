# HistoryApollo 代码组件

| 目录或文件 | 内容 |
| --- | --- |
| `HistoryApollo/` | 模块本体（权威源）。产物 `HistoryApollo.dll` 由宿主装载。 |
| `HistoryApollo/eng/` | 发布候选打包脚本。 |
| `HistoryApollo.Tests/` | 自动验证。可执行文件，全部 PASS 时退出码 0。 |
| `Test-ProjectContract.ps1` | 只读项目合同检查：版本三处一致、模块身份、目录级别、失效链接、密钥未入库。 |

`bin/`、`obj/` 是可重建生成物，不入库；发布候选写到根目录 `z-Publish/`。

模块只依赖 HistoryVulcan 5.1.2 的冻结接入面（`IModuleContext.Bus` 与 `RegisterCommands`），
引用宿主发布快照且 `Private=false`——部署时由宿主提供，包里不带副本。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
```
