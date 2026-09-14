# HistoryApollo 模型调用

HistoryApollo 是注册到 [HistoryVulcan](../2026-023-HistoryVulcan) 的模型调用模块：
把 OpenAI 兼容的对话补全接口变成宿主指令，首发内置 DeepSeek，可按需联网搜索（博查）。

![OneHistory Logo](./Logo.png)

它只做一件事——**把一次模型调用说清楚**。会话历史、提示词工程、重试编排和流式输出
都属于调用方，不在这里。

## 快速使用

```
apollo.key.set token=<密钥> provider=deepseek
apollo.chat.ask prompt=用一句话解释张量
apollo.key.set token=<博查密钥> provider=bocha
apollo.chat.ask prompt="CDQ2B20-10D 是哪个品牌的气缸" web=true
apollo.provider.list
```

指令、参数、返回载荷与密钥约定见 [`b-Office/package/模块API.md`](./b-Office/package/模块API.md)。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 读取顺序、真值规则、工作边界与完成要求 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、命令与上下文排除项 |
| [`b-Office/package/模块API.md`](./b-Office/package/模块API.md) | 模块消费合同（随发布包同行） |
| [`b-Office/current/项目概览.md`](./b-Office/current/项目概览.md) | 目标、范围、状态和交付物 |
| [`b-Office/current/技术合同.md`](./b-Office/current/技术合同.md) | 现行需求和系统架构 |
| [`b-Office/current/有效决策.md`](./b-Office/current/有效决策.md) | 当前仍然有效的关键决策 |
| [`b-Office/current/验证合同.md`](./b-Office/current/验证合同.md) | 验证层级、命令和证据 |
| [`b-Office/文档中心.md`](./b-Office/文档中心.md) | 文档索引及根目录 a/b/z 规范 |

## 常用命令

```powershell
dotnet build .\b-Code\HistoryApollo\HistoryApollo.csproj -c Release
dotnet run --project .\b-Code\HistoryApollo.Tests\HistoryApollo.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\HistoryApollo\eng\Build-HistoryApolloPackage.ps1
```

自动验证全程离线：对话相关用例走假传输，不出网、不计费。需要联网实调时设
`APOLLO_LIVE=1` 再跑测试，它会用本机真实配置发一次最小请求。

## 密钥

密钥只落在本机 `%AppData%\HistoryVulcan\Modules\HistoryApollo\data\providers.json`，
或由环境变量 `APOLLO_DEEPSEEK_KEY` / `DEEPSEEK_API_KEY` 提供（环境变量优先）；博查是 `APOLLO_BOCHA_KEY` / `BOCHA_API_KEY`。

**密钥不入库。** 项目合同检查会扫描全仓并拒绝任何形如 `sk-<长串>` 的真实密钥。

作者：Pinavia
