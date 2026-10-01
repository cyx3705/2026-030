# HistoryApollo

> 模型调用模块：把 OpenAI 兼容的对话补全接成宿主指令

## 定位

HistoryApollo 把 OpenAI 兼容的对话补全接口变成 HistoryVulcan 指令，首发内置 DeepSeek，可按需联网搜索（博查），
每一轮往返实时写进控制台。

- 它只做一件事——**把一次模型调用说清楚**。
- 会话历史、提示词工程、重试编排和逐字流式输出都属于调用方，不在这里。

## 概况

| 项 | 值 |
| --- | --- |
| 编号 | `2026-030` |
| 角色 | 宿主模块（`kind=module`） |
| 指令域 | `apollo` |
| 界面 | 无（`ui: false`） |
| MCP 投影 | `standard` |
| 版本与宿主下限 | [`HistoryApolloVersion.props`](./b-Code/HistoryApollo/HistoryApolloVersion.props) |

## 能力

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `chat` | `apollo.chat.ask` / `send` | 单轮提问 / 完整消息数组；`web=true` 时模型自行决定是否联网 |
| `model` | `apollo.model.list` | 模型清单 |
| `provider` | `apollo.provider.list` / `use` / `config` | 供应商、默认模型与接入点 |
| `key` | `apollo.key.set` / `clear` | 密钥读写（回执只出掩码） |

```
apollo.key.set token=<密钥> provider=deepseek
apollo.chat.ask prompt=用一句话解释张量
apollo.chat.ask prompt="CDQ2B20-10D 是哪个品牌的气缸" web=true
```

参数读注册自描述：`diana.docs.read domain=apollo`（宿主 6.1.0 起没有消费文档）；返回载荷、密钥与失败语义见 [技术合同](./b-Office/current/技术合同.md)「对外约定」。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [文档中心](./b-Office/文档中心.md) | 文档索引与读取顺序 |
| [项目概览](./b-Office/current/项目概览.md) | 目标、范围与状态 |
| [技术合同](./b-Office/current/技术合同.md) | 现行需求与架构 |
| [有效决策](./b-Office/current/有效决策.md) | 仍然有效的关键决策 |
| [验证合同](./b-Office/current/验证合同.md) | 验证层级、命令与证据 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code/HistoryApollo/` | 模块源码、manifest 与 `eng/` 构建脚本 |
| `b-Code/HistoryApollo.Tests/` | 自动验证 |
| `b-Code/` | 项目合同检查 |
| `b-Office/` | 项目文档：`current/` 现行合同、`history/` 只读归档 |
| `z-Publish/` | 正式快照与 `history/` 归档，由宿主管线写入 |

## 构建与验证

```powershell
dotnet build .\b-Code\HistoryApollo\HistoryApollo.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code\HistoryApollo.Tests\HistoryApollo.Tests.csproj -c Release -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
```

自动验证全程离线：对话用例走假传输，不出网、不计费。需要联网实调时设 `APOLLO_LIVE=1` 再跑测试。

## 开发与发布

改动只进 `vulcan.dev.start` 创建的工作区，经宿主 Console CLI 走
`vulcan.dev.start` → `vulcan.dev.submit`（候选构建并热装送审）→ `vulcan.dev.finish`（批准后并回并写入 `z-Publish`）。
本仓不自行发布；`eng/Build-HistoryApolloPackage.ps1` 只用于本地候选构建。

## 要点

- 密钥只落在本机 `%AppData%\HistoryVulcan\ModuleData\HistoryApollo\providers.json`（宿主给的数据目录），或由环境变量提供（优先）：
  DeepSeek 为 `APOLLO_DEEPSEEK_KEY` / `DEEPSEEK_API_KEY`，博查为 `APOLLO_BOCHA_KEY` / `BOCHA_API_KEY`。
- **密钥不入库。** 项目合同检查会扫描全仓，拒绝任何形如 `sk-<长串>` 的真实密钥。
- 嵌套调用的过程输出也逐轮进控制台；消费方不要再从载荷复述调用过程。

## 保留内容
- 本模板项目介绍：此为最初的准备的项目模板
    每个分支项目都会由他去继承
- 作者：Pinavia - 2025

![logo](./Logo.png)
