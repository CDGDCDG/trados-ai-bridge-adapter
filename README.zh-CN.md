# Trados AI Bridge Adapter

一个本地 OpenAI 兼容适配器，让 **llama.cpp / LM Studio 承载的翻译模型**稳定接入 **Trados Studio AI Bridge**。

目前支持两类模型：

- **Google TranslateGemma**（`translategemma-*-it` GGUF）
- **腾讯 Hy-MT2**（`Hy-MT2-*` GGUF）

程序监听 `127.0.0.1:9932`，把 AI Bridge 的请求转换成模型真正需要的格式，再转发给 `127.0.0.1:9931` 的 `llama-server`，最后按 AI Bridge 要求返回 `TransUnits` JSON。

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

输出 `AIBridgeAdapter.exe`。需要 Windows 自带的 .NET Framework 4.8。

---

## 快速开始

### 1. 启动 llama-server

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

### 2. 启动适配器

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

### 3. 配置 AI Bridge

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
--debug             将完整上游请求/响应写入 bridge-debug.log
```

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
- 非默认的 `[USER_PROMPT]` 作为附加风格要求。
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
- 无鉴权、无 TLS，只绑定 `127.0.0.1`。
- 标签能否保留取决于模型，请用实际文档测试。
- Hy-MT2 的 GGUF 可能需要特定 llama.cpp 构建。
- TranslateGemma 不支持术语、上下文、风格指令。
- 日志可能包含原文和译文，不要提交 `bridge.log` / `bridge-debug.log`。

---

## 已验证环境

- Trados Studio 2026 AI Bridge 1.0.0.0
- LM Studio CUDA 后端 2.62.0 / llama-server
- TranslateGemma 4B IT Q4_K_S
- Hy-MT2-30B-A3B Q6_K（社区量化版）
- Windows 11 + .NET Framework 4.8

CRLF 修复用真实 AI Bridge 请求验证：

- 修复前：提取 356 字符，输出包含 `[USER_PROMPT]`、`[COMMENT_PROMPT]`
- 修复后：提取 141 字符，只输出译文

---

## 许可证

MIT，详见 [LICENSE](LICENSE)。
