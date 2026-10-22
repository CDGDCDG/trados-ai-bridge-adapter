# 中文故障排查

按现象查找。每条都给出原因、检查和修复方法。

---

## 译文开头出现 “All XML tags...” / “The content within the <Locked...> tag...”

**v1.2.0 及更早版本的 Hy-MT2 提示词问题。**

原因：

- 适配器把“保留 XML 标签/Locked 内容”的说明放在了用户消息里，和原文连在一起。
- 部分翻译模型（尤其是自定义微调版）会把这段说明也当成待翻译文本，翻译后拼在译文开头。
- 输出里出现字面量 `<Locked...>` 时，AI Bridge 还可能报 `XmlException`，因为 Trados 把它当成了 XML 标签。

表现示例：

```text
All XML tags, attributes, placeholders, and formatting markers must be retained; ...
The content within the <Locked...> tag should remain in its original form.
如果你试图强行破解密码……
```

修复：

1. 升级到 **v1.2.1 或更新版本**。
2. 停止旧适配器和 GUI，替换 `AIBridgeAdapter.exe` 和 `AIBridgeAdapterGui.exe`。
3. 重启适配器，重新翻译同一段。

v1.2.1 起：

- 用户消息只保留官方最小翻译指令。
- 标签/Locked/格式保护说明放在 **system message**。
- 不会再被模型当作原文翻译。

验证：

```text
bridge.log 出现 OK single ... 或 OK batch ...
译文开头直接是正文，不再出现 All XML tags...
```

如果仍然出现：

- 确认运行的是新 exe（检查文件时间/版本目录）。
- 用 `--debug` 启动，查看 `bridge-debug.log` 里的 `UPSTREAM REQUEST`，确认 `messages` 包含 `system` 和最小 `user` 指令。
- 确认没有直接连接 9931 绕过适配器。

---

## 1. 译文里出现 `[USER_PROMPT]` 或 `[COMMENT_PROMPT]`

**版本 1.0.0 及更早版本的已知问题。**

原因：AI Bridge 使用 CRLF（`\r\n`）换行；旧正则只用 `$` 结尾，没有停在 `[USER_PROMPT]`，把后面的提示词也当成原文发给了模型。

检查 `bridge.log`：

```text
OK   single chars=356 ...
```

如果字符数明显大于原文（例如原文只有 141 字符），就是这个问题。

修复：

1. 升级到 v1.0.1 或更新版本。
2. 停止旧的适配器进程，替换 `AIBridgeAdapter.exe`。
3. 重启适配器，再翻译同一条。

正确日志应为：

```text
OK   single chars=141 ...
```

如果仍然出现，确认 AI Bridge 的 Endpoint 是 `http://127.0.0.1:9932/v1/`，而不是直接指向 `9931`。

---

## 2. 输出是整段英文提示词，或模型在“解释”提示词

可能原因：

- AI Bridge 连接直接指向了 `llama-server`（9931），没有经过适配器。
- 适配器用了错误的引擎（例如 Hy-MT2 模型仍以 `gemma` 模式运行）。
- llama-server 没有使用模型自己的模板。

检查：

1. 打开 `http://127.0.0.1:9932/health`，确认适配器在运行。
2. 看适配器启动日志：

```text
Engine : hymt2 (Hy-MT2 full-name prompt)
```

3. 看 `http://127.0.0.1:9931/v1/models` 是否是当前模型。
4. 确认 `llama-server` 启动了 `--jinja`。
5. Hy-MT2 不要复用 TranslateGemma 的 `--chat-template-file`。

修复：

```bat
start-hymt2.bat
```

或：

```bat
AIBridgeAdapter.exe --engine hymt2
```

---

## 3. Test Connection 失败

检查顺序：

1. `http://127.0.0.1:9932/health` 是否返回 `{"status":"ok",...}`。
2. `http://127.0.0.1:9931/v1/models` 是否能打开。
3. AI Bridge Endpoint 是否是 `http://127.0.0.1:9932/v1/`。
4. 适配器窗口是否报端口占用。
5. 是否同时开了多个适配器实例。

端口被占用时：

```bat
AIBridgeAdapter.exe --port 9934
```

然后把 AI Bridge Endpoint 改成 `http://127.0.0.1:9934/v1/`。

---

## 4. 译文为空

可能原因：

- 上游模型没有正确生成。
- `llama-server` 模板错误。
- 模型上下文/显存不足。
- 适配器返回了错误，但 AI Bridge 没有显示。

检查：

- `bridge.log` 是否有 `FAIL`。
- `bridge-debug.log`（用 `--debug` 启动）里的上游响应。
- `llama-server` 控制台是否有 OOM、template error、context overflow。

修复：

- 降低 `--ctx-size` 或 `--n-gpu-layers`。
- 确认模型文件和模板匹配。
- 用短句测试，排除超长输入。

---

## 5. 术语没有生效

先确认：

- AI Bridge 中是否启用了术语表/术语提供程序。
- 只有 **Hy-MT2** 模式会使用术语；TranslateGemma 不支持术语。
- `[TERMS]` 是否真的出现在请求里。

适配器支持这些格式：

```text
Source: 对象型号
Target: 対象機種
Status: Preferred
```

```text
对象型号 => 対象機種
```

以及 JSON 对象/数组。`forbidden` / `deprecated` 会被跳过。

如果格式不同：

1. 用 `--debug` 启动。
2. 在 `bridge-debug.log` 里找到发给上游的 prompt。
3. 检查 `参考下面的翻译` / `Reference the following translations` 段。
4. 提交 issue 时附上**脱敏后的** `[TERMS]` 片段。

---

## 6. 内联标签丢失

这是模型能力问题，不一定是适配器错误。

- 默认 `--tags quality`：标签丢失时保留自然译文，并在 Comment 中说明。
- `--tags preserve`：用 token 强制保护标签，但可能影响措辞。
- `<Locked...>` 内容在两种模式下都不会被翻译。

建议先用实际文档做 A/B 测试，再决定模式。

---

## 7. `{0}`、`%s` 等占位符丢失

1. 确认使用 v1.0.1 或更新版本。
2. 查看 `bridge.log` 是否有 `WARN` 或 `FAIL`。
3. 用一条只含占位符的短句测试：

```text
系统将在 {0} 秒后关闭，请保存文件 %s。
```

正常情况下应完整保留 `{0}` 和 `%s`。

如果仍丢失，附上脱敏后的原文和输出提交 issue。

---

## 8. 速度很慢或显存不足

- 7B Q4_K_M 通常比 12B/30B 更适合 8 GB 显存。
- 降低 `--n-gpu-layers` 可以减少 OOM，但速度会下降。
- 降低 `--ctx-size` 可以节省 KV cache。
- 关闭其他占用显存的程序。
- 批量翻译是逐段请求，段数越多耗时越长。
- 30B-A3B 在 8 GB 显存上通常需要 CPU/GPU 混合，速度会明显变慢。

---

## 9. 模型加载失败

检查：

- 模型文件是否完整。
- llama.cpp 版本是否支持该模型架构/量化。
- Hy-MT2 的部分 GGUF 需要特定构建或 STQ 内核支持。
- 显存/内存是否足够。
- 是否错误地加载了 TranslateGemma 的模板文件。

先用官方推荐的 llama.cpp 构建和最小参数测试模型本身，再接入适配器。

---

## 10. Refinement 模式报错

适配器只支持 **Translate** 模式。

如果 AI Bridge 使用 Refinement，会返回 HTTP 400。请在 AI Bridge 中切换到 Translate。

---

## 11. 中文乱码

- 用 `chcp 65001` 切到 UTF-8。
- 不要把日志/配置文件用错误的编码另存。
- 适配器内部使用 UTF-8 JSON。

`start-*.bat` 已经包含：

```bat
chcp 65001 >nul
```

---

## 12. GUI 相关问题

### 提示找不到 AIBridgeAdapter.exe

`AIBridgeAdapterGui.exe` 和 `AIBridgeAdapter.exe` 必须放在同一目录。

```text
AIBridgeAdapterGui.exe
AIBridgeAdapter.exe
```

运行 `build.cmd` 可同时生成两个文件。

### 刷新模型失败

检查：

1. API 地址是否正确（不要填 `/chat/completions`）。
2. `llama-server` 是否已启动。
3. 上游需要 Key 时，是否勾选并填写了正确的 Bearer Key。
4. 网络/防火墙是否允许访问该地址。

刷新失败不影响手动输入模型：直接在“模型”框里填模型 id 或 GGUF 路径即可。

### 点启动后立即停止

最常见原因是端口被占用：

- 9932 已有旧的适配器或命令行实例。
- 换一个监听端口，例如 `9933`。
- 或先停止旧的 `AIBridgeAdapter.exe`。

GUI 日志和 `bridge.log` 会显示 `cannot listen on 127.0.0.1:...`。

### 运行监控没有数据 / t/s 为 0

- v1.2.0 之前的适配器没有 `/metrics` 端点；升级到最新版。
- 适配器刚启动时，第一次请求还没开始，当前 t/s 为 0 是正常的。
- 请求处于预填充阶段（prompt 还在处理）时，生成 token 数为 0，t/s 也会显示 0；界面会显示“预填充/等待中”。
- 如果一直显示“监控暂不可用”，检查：
  - 适配器是否在运行；
  - GUI 的监听端口是否与适配器一致；
  - 浏览器打开 `http://127.0.0.1:<端口>/metrics` 是否有 JSON。
- 模型很快时，当前请求可能在两次刷新之间就结束了；看“最近请求”即可。

### 模型请求数比 Trados 段数多

统计的是**模型调用次数**，不是 segment 数。以下情况会让请求数增加：

- 标签校验失败后重试；
- 术语/上下文导致多轮 prompt；
- 批量翻译逐段请求。

### 当前 t/s 波动较大

当前 t/s 按流式 chunk 估算，生成初期会受预填充、网络和首 token 影响。看“最近请求”的 t/s 或累计平均值更稳定。

### 上游返回 401 / 403

- 勾选“使用 Authorization Bearer API Key”，填入上游的 Key。
- 启动日志应显示 `Auth : Bearer configured`。
- AI Bridge 连接设置里的 API Key 不会转发给上游；它只用于 AI Bridge 到适配器这一段。
- GUI 里保存的 Key 使用 Windows DPAPI 加密；换 Windows 用户后需要重新输入。

### 关闭 GUI 后适配器还在运行

正常退出时 GUI 会询问是否停止适配器。若 GUI 被强制结束，子进程可能残留：

1. 打开任务管理器。
2. 结束 `AIBridgeAdapter.exe`。
3. 重新启动 GUI。

### 设置无法保存 / Key 解密失败

- `gui-settings.json` 必须在 GUI 有写权限的目录。
- 换 Windows 用户或重装系统后，DPAPI 无法解密旧 Key；重新输入即可。
- 删除 `gui-settings.json` 会恢复默认设置。

---

## 13. 提交 issue 时请附上

- 适配器版本（`v1.0.1` 等）
- 引擎（gemma / hymt2）
- 模型名称与量化
- `llama-server` 启动参数
- `bridge.log` 最后 20 行
- 脱敏后的请求/输出片段

**不要上传：**

- 完整 Trados 项目或术语库
- 私人文档原文
- `bridge.log` / `bridge-debug.log` 原文件
- 任何 API key、路径中的用户名
