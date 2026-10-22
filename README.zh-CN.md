# Trados AI Bridge Adapter

**语言：** [English](README.md) | [简体中文](README.zh-CN.md)  
**中文文档：** [配置指南](docs/zh-CN/configuration.md) | [故障排查](docs/zh-CN/troubleshooting.md)

一个本地 OpenAI 兼容适配器，让 **llama.cpp / LM Studio 承载的翻译模型**稳定接入 **Trados Studio AI Bridge**。

目前支持两类模型：

- **Google TranslateGemma**（`translategemma-*-it` GGUF）
- **腾讯 Hy-MT2**（`Hy-MT2-*` GGUF）

程序监听 `127.0.0.1:9932`，把 AI Bridge 的请求转换成模型真正需要的格式，再转发给 `127.0.0.1:9931` 的 `llama-server`，最后按 AI Bridge 要求返回 `TransUnits` JSON。

附带 WinForms 图形界面：可配置上游 API 地址、监听端口、模型、引擎、标签模式、可选 Bearer API Key，并支持启动/停止、实时日志和 t/s 运行监控。

> 与 RWS、Google、腾讯无隶属关系，使用风险自负。

---

## 为什么需要它

### 1. TranslateGemma 需要 `chat_template_kwargs`

TranslateGemma 的 GGUF 模板要求：

```json
{
  "source_lang_code": "zh-Hans",
  "target_lang_code": "ja"
}
```

AI Bridge 无法发送这些字段。llama-server 收到普通字符串消息时会按 Content-only 模式渲染，模型就把整段 AI Bridge 提示词当作待翻译文本并原样输出。

适配器会提取原文，并做语言代码规范化：

- `zh-CN` → `zh-Hans`
- `zh-TW` / `zh-HK` / `zh-MO` → `zh-Hant`
- `ja-JP` → `ja`、`en-US` → `en` 等

### 2. AI Bridge 使用 CRLF 换行

AI Bridge 的分节标记使用 `\r\n`：

```text
[SOURCE]
...
[USER_PROMPT]
...
[COMMENT_PROMPT]
...
```

如果正则只用 `$` 结尾，在 `\r\n` 下不会停在 `[USER_PROMPT]`，会把后面的提示词也当成原文。适配器曾经因此提取出 356 字符而不是 141 字符，模型译文里就带出了 `[USER_PROMPT]` 和 `[COMMENT_PROMPT]`。

现在所有分节、segment id、terms 正则都兼容 `\r?\n` 和 `\r?$`。

### 3. Hy-MT2 有专用指令格式

Hy-MT2 需要完整语言名和翻译指令，例如：

```text
将以下文本翻译为日语，注意只需要输出翻译后的结果，不要额外解释：

{source_text}
```

源语言和目标语言都不涉及中文时使用英文指令。适配器会自动拼接术语、上下文和自定义风格要求。

---

## 模型下载

TranslateGemma：

- 模型卡：https://huggingface.co/google/translategemma-4b-it
- 集合：https://huggingface.co/collections/google/translategemma

Hy-MT2：

- 官方仓库：https://github.com/Tencent-Hunyuan/Hy-MT2
- Hugging Face 集合：https://huggingface.co/collections/tencent/hy-mt2

建议：8 GB 显存优先 7B Q4_K_M / Q5_K_M；12B、30B 通常需要更强显卡或 CPU/GPU 混合。

---

## 架构

```text
Trados Studio AI Bridge
        |
        |  POST /v1/chat/completions
        v
127.0.0.1:9932  AIBridgeAdapter.exe
        |
        |  POST /v1/chat/completions
        v
127.0.0.1:9931  llama-server
        |
        v
TranslateGemma / Hy-MT2 GGUF
```

同时支持 `/v1/models` 代理、连接测试（返回 `This connection is running`）和 SSE 流式响应。

---

## 构建

```bat
build.cmd
```

输出：

- `AIBridgeAdapter.exe`：命令行适配器
- `AIBridgeAdapterGui.exe`：图形界面

需要 Windows 自带的 .NET Framework 4.8。

---

## 快速开始

### 方式 A：图形界面（推荐）

1. 先启动 `llama-server`（见下方）。
2. 双击 `start-gui.bat` 或 `AIBridgeAdapterGui.exe`。
3. 填写：
   - **API 地址**：`http://127.0.0.1:9931`（也可以是 LM Studio 的 `http://127.0.0.1:1234`）
   - **监听端口**：`9932`
   - 点击 **刷新模型**，选择模型
   - 如上游需要鉴权，勾选 **使用 Authorization Bearer API Key** 并输入 Key
   - **引擎**：自动识别 / TranslateGemma / Hy-MT2
   - **标签模式**：quality / preserve
4. 点击 **启动适配器**。GUI 会在后台启动 `AIBridgeAdapter.exe` 并显示日志。
5. 在 **运行监控** 面板查看当前请求的 t/s、耗时、token 数，以及最近请求和累计平均。
6. 把界面上的 **Endpoint** / **Models path** 复制到 Trados AI Bridge。
7. 关闭 GUI 前先点 **停止适配器**。

设置保存在 `gui-settings.json`；API Key 使用 Windows DPAPI（当前用户）加密，子进程通过环境变量接收，不出现在命令行里。

### 运行监控

适配器提供：

```text
GET http://127.0.0.1:9932/metrics
```

GUI 每 700 ms 读取一次，显示：

- **当前请求**：是否有请求进行中、已耗时、已生成 token 数、实时 t/s
- **最近请求**：t/s、prompt/completion token 数、耗时、成功/失败
- **累计**：模型请求数、成功数、失败数、平均 t/s

说明：

- 适配器会向上游请求流式响应，因此生成过程中就能看到 tokens 增长。
- 最近请求的 t/s 优先使用 llama-server 的 `timings.predicted_per_second`。
- 当前 t/s 按流式 chunk 估算，生成过程中会逐渐稳定。
- 统计的是**模型请求数**，不是 Trados segment 数；标签重试可能让请求数大于段数。
- `/metrics` 只绑定回环地址，没有鉴权。

### 方式 B：命令行

#### 1. 启动 llama-server

TranslateGemma：

```bat
llama-server.exe ^
  --model "C:\models\translategemma-4b-it.Q4_K_S.gguf" ^
  --host 127.0.0.1 --port 9931 ^
  --jinja --ctx-size 8192 --n-gpu-layers 99
```

Hy-MT2：

```bat
llama-server.exe ^
  --model "C:\models\Hy-MT2-7B-Q4_K_M.gguf" ^
  --host 127.0.0.1 --port 9931 ^
  --jinja --ctx-size 8192 --n-gpu-layers 99
```

注意：

- 必须加 `--jinja`，使用模型自带模板。
- Hy-MT2 不要复用 TranslateGemma 的 `--chat-template-file`。
- 适配器不发送 `Authorization`，所以 llama-server 不要设置 `--api-key`。

#### 2. 启动适配器

```bat
start-gemma.bat
```

或：

```bat
start-hymt2.bat
```

也可以让适配器根据 `/v1/models` 自动识别：

```bat
start-auto.bat
```

默认监听 `http://127.0.0.1:9932/v1/`。

#### 3. 配置 AI Bridge

| 字段 | 值 |
|---|---|
| Endpoint | `http://127.0.0.1:9932/v1/` |
| Models path | `http://127.0.0.1:9932/v1/models` |
| Model | 任意值，适配器按上游实际模型转发；建议选 `/v1/models` 返回的路径 |
| API key | 任意占位值 |

点击 **Test Connection**，应返回 `This connection is running`。然后用 **Translate** 模式翻译一个短句。

---

## 参数

```text
--port <n>          监听端口，默认 9932
--upstream <url>    上游 llama-server，默认 http://127.0.0.1:9931
--engine <name>     gemma | hymt2，默认 gemma
--tags <mode>       quality | preserve，默认 quality
--model <id>        上游模型 id/路径；留空则使用 /v1/models 的第一个模型
--api-key <key>     可选的上游 Bearer Token（也可用环境变量 AIBRIDGE_API_KEY）
--debug             将完整上游请求/响应写入 bridge-debug.log
```

GUI 会自动传递这些参数，并通过环境变量传递 API Key，避免出现在命令行中。

---

## 行为说明

### TranslateGemma

- 只发送原文和语言代码。
- 术语、上下文、风格指令不会发送，因为该模型的模板不支持。
- 目标语言代码会先规范化。

### Hy-MT2

- 自动构建完整翻译指令。
- 源或目标涉及中文时用中文指令，否则用英文指令。
- `[TERMS]` 转换为 `源词 翻译成 目标词`，自动跳过 forbidden / deprecated。
- `[CONTEXT]` 作为只读背景。
- 非默认的 `[USER_PROMPT]` 作为附加风格要求，放在 system message 中。
- 标签/格式/Locked 内容保护说明放在 **system message**，用户消息只保留官方最小翻译指令。这样模型不会把说明当成原文翻译并拼到译文开头。
- `[COMMENT_PROMPT]` 不处理，Comment 通常为空。

---

## 标签与占位符

会保护：

- `<Locked...>...</Locked...>`
- `{0}`、`{name}`、`{{var}}`
- `%s`、`%d`、`%1$s`
- URL、Email、HTML 实体

内联标签会在翻译后校验：

- `--tags quality`（默认）：模型没保留的标签会被丢弃，并在 AI Bridge 的 Comment 中说明；译文更自然。
- `--tags preserve`：所有标签用哨兵 token 保护，能保留标签，但措辞可能变差。

`<Locked...>` 内容在两种模式下都会原样恢复。

---

## 限制

- 不支持 Refinement 模式，会返回 HTTP 400。
- 批量按 segment 顺序逐个请求。
- 适配器本身无入站鉴权、无 TLS，只绑定 `127.0.0.1`；上游可配置 Bearer API Key。
- 标签能否保留取决于模型，请用实际文档测试。
- Hy-MT2 的 GGUF 可能需要特定 llama.cpp 构建。
- TranslateGemma 不支持术语、上下文、风格指令。
- 日志可能包含原文和译文，不要提交 `bridge.log` / `bridge-debug.log`；`gui-settings.json` 含加密 Key，也不要提交。

---

## 已验证环境

- Trados Studio 2026 AI Bridge 1.0.0.0
- LM Studio CUDA 后端 2.62.0 / llama-server
- TranslateGemma 4B IT Q4_K_S
- Hy-MT2-7B Q6_K
- Hy-MT2-30B-A3B Q6_K（社区量化版）
- 提示词泄漏修复：最小用户指令 + system message，已用 30B 模型验证
- WinForms GUI：模型刷新、启动/停止、加密 Bearer Key、实时日志
- 运行监控：7B Q6_K 翻译过程中验证当前 t/s、最近请求与平均 t/s
- Windows 11 + .NET Framework 4.8

CRLF 修复用真实 AI Bridge 请求验证：

- 修复前：提取 356 字符，输出包含 `[USER_PROMPT]`、`[COMMENT_PROMPT]`
- 修复后：提取 141 字符，只输出译文

---

## 常见问题

### 输出里有 `[USER_PROMPT]` / `[COMMENT_PROMPT]`

这是 v1.0.0 及更早版本的 CRLF 解析问题。升级到 v1.0.1 或更新版本，并重启适配器。详见[故障排查](docs/zh-CN/troubleshooting.md)。

### 译文开头出现 “All XML tags...” / “The content within the <Locked...> tag...”

这是 v1.2.0 及更早版本的 Hy-MT2 提示词问题：标签保护说明放在用户原文区，模型把它也翻译了。升级到 v1.2.1 或更新版本。详见[故障排查](docs/zh-CN/troubleshooting.md)。

### 术语不生效

确认使用的是 Hy-MT2，并在 AI Bridge 中启用了术语表。TranslateGemma 不支持术语。详见[故障排查](docs/zh-CN/troubleshooting.md)。

### 标签丢失

默认 `--tags quality` 会优先保证译文自然，必要时丢弃标签并在 Comment 说明；需要强制保留时使用 `--tags preserve`。详见[故障排查](docs/zh-CN/troubleshooting.md)。

### 模型加载失败或速度慢

先确认 llama.cpp 构建与模型匹配，再调整量化、`--n-gpu-layers` 和 `--ctx-size`。详见[故障排查](docs/zh-CN/troubleshooting.md)。

---

## 目录结构

```text
src/AIBridgeAdapter.cs          适配器源码
src/AIBridgeAdapterGui.cs       WinForms GUI 源码
build.cmd                       编译两个可执行文件
start-gui.bat                   启动图形界面
start-gemma.bat                 TranslateGemma 模式
start-hymt2.bat                 Hy-MT2 模式
start-auto.bat                  自动识别模型
samples/                        合成测试请求
docs/zh-CN/configuration.md     中文配置指南
docs/zh-CN/troubleshooting.md   中文故障排查
```

---

## 许可证

MIT，详见 [LICENSE](LICENSE)。
