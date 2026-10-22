using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace TradosAIBridgeAdapter
{
    internal sealed class Segment
    {
        public string Id;
        public string Text;
        public string Terms;
        public Segment(string id, string text, string terms) { Id = id; Text = text; Terms = terms; }
    }

    internal sealed class TranslationResult
    {
        public string Text;
        public string Comment;
        public TranslationResult(string text, string comment) { Text = text; Comment = comment; }
    }

    internal sealed class TokenInfo
    {
        public int Id;
        public string Value;
        public bool Critical;
        public char LeftChar;
        public char RightChar;
        public bool LeftSpace;
        public bool RightSpace;
        public bool LeftBracket;
        public bool RightBracket;
    }

    internal static class Metrics
    {
        private static readonly object sync = new object();
        private static readonly DateTime startedAt = DateTime.UtcNow;
        private static int activeRequests;
        private static long totalRequests;
        private static long successRequests;
        private static long failedRequests;
        private static long totalPromptTokens;
        private static long totalCompletionTokens;
        private static double totalSeconds;
        private static long lastPromptTokens;
        private static long lastCompletionTokens;
        private static double lastSeconds;
        private static double lastTokensPerSecond;
        private static bool lastSuccess;
        private static string lastAt = "";
        private static string lastError = "";
        private static long currentStartedTicks;
        private static long currentTokens;

        public static void BeginRequest()
        {
            lock (sync)
            {
                activeRequests++;
                totalRequests++;
                currentStartedTicks = DateTime.UtcNow.Ticks;
                currentTokens = 0;
            }
        }

        public static void AddToken()
        {
            lock (sync) { currentTokens++; }
        }

        public static long GetCurrentTokens()
        {
            lock (sync) { return currentTokens; }
        }

        public static void EndRequest(bool success, long promptTokens, long completionTokens, double seconds, double tokensPerSecond, string error)
        {
            lock (sync)
            {
                if (activeRequests > 0) activeRequests--;
                if (success) successRequests++; else failedRequests++;
                if (seconds <= 0) seconds = 0.001;
                lastPromptTokens = promptTokens;
                lastCompletionTokens = completionTokens;
                lastSeconds = seconds;
                lastTokensPerSecond = tokensPerSecond;
                lastSuccess = success;
                lastAt = DateTime.Now.ToString("HH:mm:ss");
                lastError = Trim(error, 240);
                if (success)
                {
                    totalPromptTokens += promptTokens;
                    totalCompletionTokens += completionTokens;
                    totalSeconds += seconds;
                }
                currentTokens = 0;
            }
        }

        public static string ToJson(int port, string engine, string model)
        {
            lock (sync)
            {
                double uptime = (DateTime.UtcNow - startedAt).TotalSeconds;
                double currentElapsed = 0;
                double currentTps = 0;
                if (activeRequests > 0 && currentStartedTicks > 0)
                {
                    currentElapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - currentStartedTicks).TotalSeconds;
                    if (currentElapsed > 0.2) currentTps = currentTokens / currentElapsed;
                }
                double averageTps = totalSeconds > 0 ? totalCompletionTokens / totalSeconds : 0;

                Dictionary<string, object> current = new Dictionary<string, object>();
                current["active"] = activeRequests > 0;
                current["elapsedSeconds"] = Math.Round(currentElapsed, 2);
                current["tokens"] = currentTokens;
                current["tokensPerSecond"] = Math.Round(currentTps, 2);

                Dictionary<string, object> last = new Dictionary<string, object>();
                last["success"] = lastSuccess;
                last["promptTokens"] = lastPromptTokens;
                last["completionTokens"] = lastCompletionTokens;
                last["seconds"] = Math.Round(lastSeconds, 2);
                last["tokensPerSecond"] = Math.Round(lastTokensPerSecond, 2);
                last["at"] = lastAt;
                last["error"] = lastError;

                Dictionary<string, object> totals = new Dictionary<string, object>();
                totals["requests"] = totalRequests;
                totals["success"] = successRequests;
                totals["failed"] = failedRequests;
                totals["promptTokens"] = totalPromptTokens;
                totals["completionTokens"] = totalCompletionTokens;
                totals["seconds"] = Math.Round(totalSeconds, 2);
                totals["tokensPerSecond"] = Math.Round(averageTps, 2);

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["running"] = true;
                root["uptimeSeconds"] = Math.Round(uptime, 0);
                root["port"] = port;
                root["engine"] = engine;
                root["model"] = model;
                root["current"] = current;
                root["last"] = last;
                root["totals"] = totals;
                return new JavaScriptSerializer().Serialize(root);
            }
        }

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }

    internal static class Program
    {
        private static int listenPort = 9932;
        private static string upstream = "http://127.0.0.1:9931";
        private static string modelId = "translategemma-4b-it";
        private static bool debug;
        private static string tagMode = "quality";
        private static string engine = "gemma";
        private static bool engineExplicit;
        private static string modelOverride;
        private static string apiKey;
        private static HttpClient http;
        private static readonly object logLock = new object();
        private static readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly Regex CriticalMarkupRegex = new Regex(
            "<!--[\\s\\S]*?-->" +
            "|<(?<locked>Locked[A-Za-z0-9_]*)\\b[^>]*>[\\s\\S]*?</\\k<locked>>" +
            "|\\{\\{[^{}\\r\\n]+\\}\\}" +
            "|\\{[^{}\\r\\n]{1,80}\\}" +
            "|%[0-9]+\\$?[sdif]|%[sdif]" +
            "|\\$[0-9]+" +
            "|&[A-Za-z]+;|&#\\d+;" +
            "|https?://[^\\s<>\"']+" +
            "|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}",
            RegexOptions.IgnoreCase);
        private static readonly Regex TagRegex = new Regex(@"</?[A-Za-z][^<>]*?>", RegexOptions.Compiled);
        private static readonly Regex TokenRegex = new Regex(@"XQ(\d{4})(?!\d)", RegexOptions.Compiled);
        private static readonly Regex TokenLikeRegex = new Regex(@"[\uFF38\uFF58Xx][\uFF51\uFF71Qq]\s*[0-9\uFF10-\uFF19]{4}", RegexOptions.Compiled);
        private static readonly Regex PairedDecorationRegex = new Regex(
            @"(?<lead>[「『""“\[\【\(（])\s*(?<tok>XQ\d{4})(?!\d)\s*(?<trail>[」』""”\]\】\)）])", RegexOptions.Compiled);
        private static readonly Regex LeadingDecorationRegex = new Regex(
            @"(?<lead>[「『""“\[\【\(（])\s*(?<tok>XQ\d{4})(?!\d)", RegexOptions.Compiled);
        private static readonly Regex TrailingDecorationRegex = new Regex(
            @"(?<tok>XQ\d{4})(?!\d)\s*(?<trail>[」』""”\]\】\)）])", RegexOptions.Compiled);

        private static void Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length) int.TryParse(args[++i], out listenPort);
                else if (args[i] == "--upstream" && i + 1 < args.Length) upstream = args[++i].TrimEnd('/');
                else if (args[i] == "--tags" && i + 1 < args.Length) tagMode = args[++i].Trim().ToLowerInvariant();
                else if (args[i].StartsWith("--tags=", StringComparison.OrdinalIgnoreCase)) tagMode = args[i].Substring(7).Trim().ToLowerInvariant();
                else if (args[i] == "--engine" && i + 1 < args.Length) { engine = NormalizeEngine(args[++i]); engineExplicit = true; }
                else if (args[i].StartsWith("--engine=", StringComparison.OrdinalIgnoreCase)) { engine = NormalizeEngine(args[i].Substring(9)); engineExplicit = true; }
                else if (args[i] == "--model" && i + 1 < args.Length) modelOverride = args[++i].Trim();
                else if (args[i].StartsWith("--model=", StringComparison.OrdinalIgnoreCase)) modelOverride = args[i].Substring(8).Trim();
                else if (args[i] == "--api-key" && i + 1 < args.Length) apiKey = args[++i];
                else if (args[i].StartsWith("--api-key=", StringComparison.OrdinalIgnoreCase)) apiKey = args[i].Substring(10);
                else if (args[i] == "--debug") debug = true;
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("AIBRIDGE_API_KEY");
            }

            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(10);
            if (!string.IsNullOrEmpty(apiKey))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            modelId = FetchModelId();
            if (!engineExplicit && modelId.IndexOf("Hy-MT2", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                engine = "hymt2";
            }

            Log("AI Bridge Translation Adapter");
            Log("  Engine   : " + engine + (engine == "hymt2" ? " (Hy-MT2 full-name prompt)" : " (TranslateGemma chat_template_kwargs)"));
            Log("  Listen   : http://127.0.0.1:" + listenPort + "/v1/");
            Log("  Upstream : " + upstream);
            Log("  Model    : " + modelId);
            Log("  Auth     : " + (string.IsNullOrEmpty(apiKey) ? "none" : "Bearer configured"));
            Log("  Tags     : " + tagMode + " (quality = keep wording, preserve = keep inline tags)");
            Log("  Log file : " + Path.Combine(baseDir, "bridge.log"));
            Log("Ready. Press Ctrl+C to stop.");

            TcpListener listener = new TcpListener(IPAddress.Loopback, listenPort);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                Log("ERROR cannot listen on 127.0.0.1:" + listenPort + " - " + ex.Message);
                Log("Close the program using this port or start with: AIBridgeAdapter.exe --port 9933");
                return;
            }
            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
        }

        private static string FetchModelId()
        {
            if (!string.IsNullOrEmpty(modelOverride)) return modelOverride;
            try
            {
                string body;
                using (HttpClient probe = new HttpClient())
                {
                    probe.Timeout = TimeSpan.FromSeconds(3);
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        probe.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    }
                    body = probe.GetStringAsync(upstream + "/v1/models").Result;
                }
                Dictionary<string, object> root = NewSerializer().DeserializeObject(body) as Dictionary<string, object>;
                if (root != null && root.ContainsKey("data"))
                {
                    object[] data = root["data"] as object[];
                    if (data != null && data.Length > 0)
                    {
                        Dictionary<string, object> first = data[0] as Dictionary<string, object>;
                        if (first != null && first.ContainsKey("id")) return Convert.ToString(first["id"]);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("WARN could not read upstream model list: " + ex.Message);
            }
            return "translategemma-4b-it";
        }

        private static void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;
            NetworkStream stream = null;
            try
            {
                client.ReceiveTimeout = 600000;
                client.SendTimeout = 600000;
                using (client)
                {
                    stream = client.GetStream();
                    string method, path;
                    Dictionary<string, string> headers;
                    byte[] body;
                    if (!ReadHttpRequest(stream, out method, out path, out headers, out body)) return;

                    string bodyText = body.Length > 0 ? Encoding.UTF8.GetString(body) : "";
                    if (debug) DebugLog("REQUEST " + method + " " + path + Environment.NewLine + bodyText);

                    if (method == "GET" && (path == "/" || path == "/health"))
                    {
                        WriteJson(stream, 200, "{\"status\":\"ok\",\"upstream\":" + JsonEscape(upstream) + "}");
                    }
                    else if (method == "GET" && (path == "/metrics" || path == "/status"))
                    {
                        WriteJson(stream, 200, Metrics.ToJson(listenPort, engine, modelId));
                    }
                    else if (method == "GET" && path.StartsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
                    {
                        ProxyModels(stream);
                    }
                    else if (method == "POST" && path.StartsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                    {
                        ProcessChat(stream, bodyText);
                    }
                    else
                    {
                        WriteError(stream, 404, "Not found: " + method + " " + path + ". This bridge supports /v1/models and /v1/chat/completions.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("ERROR " + ex);
                if (stream != null)
                {
                    try { WriteError(stream, 500, ex.Message); } catch { }
                }
            }
        }

        private static void ProxyModels(NetworkStream stream)
        {
            string payload = "{\"object\":\"list\",\"data\":[{\"id\":\"" + JsonEscape(modelId) +
                             "\",\"object\":\"model\",\"owned_by\":\"local\"}]}";
            WriteJson(stream, 200, payload);
        }

        private static void ProcessChat(NetworkStream stream, string bodyText)
        {
            JavaScriptSerializer ser = NewSerializer();
            Dictionary<string, object> root;
            try
            {
                root = ser.DeserializeObject(bodyText) as Dictionary<string, object>;
            }
            catch
            {
                WriteError(stream, 400, "Invalid JSON request body.");
                return;
            }
            if (root == null)
            {
                WriteError(stream, 400, "Invalid JSON request body.");
                return;
            }

            bool streamRequested = GetBool(root, "stream");
            string[] contents = GetMessageTexts(root);
            string allText = string.Join("\n", contents);

            if (allText.IndexOf("I'm testing if this connection is valid", StringComparison.OrdinalIgnoreCase) >= 0
                || allText.IndexOf("This connection is running", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SendChat(stream, "This connection is running", streamRequested);
                return;
            }

            string instruction = FindInstructionText(contents);
            if (instruction == null) instruction = allText;

            if (instruction.IndexOf("Refine the [TRANSLATION]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                WriteError(stream, 400, "Refinement mode is not supported by this adapter. Use Translation mode instead.");
                return;
            }

            string sourceLang, targetLang;
            if (!TryGetLanguagePair(instruction, allText, out sourceLang, out targetLang))
            {
                WriteError(stream, 400, "Could not detect the source/target language pair in the request. Expected text such as: Translate the [SOURCE] from zh-CN to ja-JP.");
                return;
            }

            string context = ExtractSection(instruction, "CONTEXT");
            string userPrompt = ExtractSection(instruction, "USER_PROMPT");

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                if (Regex.IsMatch(instruction, @"(?m)^\s*\[SEGMENTS\]\s*\r?$"))
                {
                    List<Segment> segments = ParseSegments(instruction);
                    if (segments.Count == 0)
                    {
                        WriteError(stream, 400, "No segments found in [SEGMENTS].");
                        return;
                    }

                    string globalTerms = null;
                    int segmentsIndex = instruction.IndexOf("[SEGMENTS]", StringComparison.OrdinalIgnoreCase);
                    if (segmentsIndex > 0)
                    {
                        globalTerms = ExtractSection(instruction.Substring(0, segmentsIndex), "TERMS");
                    }

                    List<object> units = new List<object>();
                    foreach (Segment seg in segments)
                    {
                        string terms = string.IsNullOrEmpty(seg.Terms) ? globalTerms : seg.Terms;
                        TranslationResult result = Translate(seg.Text, sourceLang, targetLang, terms, context, userPrompt);
                        Dictionary<string, object> unit = new Dictionary<string, object>();
                        unit["Id"] = seg.Id;
                        unit["Translation"] = result.Text;
                        unit["Comment"] = result.Comment;
                        units.Add(unit);
                    }

                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["TransUnits"] = units;
                    Log("OK   batch segments=" + segments.Count + " " + sourceLang + "->" + targetLang + " " + sw.ElapsedMilliseconds + "ms");
                    SendChat(stream, ser.Serialize(payload), streamRequested);
                }
                else
                {
                    string source = ExtractSingleSource(instruction);
                    if (source == null || source.Trim().Length == 0)
                    {
                        WriteError(stream, 400, "No [SOURCE] block found in the request.");
                        return;
                    }

                    string singleTerms = ExtractSection(instruction, "TERMS");
                    TranslationResult result = Translate(source.Trim(), sourceLang, targetLang, singleTerms, context, userPrompt);
                    Dictionary<string, object> unit = new Dictionary<string, object>();
                    unit["Translation"] = result.Text;
                    unit["Comment"] = result.Comment;

                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["TransUnits"] = new List<object> { unit };
                    Log("OK   single chars=" + source.Length + " " + sourceLang + "->" + targetLang + " " + sw.ElapsedMilliseconds + "ms");
                    SendChat(stream, ser.Serialize(payload), streamRequested);
                }
            }
            catch (Exception ex)
            {
                Log("FAIL " + ex);
                WriteError(stream, 502, "Upstream translation error: " + ex.Message);
            }
        }

        private static TranslationResult Translate(string text, string sourceLang, string targetLang, string terms, string context, string userPrompt)
        {
            if (text == null || text.Length == 0) return new TranslationResult("", "");

            Dictionary<int, TokenInfo> map = new Dictionary<int, TokenInfo>();
            List<int> criticalSequence;
            string masked = ProtectCritical(text, map, out criticalSequence);
            bool hasTags = TagRegex.IsMatch(masked);

            if (criticalSequence.Count == 0 && !hasTags)
            {
                return new TranslationResult(CallModel(text, sourceLang, targetLang, terms, context, userPrompt), "");
            }

            // Attempt A: keep inline tags untouched; only locked content, variables and links are masked.
            string rawA = CallModel(masked, sourceLang, targetLang, terms, context, userPrompt);
            string normA = NormalizeTokens(rawA, map);
            string problem;
            if (!ValidateTokenSequence(normA, criticalSequence, map, out problem))
            {
                Log("WARN attempt A lost critical markup: " + problem);
                return TranslateByRuns(text, sourceLang, targetLang, terms, context, userPrompt,
                    "Locked content or placeholders required segment-wise translation.");
            }

            if (TagsMatch(masked, normA, out problem))
            {
                string restoredA = CleanupSpacesAroundTags(RestoreTokens(normA, map)).Trim();
                return new TranslationResult(restoredA, "");
            }
            Log("WARN attempt A tag mismatch: " + problem);

            // Attempt B (opt-in): mask every inline tag with a sentinel token.
            if (tagMode == "preserve")
            {
                List<int> tagSequence;
                string maskedB = MaskRegularTags(masked, map, out tagSequence);
                string rawB = CallModel(maskedB, sourceLang, targetLang, terms, context, userPrompt);
                string normB = NormalizeTokens(rawB, map);
                List<int> combinedSequence = GetTokenSequence(maskedB);
                if (ValidateTokenSequence(normB, combinedSequence, map, out problem))
                {
                    string restoredB = CleanupSpacesAroundTags(RestoreTokens(normB, map)).Trim();
                    return new TranslationResult(restoredB, "");
                }
                Log("WARN attempt B lost markup: " + problem);
            }

            // Attempt C: translate the plain text; marked-up content is restored exactly.
            string maskedC = TagRegex.Replace(masked, " ");
            string rawC = CallModel(maskedC, sourceLang, targetLang, terms, context, userPrompt);
            string normC = NormalizeTokens(rawC, map);
            if (ValidateTokenSequence(normC, criticalSequence, map, out problem))
            {
                string restoredC = CleanupSpacesAroundTags(RestoreTokens(normC, map)).Trim();
                restoredC = Regex.Replace(restoredC, @"([\u3001\u3002\uFF0C\uFF0E\uFF1A\uFF1B\uFF01\uFF1F])[ \t]+", "$1");
                restoredC = Regex.Replace(restoredC, @"[ \t]+([\u3001\u3002\uFF0C\uFF0E\uFF1A\uFF1B\uFF01\uFF1F])", "$1");
                restoredC = Regex.Replace(restoredC, @"[ \t]{2,}", " ");
                Log("WARN attempt C dropped inline tags for one segment.");
                return new TranslationResult(restoredC,
                    "Inline tags could not be preserved by the model; the translation was produced without them.");
            }
            Log("WARN attempt C lost critical markup: " + problem);

            return TranslateByRuns(text, sourceLang, targetLang, terms, context, userPrompt,
                "Markup required segment-wise translation; wording may be less fluent.");
        }

        private static string CallModel(string text, string sourceLang, string targetLang, string terms, string context, string userPrompt)
        {
            JavaScriptSerializer ser = NewSerializer();
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["model"] = modelId;

            int maxTokens;
            if (engine == "hymt2")
            {
                string prompt = BuildHyMT2Prompt(text, sourceLang, targetLang, terms, context);
                payload["messages"] = new object[]
                {
                    new Dictionary<string, object> { { "role", "system" }, { "content", BuildHyMT2SystemPrompt(userPrompt) } },
                    new Dictionary<string, object> { { "role", "user" }, { "content", prompt } }
                };
                payload["temperature"] = 0.7;
                payload["top_p"] = 0.6;
                payload["top_k"] = 20;
                payload["repeat_penalty"] = 1.05;
                maxTokens = Math.Min(4096, Math.Max(512, text.Length * 4 + 256));
            }
            else
            {
                payload["messages"] = new object[]
                {
                    new Dictionary<string, object> { { "role", "user" }, { "content", text } }
                };
                payload["chat_template_kwargs"] = new Dictionary<string, object>
                {
                    { "source_lang_code", sourceLang },
                    { "target_lang_code", targetLang }
                };
                payload["temperature"] = 0;
                maxTokens = Math.Min(8192, Math.Max(512, text.Length * 4 + 256));
            }
            payload["max_tokens"] = maxTokens;
            payload["stream"] = true;
            payload["stream_options"] = new Dictionary<string, object>
            {
                { "include_usage", true }
            };

            string json = ser.Serialize(payload);
            if (debug) DebugLog("UPSTREAM REQUEST " + json);

            Metrics.BeginRequest();
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool metricsClosed = false;
            try
            {
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, upstream + "/v1/chat/completions"))
                using (StringContent content = new StringContent(json, Encoding.UTF8, "application/json"))
                {
                    request.Content = content;
                    HttpResponseMessage response = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).Result;
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = response.Content.ReadAsStringAsync().Result;
                        throw new InvalidOperationException("HTTP " + (int)response.StatusCode + " " + Trim(errorBody, 500));
                    }

                    StringBuilder translated = new StringBuilder();
                    long promptTokens = 0;
                    long completionTokens = 0;
                    long chunkCount = 0;
                    double reportedTps = 0;
                    string finishReason = "";
                    bool sawData = false;
                    bool usagePromptSeen = false;
                    bool usageCompletionSeen = false;

                    using (Stream responseStream = response.Content.ReadAsStreamAsync().Result)
                    using (StreamReader reader = new StreamReader(responseStream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Length == 0) continue;
                            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                            string data = line.Substring(5).Trim();
                            if (data.Length == 0) continue;
                            if (data == "[DONE]") break;

                            Dictionary<string, object> chunk = null;
                            try { chunk = ser.DeserializeObject(data) as Dictionary<string, object>; }
                            catch { continue; }
                            if (chunk == null) continue;
                            sawData = true;

                            if (chunk.ContainsKey("usage") && chunk["usage"] != null)
                            {
                                Dictionary<string, object> usage = chunk["usage"] as Dictionary<string, object>;
                                if (usage != null)
                                {
                                    if (usage.ContainsKey("prompt_tokens"))
                                    {
                                        long.TryParse(Convert.ToString(usage["prompt_tokens"]), out promptTokens);
                                        usagePromptSeen = true;
                                    }
                                    if (usage.ContainsKey("completion_tokens"))
                                    {
                                        long.TryParse(Convert.ToString(usage["completion_tokens"]), out completionTokens);
                                        usageCompletionSeen = true;
                                    }
                                }
                            }

                            if (chunk.ContainsKey("timings") && chunk["timings"] != null)
                            {
                                Dictionary<string, object> timings = chunk["timings"] as Dictionary<string, object>;
                                if (timings != null)
                                {
                                    if (timings.ContainsKey("predicted_per_second"))
                                    {
                                        double.TryParse(Convert.ToString(timings["predicted_per_second"]),
                                            NumberStyles.Float, CultureInfo.InvariantCulture, out reportedTps);
                                    }
                                    long value;
                                    if (!usagePromptSeen && timings.ContainsKey("prompt_n")
                                        && long.TryParse(Convert.ToString(timings["prompt_n"]), out value)) promptTokens = value;
                                    if (!usageCompletionSeen && timings.ContainsKey("predicted_n")
                                        && long.TryParse(Convert.ToString(timings["predicted_n"]), out value)) completionTokens = value;
                                }
                            }

                            object[] choices = chunk.ContainsKey("choices") ? chunk["choices"] as object[] : null;
                            if (choices == null || choices.Length == 0) continue;
                            Dictionary<string, object> choice = choices[0] as Dictionary<string, object>;
                            if (choice == null) continue;

                            if (choice.ContainsKey("finish_reason") && choice["finish_reason"] != null)
                            {
                                finishReason = Convert.ToString(choice["finish_reason"]);
                            }

                            Dictionary<string, object> delta = choice.ContainsKey("delta")
                                ? choice["delta"] as Dictionary<string, object> : null;
                            if (delta != null && delta.ContainsKey("content") && delta["content"] != null)
                            {
                                string piece = Convert.ToString(delta["content"]);
                                if (piece.Length > 0)
                                {
                                    translated.Append(piece);
                                    chunkCount++;
                                    Metrics.AddToken();
                                }
                            }
                            else
                            {
                                Dictionary<string, object> message = choice.ContainsKey("message")
                                    ? choice["message"] as Dictionary<string, object> : null;
                                if (message != null && message.ContainsKey("content") && message["content"] != null)
                                {
                                    string piece = Convert.ToString(message["content"]);
                                    if (piece.Length > 0)
                                    {
                                        translated.Append(piece);
                                        chunkCount++;
                                        Metrics.AddToken();
                                    }
                                }
                            }
                        }
                    }

                    if (!sawData || translated.Length == 0)
                    {
                        throw new InvalidOperationException("Upstream returned no streaming translation text.");
                    }

                    if (completionTokens <= 0) completionTokens = chunkCount;
                    double seconds = stopwatch.Elapsed.TotalSeconds;
                    double tps = reportedTps > 0
                        ? reportedTps
                        : (seconds > 0 ? completionTokens / seconds : 0);
                    Metrics.EndRequest(true, promptTokens, completionTokens, seconds, tps, "");
                    metricsClosed = true;

                    if (debug)
                    {
                        DebugLog("UPSTREAM STREAM tokens=" + completionTokens + " tps=" + Math.Round(tps, 2)
                                 + " text=" + Trim(translated.ToString(), 2000));
                    }
                    if (finishReason == "length") Log("WARN upstream hit max_tokens (" + maxTokens + ").");
                    return translated.ToString().Trim();
                }
            }
            catch (Exception ex)
            {
                if (!metricsClosed)
                {
                    Metrics.EndRequest(false, 0, 0, stopwatch.Elapsed.TotalSeconds, 0, ex.Message);
                }
                throw;
            }
        }

        private static string ProtectCritical(string text, Dictionary<int, TokenInfo> map, out List<int> sequence)
        {
            map.Clear();
            List<int> localSequence = new List<int>();
            int index = 0;
            string masked = CriticalMarkupRegex.Replace(text, delegate(Match match)
            {
                int id = index++;
                TokenInfo info = NewTokenInfo(id, match, text, true);
                map[id] = info;
                localSequence.Add(id);
                return (info.LeftSpace ? "" : " ") + TokenName(id) + (info.RightSpace ? "" : " ");
            });
            sequence = localSequence;
            return masked;
        }

        private static string MaskRegularTags(string text, Dictionary<int, TokenInfo> map, out List<int> sequence)
        {
            int next = 0;
            foreach (int id in map.Keys) if (id >= next) next = id + 1;
            List<int> localSequence = new List<int>();
            string masked = TagRegex.Replace(text, delegate(Match match)
            {
                int id = next++;
                TokenInfo info = NewTokenInfo(id, match, text, false);
                map[id] = info;
                localSequence.Add(id);
                return (info.LeftSpace ? "" : " ") + TokenName(id) + (info.RightSpace ? "" : " ");
            });
            sequence = localSequence;
            return masked;
        }

        private static TokenInfo NewTokenInfo(int id, Match match, string text, bool critical)
        {
            TokenInfo info = new TokenInfo();
            info.Id = id;
            info.Value = match.Value;
            info.Critical = critical;

            int before = match.Index - 1;
            while (before >= 0 && (text[before] == ' ' || text[before] == '\t')) before--;
            info.LeftChar = before >= 0 ? text[before] : '\0';
            info.LeftSpace = match.Index == 0 || text[match.Index - 1] == ' ' || text[match.Index - 1] == '\t';

            int after = match.Index + match.Length;
            while (after < text.Length && (text[after] == ' ' || text[after] == '\t')) after++;
            info.RightChar = after < text.Length ? text[after] : '\0';
            info.RightSpace = match.Index + match.Length >= text.Length
                || text[match.Index + match.Length] == ' ' || text[match.Index + match.Length] == '\t';

            info.LeftBracket = IsBracketLike(info.LeftChar);
            info.RightBracket = IsBracketLike(info.RightChar);
            return info;
        }

        private static bool IsBracketLike(char c)
        {
            return c == '"' || c == '\'' || c == '“' || c == '”' || c == '「' || c == '」'
                || c == '『' || c == '』' || c == '(' || c == ')' || c == '（' || c == '）'
                || c == '[' || c == ']' || c == '【' || c == '】' || c == '{' || c == '}';
        }

        private static string TokenName(int id)
        {
            return "XQ" + id.ToString("D4");
        }

        private static string NormalizeTokens(string text, Dictionary<int, TokenInfo> map)
        {
            string normalized = TokenLikeRegex.Replace(text ?? "", delegate(Match match)
            {
                StringBuilder sb = new StringBuilder("XQ");
                foreach (char c in match.Value)
                {
                    if (c >= '0' && c <= '9') sb.Append(c);
                    else if (c >= '\uFF10' && c <= '\uFF19') sb.Append((char)('0' + (c - '\uFF10')));
                }
                return sb.ToString();
            });
            normalized = StripDecoration(normalized, PairedDecorationRegex, map, true, true);
            normalized = StripDecoration(normalized, LeadingDecorationRegex, map, true, false);
            normalized = StripDecoration(normalized, TrailingDecorationRegex, map, false, true);
            return normalized;
        }

        private static string StripDecoration(string text, Regex regex, Dictionary<int, TokenInfo> map, bool hasLead, bool hasTrail)
        {
            return regex.Replace(text, delegate(Match match)
            {
                string token = match.Groups["tok"].Value;
                TokenInfo info = null;
                int id;
                if (int.TryParse(token.Substring(2), out id)) map.TryGetValue(id, out info);
                string lead = "";
                string trail = "";
                if (hasLead && match.Groups["lead"].Success && (info == null || info.LeftBracket))
                {
                    lead = match.Groups["lead"].Value;
                }
                if (hasTrail && match.Groups["trail"].Success && (info == null || info.RightBracket))
                {
                    trail = match.Groups["trail"].Value;
                }
                return lead + token + trail;
            });
        }

        private static List<int> GetTokenSequence(string text)
        {
            List<int> sequence = new List<int>();
            foreach (Match match in TokenRegex.Matches(text))
            {
                sequence.Add(int.Parse(match.Groups[1].Value));
            }
            return sequence;
        }

        private static bool ValidateTokenSequence(string text, List<int> sequence, Dictionary<int, TokenInfo> map, out string problem)
        {
            problem = null;
            MatchCollection all = TokenRegex.Matches(text);
            if (all.Count != sequence.Count)
            {
                problem = "expected " + sequence.Count + " token(s), found " + all.Count;
                return false;
            }
            int lastPosition = -1;
            for (int i = 0; i < sequence.Count; i++)
            {
                string token = TokenName(sequence[i]);
                Match found = null;
                foreach (Match match in all)
                {
                    if (match.Value == token) { found = match; break; }
                }
                if (found == null)
                {
                    problem = "missing " + DescribeToken(sequence[i], map);
                    return false;
                }
                if (found.Index <= lastPosition)
                {
                    problem = "out of order: " + DescribeToken(sequence[i], map);
                    return false;
                }
                lastPosition = found.Index;
            }
            return true;
        }

        private static string DescribeToken(int id, Dictionary<int, TokenInfo> map)
        {
            TokenInfo info;
            if (map.TryGetValue(id, out info)) return info.Value;
            return TokenName(id);
        }

        private static bool TagsMatch(string sourceWithTokens, string outputWithTokens, out string problem)
        {
            MatchCollection expected = TagRegex.Matches(sourceWithTokens);
            MatchCollection actual = TagRegex.Matches(outputWithTokens);
            if (expected.Count != actual.Count)
            {
                problem = "expected " + expected.Count + " tag(s), found " + actual.Count;
                return false;
            }
            for (int i = 0; i < expected.Count; i++)
            {
                if (expected[i].Value != actual[i].Value)
                {
                    problem = expected[i].Value + " became " + actual[i].Value;
                    return false;
                }
            }
            problem = null;
            return true;
        }

        private static string RestoreTokens(string text, Dictionary<int, TokenInfo> map)
        {
            StringBuilder sb = new StringBuilder();
            int position = 0;
            bool trimLeading = false;
            foreach (Match match in TokenRegex.Matches(text))
            {
                string before = text.Substring(position, match.Index - position);
                if (trimLeading) before = before.TrimStart(' ', '\t');
                int id = int.Parse(match.Groups[1].Value);
                TokenInfo info;
                if (map.TryGetValue(id, out info))
                {
                    if (!info.LeftSpace) before = before.TrimEnd(' ', '\t');
                    sb.Append(before);
                    sb.Append(info.Value);
                    trimLeading = !info.RightSpace;
                }
                else
                {
                    sb.Append(before);
                    sb.Append(match.Value);
                    trimLeading = false;
                }
                position = match.Index + match.Length;
            }
            string tail = text.Substring(position);
            if (trimLeading) tail = tail.TrimStart(' ', '\t');
            sb.Append(tail);
            return sb.ToString();
        }

        private static string CleanupSpacesAroundTags(string text)
        {
            string result = Regex.Replace(text, @"([\u3000-\u303F\u4E00-\u9FFF\uFF01-\uFF60])[ \t]+(</?[A-Za-z][^<>]*?>)", "$1$2");
            result = Regex.Replace(result, @"(</?[A-Za-z][^<>]*?>)[ \t]+([\u3000-\u303F\u4E00-\u9FFF\uFF01-\uFF60])", "$1$2");
            return result;
        }

        private static TranslationResult TranslateByRuns(string text, string sourceLang, string targetLang, string terms, string context, string userPrompt, string comment)
        {
            Regex combined = new Regex(CriticalMarkupRegex.ToString() + "|" + TagRegex.ToString(), RegexOptions.IgnoreCase);
            StringBuilder sb = new StringBuilder();
            int position = 0;
            foreach (Match match in combined.Matches(text))
            {
                string run = text.Substring(position, match.Index - position);
                if (run.Trim().Length > 0) sb.Append(CallModel(run, sourceLang, targetLang, terms, context, userPrompt));
                else sb.Append(run);
                sb.Append(match.Value);
                position = match.Index + match.Length;
            }
            string tail = text.Substring(position);
            if (tail.Trim().Length > 0) sb.Append(CallModel(tail, sourceLang, targetLang, terms, context, userPrompt));
            else sb.Append(tail);
            return new TranslationResult(sb.ToString().Trim(), comment);
        }

        private static string[] GetMessageTexts(Dictionary<string, object> root)
        {
            List<string> result = new List<string>();
            object messagesObject;
            if (!root.TryGetValue("messages", out messagesObject)) return result.ToArray();
            object[] messages = messagesObject as object[];
            if (messages == null) return result.ToArray();

            foreach (object item in messages)
            {
                Dictionary<string, object> message = item as Dictionary<string, object>;
                if (message == null) continue;
                object content;
                if (!message.TryGetValue("content", out content)) continue;
                string text = ExtractContentText(content);
                if (!string.IsNullOrEmpty(text)) result.Add(text);
            }
            return result.ToArray();
        }

        private static string ExtractContentText(object content)
        {
            if (content == null) return null;
            string s = content as string;
            if (s != null) return s;

            object[] parts = content as object[];
            if (parts != null)
            {
                StringBuilder sb = new StringBuilder();
                foreach (object part in parts)
                {
                    string text = ExtractContentText(part);
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(text);
                    }
                }
                return sb.ToString();
            }

            Dictionary<string, object> dict = content as Dictionary<string, object>;
            if (dict != null)
            {
                if (dict.ContainsKey("text") && dict["text"] != null) return Convert.ToString(dict["text"]);
                if (dict.ContainsKey("content")) return ExtractContentText(dict["content"]);
            }
            return Convert.ToString(content);
        }

        private static string FindInstructionText(string[] contents)
        {
            for (int i = contents.Length - 1; i >= 0; i--)
            {
                if (contents[i].IndexOf("[SOURCE]", StringComparison.OrdinalIgnoreCase) >= 0
                    || contents[i].IndexOf("[SEGMENTS]", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return contents[i];
                }
            }
            return null;
        }

        private static bool TryGetLanguagePair(string instruction, string allText, out string sourceLang, out string targetLang)
        {
            sourceLang = null;
            targetLang = null;
            Regex re = new Regex(@"from\s+([A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{2,8})*)\s+to\s+([A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{2,8})*)", RegexOptions.IgnoreCase);
            Match match = re.Match(instruction ?? "");
            if (!match.Success) match = re.Match(allText ?? "");
            if (!match.Success) return false;
            sourceLang = NormalizeLanguageCode(match.Groups[1].Value);
            targetLang = NormalizeLanguageCode(match.Groups[2].Value);
            return true;
        }

        private static string NormalizeLanguageCode(string code)
        {
            string value = (code ?? "").Replace('_', '-').Trim();
            string lower = value.ToLowerInvariant();
            if (lower == "zh" || lower.StartsWith("zh-"))
            {
                if (lower.Contains("hant") || lower.Contains("-tw") || lower.Contains("-hk") || lower.Contains("-mo"))
                    return "zh-Hant";
                return "zh-Hans";
            }
            int dash = value.IndexOf('-');
            if (dash > 0) return value.Substring(0, dash);
            return value;
        }

        private static string NormalizeEngine(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            if (v == "hymt2" || v == "hy-mt2" || v == "hy_mt2" || v == "hunyuan") return "hymt2";
            return "gemma";
        }

        private static readonly Dictionary<string, string> HyMT2ChineseNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "zh-Hans", "中文" }, { "zh-Hant", "繁体中文" }, { "zh", "中文" },
            { "en", "英语" }, { "ja", "日语" }, { "ko", "韩语" }, { "fr", "法语" },
            { "pt", "葡萄牙语" }, { "es", "西班牙语" }, { "tr", "土耳其语" }, { "ru", "俄语" },
            { "ar", "阿拉伯语" }, { "th", "泰语" }, { "it", "意大利语" }, { "de", "德语" },
            { "vi", "越南语" }, { "ms", "马来语" }, { "id", "印尼语" }, { "tl", "菲律宾语" },
            { "hi", "印地语" }, { "pl", "波兰语" }, { "cs", "捷克语" }, { "nl", "荷兰语" },
            { "km", "高棉语" }, { "my", "缅甸语" }, { "fa", "波斯语" }, { "gu", "古吉拉特语" },
            { "ur", "乌尔都语" }, { "te", "泰卢固语" }, { "mr", "马拉地语" }, { "he", "希伯来语" },
            { "bn", "孟加拉语" }, { "ta", "泰米尔语" }, { "uk", "乌克兰语" }, { "bo", "藏语" },
            { "kk", "哈萨克语" }, { "mn", "蒙古语" }, { "ug", "维吾尔语" }, { "yue", "粤语" }
        };

        private static readonly Dictionary<string, string> HyMT2EnglishNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "zh-Hans", "Chinese" }, { "zh-Hant", "Traditional Chinese" }, { "zh", "Chinese" },
            { "en", "English" }, { "ja", "Japanese" }, { "ko", "Korean" }, { "fr", "French" },
            { "pt", "Portuguese" }, { "es", "Spanish" }, { "tr", "Turkish" }, { "ru", "Russian" },
            { "ar", "Arabic" }, { "th", "Thai" }, { "it", "Italian" }, { "de", "German" },
            { "vi", "Vietnamese" }, { "ms", "Malay" }, { "id", "Indonesian" }, { "tl", "Filipino" },
            { "hi", "Hindi" }, { "pl", "Polish" }, { "cs", "Czech" }, { "nl", "Dutch" },
            { "km", "Khmer" }, { "my", "Burmese" }, { "fa", "Persian" }, { "gu", "Gujarati" },
            { "ur", "Urdu" }, { "te", "Telugu" }, { "mr", "Marathi" }, { "he", "Hebrew" },
            { "bn", "Bengali" }, { "ta", "Tamil" }, { "uk", "Ukrainian" }, { "bo", "Tibetan" },
            { "kk", "Kazakh" }, { "mn", "Mongolian" }, { "ug", "Uyghur" }, { "yue", "Cantonese" }
        };

        private static bool IsChineseLanguage(string code)
        {
            string normalized = NormalizeLanguageCode(code);
            return normalized == "zh-Hans" || normalized == "zh-Hant" || normalized == "zh" || normalized == "yue";
        }

        private static string HyMT2LanguageName(string code, bool chinese)
        {
            string normalized = NormalizeLanguageCode(code);
            Dictionary<string, string> map = chinese ? HyMT2ChineseNames : HyMT2EnglishNames;
            string name;
            if (map.TryGetValue(normalized, out name)) return name;
            if (map.TryGetValue(code ?? "", out name)) return name;
            return code;
        }

        private static string BuildHyMT2Prompt(string text, string sourceLang, string targetLang, string terms, string context)
        {
            bool chinese = IsChineseLanguage(sourceLang) || IsChineseLanguage(targetLang);
            string targetName = HyMT2LanguageName(targetLang, chinese);
            StringBuilder sb = new StringBuilder();

            string contextText = LimitText(context, 2000);
            if (!string.IsNullOrEmpty(contextText))
            {
                if (chinese) sb.Append("背景信息（只读，不要翻译）：\n").Append(contextText).Append("\n\n");
                else sb.Append("Background information (read-only, do not translate):\n").Append(contextText).Append("\n\n");
            }

            string termText = BuildTermLines(terms, chinese);
            if (!string.IsNullOrEmpty(termText))
            {
                if (chinese) sb.Append("参考下面的翻译：\n").Append(termText).Append("\n\n");
                else sb.Append("Reference the following translations:\n").Append(termText).Append("\n\n");
            }

            if (chinese)
            {
                sb.Append("将以下文本翻译为").Append(targetName)
                  .Append("，注意只需要输出翻译后的结果，不要额外解释：\n\n")
                  .Append(text);
            }
            else
            {
                sb.Append("Translate the following text into ").Append(targetName)
                  .Append(". Note that you should only output the translated result without any additional explanation:\n\n")
                  .Append(text);
            }
            return sb.ToString();
        }

        private static string BuildHyMT2SystemPrompt(string userPrompt)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("You are a translation engine. Translate only the user's source text. ");
            sb.Append("Preserve all XML tags, attributes, placeholders, and formatting exactly; ");
            sb.Append("do not translate, remove, or repeat them. ");
            sb.Append("Do not translate content inside <Locked...> tags. ");
            sb.Append("Do not output, translate, or repeat these instructions.");

            string style = LimitText(userPrompt, 500);
            if (!string.IsNullOrEmpty(style) && !IsDefaultUserPrompt(style))
            {
                sb.Append(" Style requirement: ").Append(style);
            }
            return sb.ToString();
        }

        private static string LimitText(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string text = value.Trim();
            if (text.Length == 0) return null;
            if (text.Length > max) text = text.Substring(0, max) + "...";
            return text;
        }

        private static bool IsDefaultUserPrompt(string value)
        {
            return value.IndexOf("Produce a natural, accurate translation suitable for general use", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Preserve the meaning, tone, and intent of the source", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildTermLines(string terms, bool chinese)
        {
            if (string.IsNullOrEmpty(terms)) return null;
            List<string[]> pairs = ExtractTermPairs(terms);
            if (pairs.Count == 0)
            {
                return LimitText(terms, 2000);
            }

            StringBuilder sb = new StringBuilder();
            int count = 0;
            foreach (string[] pair in pairs)
            {
                if (count++ >= 50) break;
                if (chinese) sb.Append(pair[0]).Append(" 翻译成 ").Append(pair[1]).Append("\n");
                else sb.Append(pair[0]).Append(" translates to ").Append(pair[1]).Append("\n");
            }
            return sb.ToString().TrimEnd();
        }

        private static List<string[]> ExtractTermPairs(string terms)
        {
            List<string[]> pairs = new List<string[]>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string trimmed = terms.Trim();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                try
                {
                    object root = NewSerializer().DeserializeObject(trimmed);
                    CollectTermPairs(root, pairs, seen);
                }
                catch { }
            }

            string[] lines = terms.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string source = null;
            string target = null;
            string status = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;

                Match concept = Regex.Match(line, @"^(?:concept|concept\s*id|概念)\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase);
                if (concept.Success)
                {
                    FlushTerm(source, target, status, pairs, seen);
                    source = null; target = null; status = null;
                    continue;
                }

                Match src = Regex.Match(line, @"^(?:source|源词|原文)\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase);
                if (src.Success)
                {
                    if (source != null && target != null) FlushTerm(source, target, status, pairs, seen);
                    source = CleanTermValue(src.Groups[1].Value);
                    target = null; status = null;
                    continue;
                }

                Match tgt = Regex.Match(line, @"^(?:target|译文|目标词)\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase);
                if (tgt.Success)
                {
                    target = CleanTermValue(tgt.Groups[1].Value);
                    continue;
                }

                Match st = Regex.Match(line, @"^(?:status|状态)\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase);
                if (st.Success)
                {
                    status = CleanTermValue(st.Groups[1].Value);
                    continue;
                }

                Match arrow = Regex.Match(line, @"^[-*]?\s*(.+?)\s*(?:=>|->|→|\t|\s=\s)\s*(.+)$");
                if (arrow.Success)
                {
                    string a = CleanTermValue(arrow.Groups[1].Value);
                    string b = CleanTermValue(arrow.Groups[2].Value);
                    if (a.Length > 0 && b.Length > 0) AddTermPair(a, b, pairs, seen);
                }
            }
            FlushTerm(source, target, status, pairs, seen);
            return pairs;
        }

        private static void CollectTermPairs(object node, List<string[]> pairs, HashSet<string> seen)
        {
            Dictionary<string, object> dict = node as Dictionary<string, object>;
            if (dict != null)
            {
                string source = GetDictionaryValue(dict, "Source");
                string target = GetDictionaryValue(dict, "Target");
                string status = GetDictionaryValue(dict, "Status");
                bool forbidden = IsTrue(GetDictionaryValue(dict, "IsForbidden"));
                if (source != null && target != null && !forbidden && !IsForbiddenStatus(status))
                {
                    AddTermPair(CleanTermValue(source), CleanTermValue(target), pairs, seen);
                }
                foreach (object child in dict.Values) CollectTermPairs(child, pairs, seen);
                return;
            }
            object[] array = node as object[];
            if (array != null)
            {
                foreach (object child in array) CollectTermPairs(child, pairs, seen);
            }
        }

        private static string GetDictionaryValue(Dictionary<string, object> dict, string key)
        {
            foreach (KeyValuePair<string, object> pair in dict)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && pair.Value != null)
                    return Convert.ToString(pair.Value);
            }
            return null;
        }

        private static bool IsTrue(string value)
        {
            bool result;
            return value != null && bool.TryParse(value, out result) && result;
        }

        private static void FlushTerm(string source, string target, string status, List<string[]> pairs, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return;
            if (IsForbiddenStatus(status)) return;
            AddTermPair(source, target, pairs, seen);
        }

        private static bool IsForbiddenStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return false;
            return status.IndexOf("forbidden", StringComparison.OrdinalIgnoreCase) >= 0
                || status.IndexOf("deprecated", StringComparison.OrdinalIgnoreCase) >= 0
                || status.IndexOf("禁用", StringComparison.Ordinal) >= 0
                || status.IndexOf("弃用", StringComparison.Ordinal) >= 0;
        }

        private static string CleanTermValue(string value)
        {
            string result = (value ?? "").Trim();
            result = result.TrimEnd(',', ';', '，', '；');
            if (result.Length >= 2 && ((result[0] == '"' && result[result.Length - 1] == '"')
                || (result[0] == '\'' && result[result.Length - 1] == '\'')
                || (result[0] == '“' && result[result.Length - 1] == '”')))
            {
                result = result.Substring(1, result.Length - 2).Trim();
            }
            return result;
        }

        private static void AddTermPair(string source, string target, List<string[]> pairs, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return;
            if (source.Length > 200 || target.Length > 200) return;
            string key = source + "\0" + target;
            if (seen.Add(key)) pairs.Add(new string[] { source, target });
        }

        private static string ExtractSection(string text, string sectionName)
        {
            if (string.IsNullOrEmpty(text)) return null;
            Regex re = new Regex(
                @"(?ms)^[ \t]*\[" + Regex.Escape(sectionName) + @"\][ \t]*\r?\n(.*?)(?=^[ \t]*\[(?:SOURCE|SEGMENTS|USER_PROMPT|COMMENT_PROMPT|TERMS|CONTEXT|TRANSLATION|OUTPUT_SCHEMA)\][ \t]*\r?$|\z)",
                RegexOptions.IgnoreCase);
            Match match = re.Match(text);
            if (!match.Success) return null;
            string value = match.Groups[1].Value.Trim();
            return value.Length == 0 ? null : value;
        }

        private static string ExtractSingleSource(string text)
        {
            Regex re = new Regex(@"(?ms)^[ \t]*\[SOURCE\][ \t]*\r?\n(.*?)(?=^[ \t]*\[(?:USER_PROMPT|COMMENT_PROMPT|TERMS|CONTEXT|TRANSLATION|SEGMENTS|OUTPUT_SCHEMA)\][ \t]*\r?$|\z)");
            Match match = re.Match(text ?? "");
            if (!match.Success) return null;
            return match.Groups[1].Value.Trim();
        }

        private static List<Segment> ParseSegments(string text)
        {
            List<Segment> result = new List<Segment>();
            Regex blockRe = new Regex(@"(?ms)^[ \t]*\[SEGMENTS\][ \t]*\r?\n(.*?)(?=^[ \t]*\[(?:USER_PROMPT|COMMENT_PROMPT|CONTEXT|TRANSLATION|SOURCE|OUTPUT_SCHEMA)\][ \t]*\r?$|\z)");
            Match block = blockRe.Match(text ?? "");
            if (!block.Success) return result;

            string content = block.Groups[1].Value;
            Regex idRe = new Regex(@"(?m)^[ \t]*===[ \t]*id:[ \t]*(.+?)[ \t]*===[ \t]*\r?$");
            MatchCollection ids = idRe.Matches(content);
            for (int i = 0; i < ids.Count; i++)
            {
                int start = ids[i].Index + ids[i].Length;
                int end = (i + 1 < ids.Count) ? ids[i + 1].Index : content.Length;
                string segmentText = content.Substring(start, end - start);

                string termsBlock = null;
                Match terms = Regex.Match(segmentText, @"(?m)^[ \t]*\[TERMS\][ \t]*\r?$");
                if (terms.Success)
                {
                    termsBlock = segmentText.Substring(terms.Index + terms.Length).Trim();
                    segmentText = segmentText.Substring(0, terms.Index);
                }

                result.Add(new Segment(ids[i].Groups[1].Value.Trim(), segmentText.Trim(), termsBlock));
            }
            return result;
        }

        private static void SendChat(NetworkStream stream, string content, bool streamRequested)
        {
            string id = "chatcmpl-" + Guid.NewGuid().ToString("N");
            long created = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

            if (!streamRequested)
            {
                JavaScriptSerializer ser = NewSerializer();
                Dictionary<string, object> message = new Dictionary<string, object>();
                message["role"] = "assistant";
                message["content"] = content;
                Dictionary<string, object> choice = new Dictionary<string, object>();
                choice["index"] = 0;
                choice["message"] = message;
                choice["finish_reason"] = "stop";
                Dictionary<string, object> usage = new Dictionary<string, object>();
                usage["prompt_tokens"] = 0;
                usage["completion_tokens"] = 0;
                usage["total_tokens"] = 0;
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["id"] = id;
                root["object"] = "chat.completion";
                root["created"] = created;
                root["model"] = modelId;
                root["choices"] = new object[] { choice };
                root["usage"] = usage;
                WriteJson(stream, 200, ser.Serialize(root));
                return;
            }

            string head = "HTTP/1.1 200 OK\r\n" +
                          "Content-Type: text/event-stream; charset=utf-8\r\n" +
                          "Cache-Control: no-cache\r\n" +
                          "Connection: close\r\n\r\n";
            byte[] headBytes = Encoding.UTF8.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            WriteSse(stream, BuildChunk(id, created, "", "assistant", null));
            WriteSse(stream, BuildChunk(id, created, content, null, null));
            WriteSse(stream, BuildChunk(id, created, null, null, "stop"));
            byte[] done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            stream.Write(done, 0, done.Length);
            stream.Flush();
        }

        private static string BuildChunk(string id, long created, string content, string role, string finishReason)
        {
            JavaScriptSerializer ser = NewSerializer();
            Dictionary<string, object> delta = new Dictionary<string, object>();
            if (role != null) delta["role"] = role;
            if (content != null) delta["content"] = content;
            Dictionary<string, object> choice = new Dictionary<string, object>();
            choice["index"] = 0;
            choice["delta"] = delta;
            choice["finish_reason"] = finishReason;
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["id"] = id;
            root["object"] = "chat.completion.chunk";
            root["created"] = created;
            root["model"] = modelId;
            root["choices"] = new object[] { choice };
            return ser.Serialize(root);
        }

        private static void WriteSse(NetworkStream stream, string json)
        {
            byte[] data = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        private static void WriteJson(NetworkStream stream, int status, string json)
        {
            WriteRaw(stream, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }

        private static void WriteError(NetworkStream stream, int status, string message)
        {
            JavaScriptSerializer ser = NewSerializer();
            Dictionary<string, object> error = new Dictionary<string, object>();
            error["message"] = message;
            error["type"] = "bridge_error";
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["error"] = error;
            WriteJson(stream, status, ser.Serialize(root));
        }

        private static void WriteRaw(NetworkStream stream, int status, string contentType, byte[] body)
        {
            string head = "HTTP/1.1 " + status + " " + StatusText(status) + "\r\n" +
                          "Content-Type: " + contentType + "\r\n" +
                          "Content-Length: " + body.Length + "\r\n" +
                          "Connection: close\r\n" +
                          "Access-Control-Allow-Origin: *\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string StatusText(int status)
        {
            if (status == 200) return "OK";
            if (status == 400) return "Bad Request";
            if (status == 404) return "Not Found";
            if (status == 500) return "Internal Server Error";
            if (status == 502) return "Bad Gateway";
            return "OK";
        }

        private static bool ReadHttpRequest(NetworkStream stream, out string method, out string path, out Dictionary<string, string> headers, out byte[] body)
        {
            method = null;
            path = null;
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            body = new byte[0];

            byte[] buffer = new byte[16384];
            MemoryStream headerBuffer = new MemoryStream();
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return false;
                headerBuffer.Write(buffer, 0, read);
                byte[] current = headerBuffer.GetBuffer();
                int length = (int)headerBuffer.Length;
                for (int i = 0; i <= length - 4; i++)
                {
                    if (current[i] == 13 && current[i + 1] == 10 && current[i + 2] == 13 && current[i + 3] == 10)
                    {
                        headerEnd = i;
                        break;
                    }
                }
                if (headerBuffer.Length > 1024 * 1024) return false;
            }

            byte[] all = headerBuffer.ToArray();
            string headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
            int bodyStart = headerEnd + 4;
            int alreadyRead = all.Length - bodyStart;

            string[] lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return false;
            method = requestLine[0];
            path = requestLine[1];

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string key = lines[i].Substring(0, colon).Trim();
                string value = lines[i].Substring(colon + 1).Trim();
                headers[key] = value;
            }

            MemoryStream bodyBuffer = new MemoryStream();
            if (alreadyRead > 0) bodyBuffer.Write(all, bodyStart, alreadyRead);

            string transferEncoding;
            if (headers.TryGetValue("Transfer-Encoding", out transferEncoding)
                && transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                body = ReadChunkedBody(stream, bodyBuffer.ToArray());
                return true;
            }

            int contentLength = 0;
            string lengthHeader;
            if (headers.TryGetValue("Content-Length", out lengthHeader)) int.TryParse(lengthHeader, out contentLength);
            while (bodyBuffer.Length < contentLength)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                bodyBuffer.Write(buffer, 0, read);
            }
            byte[] full = bodyBuffer.ToArray();
            int take = Math.Min(full.Length, contentLength);
            body = new byte[take];
            Array.Copy(full, body, take);
            return true;
        }

        private static byte[] ReadChunkedBody(NetworkStream stream, byte[] prefix)
        {
            MemoryStream input = new MemoryStream(prefix);
            MemoryStream output = new MemoryStream();
            while (true)
            {
                string sizeLine = ReadLine(input, stream);
                if (sizeLine == null) break;
                int semicolon = sizeLine.IndexOf(';');
                if (semicolon >= 0) sizeLine = sizeLine.Substring(0, semicolon);
                int size;
                if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out size)) break;
                if (size == 0)
                {
                    while (true)
                    {
                        string trailer = ReadLine(input, stream);
                        if (string.IsNullOrEmpty(trailer)) break;
                    }
                    break;
                }
                byte[] chunk = new byte[size];
                int offset = 0;
                while (offset < size)
                {
                    int read = ReadFrom(input, stream, chunk, offset, size - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                output.Write(chunk, 0, offset);
                ReadLine(input, stream);
            }
            return output.ToArray();
        }

        private static string ReadLine(MemoryStream input, NetworkStream stream)
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                int b = ReadByteFrom(input, stream);
                if (b < 0) return sb.Length > 0 ? sb.ToString() : null;
                if (b == 13)
                {
                    int next = ReadByteFrom(input, stream);
                    if (next == 10) return sb.ToString();
                    if (next >= 0) sb.Append((char)next);
                    continue;
                }
                sb.Append((char)b);
            }
        }

        private static int ReadByteFrom(MemoryStream input, NetworkStream stream)
        {
            if (input.Position < input.Length) return input.ReadByte();
            return stream.ReadByte();
        }

        private static int ReadFrom(MemoryStream input, NetworkStream stream, byte[] buffer, int offset, int count)
        {
            int available = (int)(input.Length - input.Position);
            if (available > 0)
            {
                int take = Math.Min(available, count);
                return input.Read(buffer, offset, take);
            }
            return stream.Read(buffer, offset, count);
        }

        private static bool GetBool(Dictionary<string, object> dictionary, string key)
        {
            object value;
            if (!dictionary.TryGetValue(key, out value) || value == null) return false;
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value), out parsed) && parsed;
        }

        private static JavaScriptSerializer NewSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer;
        }

        private static string JsonEscape(string value)
        {
            return NewSerializer().Serialize(value ?? "");
        }

        private static string Trim(string value, int max)
        {
            if (value == null) return "";
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message;
            try { Console.WriteLine(line); } catch { }
            try
            {
                lock (logLock) File.AppendAllText(Path.Combine(baseDir, "bridge.log"), line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private static void DebugLog(string message)
        {
            try
            {
                lock (logLock) File.AppendAllText(Path.Combine(baseDir, "bridge-debug.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + Environment.NewLine + message + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
