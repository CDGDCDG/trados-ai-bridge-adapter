# Trados AI Bridge Adapter

A small local OpenAI-compatible adapter that makes **llama.cpp / LM Studio-hosted translation models** work reliably with **Trados Studio AI Bridge**.

It was built for two model families:

- **Google TranslateGemma** (`translategemma-*-it`, GGUF)
- **Tencent Hy-MT2** (`Hy-MT2-*`, GGUF)

It runs on Windows, listens on `127.0.0.1:9932`, calls a local `llama-server` on `127.0.0.1:9931`, and returns the `TransUnits` JSON that AI Bridge expects.

> Not affiliated with RWS, Google, or Tencent. Use at your own risk.

---

## Why this exists

Trados Studio AI Bridge is an OpenAI-compatible client, but translation models are not all OpenAI-compatible chat models.

Three concrete problems are solved here:

### 1. TranslateGemma needs `chat_template_kwargs`

TranslateGemma's GGUF chat template expects structured fields:

```json
{
  "source_lang_code": "zh-Hans",
  "target_lang_code": "ja"
}
```

AI Bridge cannot send these fields. When llama-server receives a plain string message, its template reports `supports_string_content=false` and falls back to a Content-only rendering. The model then treats the entire AI Bridge prompt as the text to translate and echoes it.

The adapter extracts the source and calls llama-server with `chat_template_kwargs`, including language-code normalization:

- `zh-CN` → `zh-Hans`
- `zh-TW` / `zh-HK` / `zh-MO` → `zh-Hant`
- `ja-JP` → `ja`, `en-US` → `en`, etc.

### 2. AI Bridge prompts use CRLF

AI Bridge sends section markers with `\r\n` line endings:

```text
[SOURCE]
...
[USER_PROMPT]
...
[COMMENT_PROMPT]
...
```

A naive regex ending in `$` does not stop at `[USER_PROMPT]` when the line ends with `\r\n`. The adapter originally captured 356 characters instead of the 141-character source, so the model received the source **plus** `[USER_PROMPT]` and `[COMMENT_PROMPT]` and echoed them into the translation.

All section, segment-id, and terms regexes now accept `\r?\n` and `\r?$`.

### 3. Hy-MT2 has its own instruction format

Hy-MT2 expects full language names and a translation instruction, for example:

```text
将以下文本翻译为日语，注意只需要输出翻译后的结果，不要额外解释：

{source_text}
```

For non-Chinese pairs it uses an English instruction. The adapter builds the prompt, and optionally adds:

- terminology lists
- read-only context
- a custom style/user prompt

---

## Architecture

```text
Trados Studio AI Bridge
        |
        |  POST /v1/chat/completions  (OpenAI format)
        v
127.0.0.1:9932  AIBridgeAdapter.exe
        |
        |  POST /v1/chat/completions  (model-specific payload)
        v
127.0.0.1:9931  llama-server
        |
        v
TranslateGemma GGUF / Hy-MT2 GGUF
```

The adapter also proxies `GET /v1/models`, answers the AI Bridge connection test with `This connection is running`, and supports SSE streaming.

---

## Features

- Single-segment `[SOURCE]` and batch `[SEGMENTS]` requests
- CRLF- and LF-safe prompt parsing
- TranslateGemma engine:
  - `chat_template_kwargs`
  - language-code normalization
  - temperature 0
- Hy-MT2 engine:
  - official Chinese/English prompt format
  - full language names
  - terminology, context, and style instructions
  - recommended sampling parameters (`temperature=0.7`, `top_p=0.6`, `top_k=20`, `repeat_penalty=1.05`)
- Locked content (`<Locked...>...</Locked...>`) is never translated
- `{0}`, `%s`, `{{var}}`, URLs, emails, HTML entities are protected and restored
- Inline XML tags are validated; optional token-preserving mode
- AI Bridge `Comment` is used only when inline tags had to be dropped
- No external runtime dependencies; .NET Framework 4.8 only

---

## Requirements

- Windows 10/11
- .NET Framework 4.8 (the `csc.exe` used by `build.cmd` is included with Windows)
- A local llama.cpp `llama-server` with a translation model loaded
- Trados Studio 2026 with AI Bridge

---

## Build

```bat
build.cmd
```

Output: `AIBridgeAdapter.exe`.

The build uses:

```text
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

---

## Quick start

### 1. Start llama-server

TranslateGemma example:

```bat
llama-server.exe ^
  --model "C:\models\translategemma-4b-it.Q4_K_S.gguf" ^
  --host 127.0.0.1 --port 9931 ^
  --jinja --ctx-size 8192 --n-gpu-layers 99
```

Hy-MT2 example:

```bat
llama-server.exe ^
  --model "C:\models\Hy-MT2-7B-Q4_K_M.gguf" ^
  --host 127.0.0.1 --port 9931 ^
  --jinja --ctx-size 8192 --n-gpu-layers 99
```

Notes:

- Use `--jinja` so the model's own chat template is used.
- Do **not** reuse a TranslateGemma `--chat-template-file` for Hy-MT2. Let the GGUF template load.
- The adapter sends no `Authorization` header. Do not start llama-server with `--api-key` unless you add authentication support to the adapter.
- Hy-MT2 GGUF builds may require a specific llama.cpp build; see the model card.

### 2. Start the adapter

For TranslateGemma:

```bat
start-gemma.bat
```

For Hy-MT2:

```bat
start-hymt2.bat
```

Or let the adapter detect the engine from `/v1/models`:

```bat
start-auto.bat
```

Default listener: `http://127.0.0.1:9932/v1/`.

### 3. Configure AI Bridge

Create or edit a connection:

| Field | Value |
|---|---|
| Endpoint | `http://127.0.0.1:9932/v1/` |
| Models path | `http://127.0.0.1:9932/v1/models` |
| Model | Any value; the adapter routes to llama-server. Prefer the path returned by `/v1/models`. |
| API key | Any placeholder; the adapter does not validate it. |

Click **Test Connection**. It should return:

```text
This connection is running
```

Then translate a short segment in **Translate** mode.

---

## Options

```text
AIBridgeAdapter.exe [options]

--port <n>          Listen port, default 9932
--upstream <url>    llama-server base URL, default http://127.0.0.1:9931
--engine <name>     gemma | hymt2, default gemma
--tags <mode>       quality | preserve, default quality
--debug             Write full upstream requests/responses to bridge-debug.log
```

Examples:

```bat
AIBridgeAdapter.exe --engine hymt2 --port 9934
AIBridgeAdapter.exe --engine gemma --upstream http://127.0.0.1:9933
```

Run separate adapter instances on different ports only if you also run separate llama-server instances for each model.

---

## Engine behavior

### TranslateGemma

- Only the source text and language codes are sent to the model.
- Terms, context, and style prompts are **not** sent, because the TranslateGemma template does not accept them.
- Target code is normalized before calling llama-server.

### Hy-MT2

- The adapter builds a full instruction prompt.
- Chinese is used when the source or target is Chinese; otherwise English.
- `[TERMS]` is converted to `source 翻译成 target` / `source translates to target`; forbidden and deprecated entries are skipped.
- `[CONTEXT]` is passed as read-only background.
- A non-default `[USER_PROMPT]` is passed as an additional style requirement.
- `[COMMENT_PROMPT]` is ignored; the adapter returns an empty comment unless tags were dropped.

---

## Tags and placeholders

The adapter protects:

- `<Locked...>...</Locked...>` blocks
- `{0}`, `{name}`, `{{var}}`
- `%s`, `%d`, `%1$s`
- URLs and email addresses
- HTML entities

Inline XML tags are checked after translation.

- `--tags quality` (default): if the model does not preserve a tag, the tag is dropped from that segment and a note is placed in the AI Bridge `Comment` field. The wording stays fluent.
- `--tags preserve`: all tags are replaced with sentinel tokens before translation and restored afterward. This keeps tags but can make the model's wording less natural.

`<Locked...>` content is always restored verbatim in both modes.

---

## Limitations

- Refinement mode is not supported; the adapter returns HTTP 400.
- Batch segments are translated sequentially, one model request per segment.
- The adapter has no authentication or TLS. It binds to `127.0.0.1` only.
- Tag preservation depends on the model; always test with your document.
- Hy-MT2's GGUF may need a llama.cpp build with the required kernel/template support.
- TranslateGemma ignores terminology, context, and style prompts.
- `[TERMS]` parsing is format-tolerant (JSON, `Source/Target/Status` fields, arrow/tab pairs), but AI Bridge builds may differ. If terminology is not applied, open an issue with a sanitized `[TERMS]` sample.
- Logs can contain source and translated text. Do not commit or publish `bridge.log` or `bridge-debug.log`.

---

## Tested with

- Trados Studio 2026 AI Bridge 1.0.0.0
- llama.cpp / llama-server (LM Studio CUDA backend 2.62.0)
- TranslateGemma 4B IT Q4_K_S GGUF
- Hy-MT2-7B Q6_K GGUF
- Hy-MT2-30B-A3B Q6_K GGUF (community quantization)
- Windows 11, .NET Framework 4.8

The CRLF fix was verified against a real AI Bridge request:

- before: extracted source length 356, output contained `[USER_PROMPT]` and `[COMMENT_PROMPT]`
- after: extracted source length 141, output contained only the translation

---

## Repository layout

```text
src/AIBridgeAdapter.cs   adapter source
build.cmd                compile with .NET Framework csc
start-gemma.bat          run in TranslateGemma mode
start-hymt2.bat          run in Hy-MT2 mode
start-auto.bat           auto-detect engine from /v1/models
samples/                 synthetic AI Bridge request examples
```

---

## License

MIT. See [LICENSE](LICENSE).
