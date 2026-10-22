# 中文配置指南

本文说明如何把 `AIBridgeAdapter` 接到 Trados Studio AI Bridge，并分别配置 TranslateGemma 与 Hy-MT2。

---

## 1. 前置条件

- Windows 10/11
- .NET Framework 4.8
- `llama-server`（llama.cpp 或 LM Studio 自带后端）
- Trados Studio 2026，已安装 AI Bridge
- 足够的显存/内存；模型越大，速度越慢

---

## 2. 选择模型

### TranslateGemma

适合：通用翻译、55 种语言、多模态翻译。

常见 GGUF：

- `translategemma-4b-it`：轻量，适合 8 GB 显存
- `translategemma-12b-it`：质量更好，但 8 GB 显存通常需要 CPU/GPU 混合

模型卡：

- https://huggingface.co/google/translategemma-4b-it

### Hy-MT2

适合：33 种语言互译、术语约束、风格控制、上下文翻译、结构化文本。

常见尺寸：

- `Hy-MT2-1.8B`：速度优先，显存占用低
- `Hy-MT2-7B`：质量与速度平衡，推荐 8 GB 显存使用 Q4_K_M/Q5_K_M
- `Hy-MT2-30B-A3B`：质量优先，需要更多显存/内存

模型与说明：

- https://github.com/Tencent-Hunyuan/Hy-MT2
- https://huggingface.co/collections/tencent/hy-mt2

注意：部分 Hy-MT2 GGUF 需要特定 llama.cpp 构建或 STQ 内核支持。加载失败时先检查模型卡要求。

---

## 3. 启动 llama-server

### TranslateGemma

```bat
llama-server.exe ^
  --model "C:\models\translategemma-4b-it.Q4_K_S.gguf" ^
  --host 127.0.0.1 --port 9931 ^
  --jinja --ctx-size 8192 --n-gpu-layers 99
```

### Hy-MT2

```bat
llama-server.exe ^
  --model "C:\models\Hy-MT2-7B-Q4_K_M.gguf" ^
  --host 127.0.0.1 --port 9931 ^
  --jinja --ctx-size 8192 --n-gpu-layers 99
```

### 参数说明

- `--jinja`：使用 GGUF 自带的 Jinja 聊天模板，必须加。
- `--ctx-size`：上下文长度。Trados 单段通常 4096 已够；需要长上下文可设 8192。
- `--n-gpu-layers`：交给 GPU 的层数。8 GB 显存从 99 开始，OOM 就降低到 28/24/20。
- `--parallel 1`：适配器逐段请求，单槽即可，能省显存。
- 不要设置 `--api-key`：适配器没有发送 `Authorization`。
- Hy-MT2 不要复用 TranslateGemma 的 `--chat-template-file`；让模型使用自己的模板。

确认模型已加载：

```text
http://127.0.0.1:9931/v1/models
```

应返回当前 GGUF 的路径。

---

## 4. 启动适配器

```bat
start-gemma.bat
```

或：

```bat
start-hymt2.bat
```

也可以自动识别：

```bat
start-auto.bat
```

默认监听 `127.0.0.1:9932`，上游 `127.0.0.1:9931`。

### 常用参数

```text
--port 9932
--upstream http://127.0.0.1:9931
--engine gemma | hymt2
--tags quality | preserve
--debug
```

示例：

```bat
AIBridgeAdapter.exe --engine hymt2 --port 9934
```

如果要同时连接两个模型，需要分别启动两个 `llama-server`（不同端口）和两个适配器实例；同一台机器同时加载两个大模型通常不现实。

---

## 5. 配置 AI Bridge

在 Trados Studio 中打开 AI Bridge 连接设置：

| 字段 | 值 |
|---|---|
| Endpoint | `http://127.0.0.1:9932/v1/` |
| Models path | `http://127.0.0.1:9932/v1/models` |
| Model | 任意值；建议选择 `/v1/models` 返回的 GGUF 路径 |
| API key | 任意占位值；适配器不校验 |

点击 **Test Connection**，应返回：

```text
This connection is running
```

然后使用 **Translate** 模式翻译一个短句。

---

## 6. 提示词与术语

### TranslateGemma

- 只使用原文和语言代码。
- 不支持术语表、上下文、风格指令。
- 语言代码会自动规范化：`zh-CN → zh-Hans`、`zh-TW → zh-Hant`、`ja-JP → ja`。

### Hy-MT2

- 使用官方中文/英文翻译指令。
- 支持 `[TERMS]` 术语表：
  - 自动转换为 `源词 翻译成 目标词`
  - 自动跳过 forbidden / deprecated
- 支持 `[CONTEXT]` 作为只读背景。
- 支持非默认 `[USER_PROMPT]` 作为附加风格要求。
- `[COMMENT_PROMPT]` 不处理，Comment 通常为空。

要让 AI Bridge 真正传入术语，需要在 AI Bridge 中启用术语表/术语提供程序。适配器解析不到的格式会原样传给模型。

### AI Bridge Prompt 设置建议（Hy-MT2）

| 设置 | 建议 |
|---|---|
| Prompt Name | `Hy-MT2` |
| Mode | `Translate` |
| Additional system guidance | 留空 |
| Prompt instruction | 可留空；需要风格控制时写如 `Use a formal, concise tone.` |
| Include tags | 开启 |
| Terminology provider | 需要术语时开启 |
| Context | 需要上下文时开启 |
| Include comment | 可选；适配器通常返回空 Comment |

说明：适配器会自己构建 Hy-MT2 的翻译指令；AI Bridge 的自定义 Prompt 只有在不是默认通用文案时，才会作为“附加要求”传入。TranslateGemma 模式会忽略 Prompt、术语和上下文。

---

## 7. 标签与占位符

适配器会保护：

- `<Locked...>...</Locked...>`
- `{0}`、`{name}`、`{{var}}`
- `%s`、`%d`、`%1$s`
- URL、Email、HTML 实体

内联标签模式：

- `--tags quality`（默认）：译文优先；模型没保留的标签会被丢弃，并在 Comment 中说明。
- `--tags preserve`：标签优先；所有标签用哨兵 token 保护，措辞可能变差。

`<Locked...>` 内容始终原样恢复。

---

## 8. 性能建议

- 7B Q4_K_M 在 8 GB 显存上通常比 12B/30B 更实用。
- 先关闭其他占用显存的程序（LM Studio 已加载模型、游戏、浏览器硬件加速等）。
- `--ctx-size` 越大，KV cache 占用越高。
- 批量翻译是逐段请求；段数越多耗时越长。
- 如果经常 OOM，降低 `--n-gpu-layers` 或换更小量化。

---

## 9. 安全与隐私

- 适配器只监听 `127.0.0.1`，不接受外部连接。
- 没有鉴权、没有 TLS；不要暴露到局域网或公网。
- `bridge.log` 和 `bridge-debug.log` 会包含原文/译文，不要提交或公开。
- 本仓库不包含任何 Trados 配置、模型路径或用户文档。

---

## 10. 验证清单

1. `http://127.0.0.1:9931/v1/models` 返回目标模型。
2. `http://127.0.0.1:9932/health` 返回 `{"status":"ok"}`。
3. 启动日志中 `Engine` 与模型匹配。
4. AI Bridge Test Connection 返回 `This connection is running`。
5. 短句翻译返回 `TransUnits` JSON，没有 `[USER_PROMPT]` / `[COMMENT_PROMPT]`。
6. `bridge.log` 出现 `OK single ...` 或 `OK batch ...`。
