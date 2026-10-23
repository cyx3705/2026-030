# HistoryApollo

> 模型调用模块：把 OpenAI 兼容的对话补全接成宿主指令

## 定位

HistoryApollo 把 OpenAI 兼容的对话补全接口变成 HistoryVulcan 指令，首发内置 DeepSeek，可按需联网搜索（博查），
可附本机图片交给识图模型（内置通义千问），每一轮往返实时写进控制台。

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
| `chat` | `apollo.chat.ask` / `send` | 单轮提问 / 完整消息数组；`web=true` 时模型自行决定是否联网；`images=` 附图走识图供应商 |
| `model` | `apollo.model.list` | 模型清单 |
| `provider` | `apollo.provider.list` / `use` / `config` | 供应商、默认模型与接入点 |
| `key` | `apollo.key.set` / `clear` | 密钥读写（回执只出掩码） |

```
apollo.key.set token=<密钥> provider=deepseek
apollo.chat.ask prompt=用一句话解释张量
apollo.chat.ask prompt="CDQ2B20-10D 是哪个品牌的气缸" web=true
apollo.key.set token=<密钥> provider=qwen
apollo.chat.ask prompt=这张图纸是什么零件 images=D:\图纸.png
```

参数读注册自描述：`diana.docs.read domain=apollo`（宿主 6.1.0 起没有消费文档）；返回载荷、密钥与取舍见 [现行约定](./b-Office/现行约定.md)。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [现行约定](./b-Office/现行约定.md) | 唯一的长期文档：真相在哪、取舍、真机才知道的、工程约定 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code/HistoryApollo/` | 模块源码、manifest 与 `eng/` 构建脚本 |
| `b-Code/HistoryApollo.Tests/` | 自动验证 |
| `b-Code/` | 项目合同检查 |
| `b-Office/` | `current/现行约定.md`，唯一的长期文档 |
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
  DeepSeek 为 `APOLLO_DEEPSEEK_KEY` / `DEEPSEEK_API_KEY`，博查为 `APOLLO_BOCHA_KEY` / `BOCHA_API_KEY`，通义千问为 `APOLLO_QWEN_KEY` / `QWEN_API_KEY`。
- **密钥不入库。** 项目合同检查会扫描全仓，拒绝任何形如 `sk-<长串>` 的真实密钥。
- 嵌套调用的过程输出也逐轮进控制台；消费方不要再从载荷复述调用过程。

## 保留内容

### logo与作者

- 作者：pinavia
- ![logo](./Logo.png)

### 学习许可与权利保留

- 本项目允许个人、学校和研究者进行非商业性的学习、研究和引用。
- 未经著作权人书面许可，不得将项目内容用于商业开发、产品制造、商业服务、商业宣传、投标交付、毕业设计成品交付或再授权。
- 不得删除作者、来源、版本历史和第三方许可证声明，不得将本项目或其衍生成果宣称为与作者无关的原创项目。
- 软件源码适用 [PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/)；机械模型、PCB、原理图、图片、文档、Unity 工程和其他非软件资料适用 [CC BY-NC-ND 4.0](https://creativecommons.org/licenses/by-nc-nd/4.0/)。
- SE、SW、STEP/STP 及其他三维模型仅允许个人或学校进行非商业学习、评审和研究；未经著作权人书面许可，不得用于加工、打样、制造、销售、量产、投标、产品交付、商业宣传、改作或再授权。
- 第三方组件、模板、元件库、外购件模型和工具仍受其原始许可证约束；本声明不扩大作者对第三方内容的权利。

商业使用、产品化、公司内部长期使用、转让或排他性授权需另行取得著作权人的书面许可。
