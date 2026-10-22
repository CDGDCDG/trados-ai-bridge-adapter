using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TradosAIBridgeAdapterGui
{
    internal sealed class GuiSettings
    {
        public string Upstream = "http://127.0.0.1:9931";
        public int Port = 9932;
        public string Model = "";
        public int EngineIndex;
        public int TagsIndex;
        public bool UseApiKey;
        public string ApiKeyProtected = "";
    }

    internal sealed class MainForm : Form
    {
        private readonly string baseDir;
        private readonly string settingsPath;
        private readonly string adapterPath;

        private TextBox txtUpstream;
        private NumericUpDown numPort;
        private ComboBox cboModel;
        private Button btnRefresh;
        private CheckBox chkApiKey;
        private TextBox txtApiKey;
        private ComboBox cboEngine;
        private ComboBox cboTags;
        private TextBox txtEndpoint;
        private TextBox txtModelsPath;
        private Label lblStatus;
        private TextBox txtLog;
        private Button btnStart;
        private Button btnStop;
        private Button btnOpenLog;
        private Process adapterProcess;
        private bool closing;

        public MainForm()
        {
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            settingsPath = Path.Combine(baseDir, "gui-settings.json");
            adapterPath = Path.Combine(baseDir, "AIBridgeAdapter.exe");

            Text = "Trados AI Bridge Adapter - 控制台";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 640);
            Size = new Size(800, 700);
            AutoScaleMode = AutoScaleMode.Font;

            BuildUi();
            LoadSettings();
            UpdateEndpointLabels();
            UpdateButtons();
            FormClosing += MainForm_FormClosing;
            Shown += delegate { BeginInvoke(new Action(RefreshModels)); };
        }

        private void BuildUi()
        {
            GroupBox grpUpstream = new GroupBox();
            grpUpstream.Text = "上游模型服务";
            grpUpstream.Location = new Point(12, 12);
            grpUpstream.Size = new Size(760, 210);
            grpUpstream.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(grpUpstream);

            Label lblUpstream = new Label();
            lblUpstream.Text = "API 地址";
            lblUpstream.Location = new Point(15, 32);
            lblUpstream.AutoSize = true;
            grpUpstream.Controls.Add(lblUpstream);

            txtUpstream = new TextBox();
            txtUpstream.Text = "http://127.0.0.1:9931";
            txtUpstream.Location = new Point(190, 29);
            txtUpstream.Size = new Size(540, 25);
            txtUpstream.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtUpstream.TextChanged += delegate { UpdateEndpointLabels(); };
            grpUpstream.Controls.Add(txtUpstream);

            Label lblUpstreamHint = new Label();
            lblUpstreamHint.Text = "可填 http://127.0.0.1:9931 或 http://127.0.0.1:1234/v1";
            lblUpstreamHint.ForeColor = Color.Gray;
            lblUpstreamHint.Location = new Point(190, 54);
            lblUpstreamHint.AutoSize = true;
            grpUpstream.Controls.Add(lblUpstreamHint);

            Label lblPort = new Label();
            lblPort.Text = "监听端口";
            lblPort.Location = new Point(15, 82);
            lblPort.AutoSize = true;
            grpUpstream.Controls.Add(lblPort);

            numPort = new NumericUpDown();
            numPort.Minimum = 1;
            numPort.Maximum = 65535;
            numPort.Value = 9932;
            numPort.Location = new Point(190, 79);
            numPort.Size = new Size(100, 25);
            numPort.ValueChanged += delegate { UpdateEndpointLabels(); };
            grpUpstream.Controls.Add(numPort);

            Label lblModel = new Label();
            lblModel.Text = "模型";
            lblModel.Location = new Point(315, 82);
            lblModel.AutoSize = true;
            grpUpstream.Controls.Add(lblModel);

            cboModel = new ComboBox();
            cboModel.DropDownStyle = ComboBoxStyle.DropDown;
            cboModel.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            cboModel.AutoCompleteSource = AutoCompleteSource.ListItems;
            cboModel.Location = new Point(360, 79);
            cboModel.Size = new Size(290, 25);
            cboModel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            grpUpstream.Controls.Add(cboModel);

            btnRefresh = new Button();
            btnRefresh.Text = "刷新模型";
            btnRefresh.Location = new Point(660, 78);
            btnRefresh.Size = new Size(85, 27);
            btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRefresh.Click += delegate { RefreshModels(); };
            grpUpstream.Controls.Add(btnRefresh);

            chkApiKey = new CheckBox();
            chkApiKey.Text = "使用 Authorization Bearer API Key";
            chkApiKey.Location = new Point(15, 118);
            chkApiKey.AutoSize = true;
            chkApiKey.CheckedChanged += delegate
            {
                txtApiKey.Enabled = chkApiKey.Checked;
                UpdateButtons();
            };
            grpUpstream.Controls.Add(chkApiKey);

            txtApiKey = new TextBox();
            txtApiKey.Location = new Point(260, 116);
            txtApiKey.Size = new Size(470, 25);
            txtApiKey.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtApiKey.UseSystemPasswordChar = true;
            txtApiKey.Enabled = false;
            grpUpstream.Controls.Add(txtApiKey);

            Label lblApiHint = new Label();
            lblApiHint.Text = "仅用于上游 llama-server / LM Studio；AI Bridge 连接里的 API Key 不会转发。";
            lblApiHint.ForeColor = Color.Gray;
            lblApiHint.Location = new Point(260, 141);
            lblApiHint.AutoSize = true;
            grpUpstream.Controls.Add(lblApiHint);

            Label lblEngine = new Label();
            lblEngine.Text = "引擎";
            lblEngine.Location = new Point(15, 175);
            lblEngine.AutoSize = true;
            grpUpstream.Controls.Add(lblEngine);

            cboEngine = new ComboBox();
            cboEngine.DropDownStyle = ComboBoxStyle.DropDownList;
            cboEngine.Items.AddRange(new object[] { "自动识别（按模型名）", "TranslateGemma", "Hy-MT2" });
            cboEngine.SelectedIndex = 0;
            cboEngine.Location = new Point(70, 172);
            cboEngine.Size = new Size(230, 25);
            cboEngine.SelectedIndexChanged += delegate { UpdateButtons(); };
            grpUpstream.Controls.Add(cboEngine);

            Label lblTags = new Label();
            lblTags.Text = "标签模式";
            lblTags.Location = new Point(330, 175);
            lblTags.AutoSize = true;
            grpUpstream.Controls.Add(lblTags);

            cboTags = new ComboBox();
            cboTags.DropDownStyle = ComboBoxStyle.DropDownList;
            cboTags.Items.AddRange(new object[] { "quality（译文优先）", "preserve（标签优先）" });
            cboTags.SelectedIndex = 0;
            cboTags.Location = new Point(400, 172);
            cboTags.Size = new Size(210, 25);
            grpUpstream.Controls.Add(cboTags);

            GroupBox grpBridge = new GroupBox();
            grpBridge.Text = "AI Bridge 连接信息";
            grpBridge.Location = new Point(12, 230);
            grpBridge.Size = new Size(760, 100);
            grpBridge.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(grpBridge);

            Label lblEndpoint = new Label();
            lblEndpoint.Text = "Endpoint";
            lblEndpoint.Location = new Point(15, 30);
            lblEndpoint.AutoSize = true;
            grpBridge.Controls.Add(lblEndpoint);

            txtEndpoint = new TextBox();
            txtEndpoint.ReadOnly = true;
            txtEndpoint.Location = new Point(100, 27);
            txtEndpoint.Size = new Size(540, 25);
            txtEndpoint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            grpBridge.Controls.Add(txtEndpoint);

            Button btnCopyEndpoint = new Button();
            btnCopyEndpoint.Text = "复制";
            btnCopyEndpoint.Location = new Point(660, 26);
            btnCopyEndpoint.Size = new Size(85, 27);
            btnCopyEndpoint.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCopyEndpoint.Click += delegate { CopyText(txtEndpoint.Text); };
            grpBridge.Controls.Add(btnCopyEndpoint);

            Label lblModelsPath = new Label();
            lblModelsPath.Text = "Models path";
            lblModelsPath.Location = new Point(15, 64);
            lblModelsPath.AutoSize = true;
            grpBridge.Controls.Add(lblModelsPath);

            txtModelsPath = new TextBox();
            txtModelsPath.ReadOnly = true;
            txtModelsPath.Location = new Point(100, 61);
            txtModelsPath.Size = new Size(540, 25);
            txtModelsPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            grpBridge.Controls.Add(txtModelsPath);

            Button btnCopyModels = new Button();
            btnCopyModels.Text = "复制";
            btnCopyModels.Location = new Point(660, 60);
            btnCopyModels.Size = new Size(85, 27);
            btnCopyModels.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCopyModels.Click += delegate { CopyText(txtModelsPath.Text); };
            grpBridge.Controls.Add(btnCopyModels);

            btnStart = new Button();
            btnStart.Text = "启动适配器";
            btnStart.Location = new Point(12, 342);
            btnStart.Size = new Size(120, 32);
            btnStart.Click += delegate { StartAdapter(); };
            Controls.Add(btnStart);

            btnStop = new Button();
            btnStop.Text = "停止适配器";
            btnStop.Location = new Point(142, 342);
            btnStop.Size = new Size(120, 32);
            btnStop.Click += delegate { StopAdapter(true); };
            Controls.Add(btnStop);

            Button btnSave = new Button();
            btnSave.Text = "保存设置";
            btnSave.Location = new Point(272, 342);
            btnSave.Size = new Size(100, 32);
            btnSave.Click += delegate { SaveSettings(true); };
            Controls.Add(btnSave);

            btnOpenLog = new Button();
            btnOpenLog.Text = "打开日志";
            btnOpenLog.Location = new Point(382, 342);
            btnOpenLog.Size = new Size(100, 32);
            btnOpenLog.Click += delegate { OpenLog(); };
            Controls.Add(btnOpenLog);

            lblStatus = new Label();
            lblStatus.Text = "就绪";
            lblStatus.Location = new Point(500, 350);
            lblStatus.Size = new Size(270, 20);
            lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(lblStatus);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.WordWrap = false;
            txtLog.Font = new Font("Consolas", 9F);
            txtLog.BackColor = Color.White;
            txtLog.Location = new Point(12, 380);
            txtLog.Size = new Size(760, 280);
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(txtLog);

            AppendLog("Trados AI Bridge Adapter GUI");
            AppendLog("Adapter: " + adapterPath);
            AppendLog("Settings: " + settingsPath);
            AppendLog("");
        }

        private void UpdateEndpointLabels()
        {
            int port = (int)numPort.Value;
            txtEndpoint.Text = "http://127.0.0.1:" + port + "/v1/";
            txtModelsPath.Text = "http://127.0.0.1:" + port + "/v1/models";
        }

        private string SelectedEngine()
        {
            if (cboEngine.SelectedIndex == 1) return "gemma";
            if (cboEngine.SelectedIndex == 2) return "hymt2";
            return "auto";
        }

        private string SelectedTagMode()
        {
            return cboTags.SelectedIndex == 1 ? "preserve" : "quality";
        }

        private void SetStatus(string text, bool error)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = error ? Color.Firebrick : Color.DarkGreen;
        }

        private void UpdateButtons()
        {
            bool running = adapterProcess != null && !adapterProcess.HasExited;
            btnStart.Enabled = !running;
            btnStop.Enabled = running;
            btnRefresh.Enabled = !running;
            txtUpstream.Enabled = !running;
            numPort.Enabled = !running;
            cboModel.Enabled = !running;
            cboEngine.Enabled = !running;
            cboTags.Enabled = !running;
            chkApiKey.Enabled = !running;
            txtApiKey.Enabled = !running && chkApiKey.Checked;
        }

        private string NormalizeUpstream(string value)
        {
            string text = (value ?? "").Trim();
            if (text.Length == 0) return "";
            text = text.TrimEnd('/');
            if (text.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - "/v1/chat/completions".Length);
            else if (text.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - "/v1/models".Length);
            else if (text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - 3);
            return text.TrimEnd('/');
        }

        private async void RefreshModels()
        {
            string upstream = NormalizeUpstream(txtUpstream.Text);
            Uri uri;
            if (upstream.Length == 0 || !Uri.TryCreate(upstream, UriKind.Absolute, out uri))
            {
                SetStatus("API 地址无效", true);
                return;
            }

            btnRefresh.Enabled = false;
            SetStatus("正在读取模型列表...", false);
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    if (chkApiKey.Checked && txtApiKey.Text.Length > 0)
                    {
                        client.DefaultRequestHeaders.Authorization =
                            new AuthenticationHeaderValue("Bearer", txtApiKey.Text);
                    }

                    string json = await client.GetStringAsync(upstream + "/v1/models");
                    List<string> models = ParseModelIds(json);
                    string current = cboModel.Text;
                    cboModel.Items.Clear();
                    foreach (string model in models) cboModel.Items.Add(model);

                    if (!string.IsNullOrEmpty(current))
                    {
                        cboModel.Text = current;
                    }
                    else if (cboModel.Items.Count > 0)
                    {
                        cboModel.SelectedIndex = 0;
                    }

                    SetStatus("已读取 " + models.Count + " 个模型", false);
                    AppendLog("模型列表已刷新：" + models.Count + " 个");
                    foreach (string model in models) AppendLog("  " + model);
                }
            }
            catch (Exception ex)
            {
                SetStatus("读取模型失败", true);
                AppendLog("读取模型失败：" + ex.Message);
            }
            finally
            {
                if (!closing) btnRefresh.Enabled = adapterProcess == null || adapterProcess.HasExited;
            }
        }

        private static List<string> ParseModelIds(string json)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> root = ser.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) return result;

            object dataObject;
            if (root.TryGetValue("data", out dataObject))
            {
                object[] data = dataObject as object[];
                if (data != null)
                {
                    foreach (object item in data)
                    {
                        Dictionary<string, object> dict = item as Dictionary<string, object>;
                        if (dict != null && dict.ContainsKey("id"))
                        {
                            string id = Convert.ToString(dict["id"]);
                            if (!string.IsNullOrEmpty(id) && seen.Add(id)) result.Add(id);
                        }
                    }
                }
            }

            if (result.Count == 0)
            {
                object modelsObject;
                if (root.TryGetValue("models", out modelsObject))
                {
                    object[] models = modelsObject as object[];
                    if (models != null)
                    {
                        foreach (object item in models)
                        {
                            Dictionary<string, object> dict = item as Dictionary<string, object>;
                            if (dict == null) continue;
                            string id = dict.ContainsKey("name") ? Convert.ToString(dict["name"])
                                : (dict.ContainsKey("model") ? Convert.ToString(dict["model"]) : null);
                            if (!string.IsNullOrEmpty(id) && seen.Add(id)) result.Add(id);
                        }
                    }
                }
            }

            return result;
        }

        private void StartAdapter()
        {
            if (adapterProcess != null && !adapterProcess.HasExited)
            {
                return;
            }
            if (!File.Exists(adapterPath))
            {
                MessageBox.Show(this, "找不到 AIBridgeAdapter.exe：\n" + adapterPath,
                    "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string upstream = NormalizeUpstream(txtUpstream.Text);
            Uri uri;
            if (upstream.Length == 0 || !Uri.TryCreate(upstream, UriKind.Absolute, out uri))
            {
                MessageBox.Show(this, "请填写有效的 API 地址，例如 http://127.0.0.1:9931",
                    "参数错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string model = (cboModel.Text ?? "").Trim().Replace("\"", "");
            StringBuilder args = new StringBuilder();
            args.Append("--port ").Append((int)numPort.Value);
            args.Append(" --upstream \"").Append(upstream).Append("\"");
            args.Append(" --tags ").Append(SelectedTagMode());
            if (SelectedEngine() != "auto") args.Append(" --engine ").Append(SelectedEngine());
            if (model.Length > 0) args.Append(" --model \"").Append(model).Append("\"");

            try
            {
                ProcessStartInfo info = new ProcessStartInfo(adapterPath, args.ToString());
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = Encoding.UTF8;
                info.StandardErrorEncoding = Encoding.UTF8;
                info.WorkingDirectory = baseDir;
                info.EnvironmentVariables.Remove("AIBRIDGE_API_KEY");
                if (chkApiKey.Checked && txtApiKey.Text.Length > 0)
                {
                    info.EnvironmentVariables["AIBRIDGE_API_KEY"] = txtApiKey.Text;
                }

                Process process = new Process();
                process.StartInfo = info;
                process.EnableRaisingEvents = true;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) AppendLog(e.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) AppendLog("ERR " + e.Data);
                };
                process.Exited += delegate { OnAdapterExited(process); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                adapterProcess = process;

                SaveSettings(false);
                UpdateButtons();
                SetStatus("适配器运行中，PID " + process.Id, false);
                AppendLog("");
                AppendLog("适配器已启动，PID " + process.Id);
                AppendLog("AI Bridge Endpoint: " + txtEndpoint.Text);
                AppendLog("Models path: " + txtModelsPath.Text);
                AppendLog("");
            }
            catch (Exception ex)
            {
                SetStatus("启动失败", true);
                AppendLog("启动失败：" + ex.Message);
                MessageBox.Show(this, "启动适配器失败：\n" + ex.Message,
                    "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnAdapterExited(Process process)
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(delegate
                {
                    if (adapterProcess == process) adapterProcess = null;
                    UpdateButtons();
                    SetStatus("适配器已停止", false);
                    AppendLog("适配器已停止。");
                }));
            }
            catch { }
        }

        private void StopAdapter(bool userInitiated)
        {
            Process process = adapterProcess;
            if (process == null || process.HasExited)
            {
                adapterProcess = null;
                UpdateButtons();
                return;
            }

            try
            {
                process.Kill();
                process.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                AppendLog("停止失败：" + ex.Message);
            }
            adapterProcess = null;
            UpdateButtons();
            SetStatus("适配器已停止", false);
            if (userInitiated) AppendLog("适配器已停止。");
        }

        private void SaveSettings(bool notify)
        {
            try
            {
                GuiSettings settings = new GuiSettings();
                settings.Upstream = NormalizeUpstream(txtUpstream.Text);
                settings.Port = (int)numPort.Value;
                settings.Model = (cboModel.Text ?? "").Trim();
                settings.EngineIndex = cboEngine.SelectedIndex < 0 ? 0 : cboEngine.SelectedIndex;
                settings.TagsIndex = cboTags.SelectedIndex < 0 ? 0 : cboTags.SelectedIndex;
                settings.UseApiKey = chkApiKey.Checked;
                settings.ApiKeyProtected = "";

                if (chkApiKey.Checked && txtApiKey.Text.Length > 0)
                {
                    byte[] protectedBytes = ProtectedData.Protect(
                        Encoding.UTF8.GetBytes(txtApiKey.Text), null, DataProtectionScope.CurrentUser);
                    settings.ApiKeyProtected = Convert.ToBase64String(protectedBytes);
                }

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                File.WriteAllText(settingsPath, ser.Serialize(settings), new UTF8Encoding(false));
                if (notify)
                {
                    SetStatus("设置已保存", false);
                    AppendLog("设置已保存到 " + settingsPath);
                }
            }
            catch (Exception ex)
            {
                SetStatus("保存设置失败", true);
                AppendLog("保存设置失败：" + ex.Message);
                if (notify)
                {
                    MessageBox.Show(this, "保存设置失败：\n" + ex.Message,
                        "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void LoadSettings()
        {
            if (!File.Exists(settingsPath)) return;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                GuiSettings settings = ser.Deserialize<GuiSettings>(File.ReadAllText(settingsPath));
                if (settings == null) return;

                txtUpstream.Text = string.IsNullOrEmpty(settings.Upstream)
                    ? "http://127.0.0.1:9931" : settings.Upstream;
                numPort.Value = Math.Max(numPort.Minimum, Math.Min(numPort.Maximum, settings.Port));
                cboModel.Text = settings.Model ?? "";
                cboEngine.SelectedIndex = Math.Max(0, Math.Min(2, settings.EngineIndex));
                cboTags.SelectedIndex = Math.Max(0, Math.Min(1, settings.TagsIndex));
                chkApiKey.Checked = settings.UseApiKey;

                if (settings.UseApiKey && !string.IsNullOrEmpty(settings.ApiKeyProtected))
                {
                    try
                    {
                        byte[] data = Convert.FromBase64String(settings.ApiKeyProtected);
                        byte[] plain = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                        txtApiKey.Text = Encoding.UTF8.GetString(plain);
                    }
                    catch
                    {
                        txtApiKey.Text = "";
                        AppendLog("无法解密已保存的 API Key，请重新输入。");
                    }
                }
                UpdateEndpointLabels();
                UpdateButtons();
            }
            catch (Exception ex)
            {
                AppendLog("读取设置失败：" + ex.Message);
            }
        }

        private void OpenLog()
        {
            string logPath = Path.Combine(baseDir, "bridge.log");
            if (!File.Exists(logPath))
            {
                MessageBox.Show(this, "日志文件还不存在：\n" + logPath,
                    "打开日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Process.Start("notepad.exe", "\"" + logPath + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开日志：\n" + ex.Message,
                    "打开日志", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CopyText(string text)
        {
            try
            {
                Clipboard.SetText(text ?? "");
                SetStatus("已复制", false);
            }
            catch (Exception ex)
            {
                SetStatus("复制失败", true);
                AppendLog("复制失败：" + ex.Message);
            }
        }

        private void AppendLog(string line)
        {
            if (txtLog == null || txtLog.IsDisposed) return;
            if (txtLog.InvokeRequired)
            {
                try { txtLog.BeginInvoke(new Action<string>(AppendLog), line); } catch { }
                return;
            }
            txtLog.AppendText((line ?? "") + Environment.NewLine);
            if (txtLog.TextLength > 300000)
            {
                txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 200000);
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (adapterProcess != null && !adapterProcess.HasExited)
            {
                DialogResult result = MessageBox.Show(this,
                    "适配器正在运行，是否停止并退出？",
                    "退出", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            closing = true;
            SaveSettings(false);
            if (adapterProcess != null && !adapterProcess.HasExited)
            {
                try { adapterProcess.Kill(); } catch { }
            }
        }
    }

    internal static class GuiProgram
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
