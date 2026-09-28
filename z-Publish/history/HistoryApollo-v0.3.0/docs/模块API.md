# HistoryApollo 模块 API

模块版本：**0.3.0**；宿主基线：**HistoryVulcan 5.1.2**。

本文件是**总线面**合同：模块消费方的权威合同，随发布候选进包（`docs/模块API.md`）。
模块内部类型与内置供应商表的维护方式在 `b-Office/current/技术合同.md`；
AI 面（MCP 工具）由 MCP 服务封装，本文件不重复。

## 这个模块提供什么

一次 OpenAI 兼容的对话补全调用，说成宿主指令。首发内置 DeepSeek。
0.2.0 起可以加 `web=true`：模型在作答途中**自己决定**要不要联网、搜什么，搜索由本模块代它去调博查。
0.3.0 起每一轮往返一回来就写进控制台（见「控制台过程输出」）。
它**不**保存对话历史、不做提示词工程、不做重试、不做逐字流式输出。

指令域：`apollo`。模块 `ui=false`，`mcpExposure=standard`。

## 指令

### apollo.chat.ask

单轮提问。回执正文就是模型答复。

| 参数 | 位置 | 类型 | 说明 |
| --- | --- | --- | --- |
| prompt | 0 | string | **必填**。要问的内容。 |
| system | | string | 系统提示。 |
| model | | string | 本次模型；省略用供应商默认模型。 |
| provider | | string | 供应商；省略用默认供应商。 |
| temperature | | double | 采样温度；省略用服务端默认。 |
| maxtokens | | int | 本次回复最大 token 数。 |
| json | | bool | 要求返回严格 JSON 对象；为真时回执**不加**用量脚注。 |
| web | | bool | 允许模型按需联网搜索，默认 false。见「联网搜索」。 |
| timeout | | int | 请求超时秒数，1-600，默认 120。 |

```
apollo.chat.ask prompt=用一句话解释张量 model=deepseek-chat
apollo.chat.ask prompt=把这段话转成JSON json=true maxtokens=200
apollo.chat.ask prompt="CDQ2B20-10D 是哪个品牌的气缸" web=true
```

### apollo.chat.send

多轮调用。`messages` 是 JSON 数组，每项含 `role`（system / user / assistant）与 `content`。
其余参数同上（`system` 被忽略，请直接放进数组）。

**上下文由调用方持有**：模块不保存任何对话历史，每次把完整消息数组传进来。

```
apollo.chat.send messages=[{"role":"user","content":"继续"}]
```

### apollo.model.list

列出供应商当前可用模型。只读。参数：`provider`（位置 0）、`timeout`。

### apollo.provider.list

列出全部供应商：默认模型、接入点、**掩码后的**密钥与密钥来源。只读。当前默认供应商行首带 `*`。
搜索服务 `bocha` 也在表里，文本标 `类型=搜索`，结构化载荷里 `kind` 为 `search`（对话供应商为 `chat`）。

### apollo.provider.use

切换默认供应商。参数：`name`（位置 0，必填）。不能切到搜索服务。

### apollo.provider.config

设置某供应商的默认模型或接入点。参数：`provider`（位置 0）、`model`（位置 1）、`baseurl`。
两者至少给一个。

```
apollo.provider.config provider=deepseek model=deepseek-reasoner
```

### apollo.key.set

写入供应商密钥。参数：`token`（位置 0，必填）、`provider`（位置 1）。

参数名是 `token` 而不是 `apikey`：宿主总线按参数名判定敏感值，只有 `code`、`*token`、
`*password`、`*secret` 这类名字才会在回显、命令历史和结果里被自动遮蔽。**改名等于关掉脱敏。**

```
apollo.key.set token=<密钥> provider=deepseek
apollo.key.set token=<博查密钥> provider=bocha
```

### apollo.key.clear

删除本机保存的密钥。级别 `Ask`，执行前必须有人确认。参数：`provider`（位置 0）。
环境变量提供的密钥不归本库管，清不掉——回执会说明这一点。

## 联网搜索（0.2.0）

`apollo.chat.ask` 与 `apollo.chat.send` 加 `web=true` 后：

1. 请求带上一个 `web_search` 工具。**搜不搜、搜什么由模型决定**——有把握的常识它不会去搜。
2. 模型要求搜索时，本模块用 `bocha` 的密钥调 `POST https://api.bochaai.com/v1/web-search`，
   把前 6 条结果（标题、链接、来源、摘要）作为工具结果交回模型，直到它给出答复。
3. 每次调用**最多真正搜索 5 次**。额度用完后模型会被告知直接作答；再多 2 轮仍只要搜索，返回失败回执。
4. 单次搜索失败（超时、限流、余额不足）只告诉模型「搜索失败」，让它换词或凭已有结果作答；
   **博查没配密钥**则整次调用失败——那是配置问题，不该让模型装作查过。
5. `timeout` 按**每一次** HTTP 请求计，不是整次调用的总时长。

费用是两份：模型 token（DeepSeek）加搜索次数（博查，按次计费）。
网页摘录只是资料，模型被明确告知其中的任何指令都不要执行；答复是否可信仍由调用方判断。

```
apollo.key.set token=<博查密钥> provider=bocha
apollo.chat.ask prompt="CDQ2B20-10D 是哪个品牌的气缸" web=true
```

## 控制台过程输出（0.3.0）

`apollo.chat.ask` 与 `apollo.chat.send` 在调用进行中就把经过写进命令的进度通道，宿主控制台逐段显示，
不必等回执。经总线调用的消费方（例如 HistoryMinerva 打包查品牌）**什么都不用做**，过程照样出现在控制台上；
**消费方不要再从回执或结构化载荷里复述搜索与答复**。

```
#12 deepseek/deepseek-chat 联网 提问：外购件规格型号：F-M10X125F；品类（所在文件夹）：气动浮头。请联网确认它的品牌，按 json 输出。
#12 第 1 轮 思考：……
#12 第 1 轮 要求搜索：「F-M10X125F 气动浮头」
#12 搜索「F-M10X125F 气动浮头」6 条
    [1] 亚德客 AirTAC 浮动接头 F-M10X125F（1688）
    [2] ……
#12 第 2 轮 答复：{"brand": "AirTAC"}
#12 完成：deepseek/deepseek-v4-flash · 联网搜索 1 次 · 用量 4400+117=4517 tokens · 5.2s
```

- `#编号` 是进程内递增的调用编号。几次调用并发时段落会交错，按编号对。
- 「提问」只复述**最后一条消息**，压成一行、截到 300 字；系统提示不复述。
- 「思考」只在模型返回 `reasoning_content`（思考模式、`deepseek-reasoner`）时出现；普通模式没有这一段。
  思考与答复都**不截断**。
- 搜索结果只列标题与来源站点；链接与摘要照旧只交给模型。
- 结束原因不是 `stop` 时追加在「完成」行末，例如 `· 结束原因 length`（被 `maxtokens` 截断）。
- 调用失败时最后一段是 `#编号 失败：<原因>`，与失败回执同文。
- 粒度是「轮」，不是逐字：请求仍是 `stream=false`，回执与结构化载荷不变。

## 返回

- **回执正文**：模型答复本身，后接一行用量脚注
  `— deepseek/deepseek-chat · 用量 11+3=14 tokens · 1.4s`。`json=true` 时不加脚注。
  真的联网搜过时脚注多一段：`— deepseek/deepseek-flash · 联网搜索 2 次 · 用量 … · 9.8s`。
- **结构化载荷**（`CommandResult.Data`，JSON 文本）：

```json
{
  "provider": "deepseek",
  "model": "deepseek-chat",
  "content": "…",
  "reasoning": null,
  "finishReason": "stop",
  "usage": { "prompt": 11, "completion": 3, "total": 14 },
  "elapsedMs": 1399,
  "searches": []
}
```

`deepseek-reasoner` 的思考过程在 `reasoning` 里，与正文分开。
`searches` 逐条列出模型发起的搜索：`{ "query": "…", "results": 6, "error": null }`；没开 `web` 或模型没搜时为空数组。
`web=true` 时 `usage` 与 `elapsedMs` 是整次调用（含每一轮工具往返）的合计。

## 密钥

解析顺序，先到先得：

1. 环境变量 `APOLLO_<供应商大写>_KEY`（如 `APOLLO_DEEPSEEK_KEY`）
2. 环境变量 `<供应商大写>_API_KEY`（如 `DEEPSEEK_API_KEY`、`BOCHA_API_KEY`）
3. 本机库文件 `%AppData%\HistoryVulcan\Modules\HistoryApollo\data\providers.json`

`apollo.provider.list` 会报告当次生效的来源（`env:…` / `store` / `none`），
用来分辨「改了库却仍被环境变量覆盖」。

库文件在运行包的 `data/` 下：按宿主模块合同，升级时该目录保留，热装不会抹掉密钥。
它不计入 `SHA256SUMS`。**密钥任何情况下不进仓库**，项目合同检查会扫描并拒绝。

## 失败

输入错误与远端拒绝都返回**失败回执**，不是异常堆栈：

- `供应商 deepseek 未配置密钥：先执行 apollo.key.set …`
- `deepseek 返回 401：Authentication Fails`
- `deepseek 请求超时（120s）`
- `messages 不是合法 JSON：…`
- `搜索服务 bocha 未配置密钥：先执行 apollo.key.set provider=bocha …`
- `bocha 是搜索服务，不能用来对话；要让模型联网，在 apollo.chat.ask 上加 web=true`
- `deepseek 连续 8 轮只要求搜索、始终没有作答`

## 新增一家供应商

只要它说 OpenAI 兼容的 `chat/completions`：

```
apollo.provider.config provider=<名字> baseurl=https://… model=<默认模型>
apollo.key.set token=<密钥> provider=<名字>
```
