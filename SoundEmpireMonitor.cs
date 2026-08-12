using System;
using System.IO;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SoundEmpireMonitor
{
    class Program : ApplicationContext
    {
        private const string AppVersion = "1.3";
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
        private static readonly object foldersLock = new object();

        static int checkIntervalSeconds = 60;
        static double timeoutMinutes = 1;
        static string webhookUrl = "";
        static List<FolderMonitor> folders = new List<FolderMonitor>();
        
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem statusMenuItem;
        private ToolStripMenuItem settingsMenuItem;
        private ToolStripMenuItem foldersMenuItem;
        private ToolStripMenuItem startMenuItem;
        private ToolStripMenuItem stopMenuItem;
        private ToolStripMenuItem exitMenuItem;
        
        private bool isMonitoring = true;
        private Thread monitorThread;
        private bool isRunning = true;
        private bool allSourcesUnavailableNotified = false;
        
        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            
            if (!LoadConfig(ConfigPath))
            {
                MessageBox.Show("Ne udalos zagruzit config.ini", "Oshibka", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Program());
        }
        
        public Program()
        {
            CreateTrayIcon();
            CreateContextMenu();
            
            QueueTestMessage();
            
            monitorThread = new Thread(MonitorLoop);
            monitorThread.IsBackground = true;
            monitorThread.Start();
            
            trayIcon.ShowBalloonTip(3000, "Sound Empire Monitor", "Monitoring started in background", ToolTipIcon.Info);
        }
        
        private void CreateTrayIcon()
        {
            trayIcon = new NotifyIcon();
            trayIcon.Text = "Sound Empire Monitor v" + AppVersion;
            trayIcon.Icon = LoadIcon();
            trayIcon.Visible = true;
            trayIcon.DoubleClick += OnTrayDoubleClick;
            Application.DoEvents();
        }
        
        private void CreateContextMenu()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Opening += OnTrayMenuOpening;
            
            statusMenuItem = new ToolStripMenuItem("Status: Monitoring active");
            statusMenuItem.Enabled = false;
            statusMenuItem.ForeColor = Color.Green;
            trayMenu.Items.Add(statusMenuItem);
            
            trayMenu.Items.Add(new ToolStripSeparator());

            settingsMenuItem = new ToolStripMenuItem("Настройки");
            settingsMenuItem.Click += OnSettingsClick;
            trayMenu.Items.Add(settingsMenuItem);
            
            trayMenu.Items.Add(new ToolStripSeparator());
            
            foldersMenuItem = new ToolStripMenuItem("Folders");
            UpdateFoldersMenu();
            trayMenu.Items.Add(foldersMenuItem);
            
            trayMenu.Items.Add(new ToolStripSeparator());
            
            startMenuItem = new ToolStripMenuItem("Старт");
            startMenuItem.Click += OnStartClick;
            trayMenu.Items.Add(startMenuItem);

            stopMenuItem = new ToolStripMenuItem("Стоп");
            stopMenuItem.Click += OnStopClick;
            trayMenu.Items.Add(stopMenuItem);
            
            trayMenu.Items.Add(new ToolStripSeparator());

            exitMenuItem = new ToolStripMenuItem("Exit");
            exitMenuItem.Click += OnExitClick;
            trayMenu.Items.Add(exitMenuItem);
            
            trayIcon.ContextMenuStrip = trayMenu;
            UpdateMonitoringMenuState();
        }

        private void OnTrayMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            UpdateFoldersMenu();
            UpdateMonitoringMenuState();
        }
        
        private Icon LoadIcon()
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "monitor.ico");
            if (File.Exists(iconPath))
            {
                try
                {
                    return new Icon(iconPath);
                }
                catch { }
            }
            
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(0, 120, 215));
                g.DrawRectangle(Pens.White, 2, 2, 27, 22);
                g.DrawLine(Pens.White, 8, 26, 23, 26);
                g.DrawLine(Pens.White, 12, 28, 19, 28);
                g.FillEllipse(Brushes.LimeGreen, 24, 4, 6, 6);
                g.DrawLine(Pens.White, 4, 8, 4, 16);
                g.DrawLine(Pens.White, 5, 10, 9, 6);
                g.DrawLine(Pens.White, 5, 14, 9, 18);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
        
        private void UpdateFoldersMenu()
        {
            if (foldersMenuItem == null) return;
            
            foldersMenuItem.DropDownItems.Clear();
            foreach (var folder in GetFoldersSnapshot())
            {
                string status = folder.LastKnownAvailable.HasValue
                    ? (folder.LastKnownAvailable.Value ? "[OK] " : "[NO] ")
                    : "[??] ";
                var item = new ToolStripMenuItem(status + folder.City + " - " + folder.Path);
                item.Enabled = false;
                foldersMenuItem.DropDownItems.Add(item);
            }
        }

        private void UpdateMonitoringMenuState()
        {
            if (statusMenuItem == null || startMenuItem == null || stopMenuItem == null)
            {
                return;
            }

            if (isMonitoring)
            {
                statusMenuItem.Text = "Status: Monitoring active";
                statusMenuItem.ForeColor = Color.Green;
                startMenuItem.Enabled = false;
                stopMenuItem.Enabled = true;
                UpdateTrayIcon(true);
            }
            else
            {
                statusMenuItem.Text = "Status: Monitoring stopped";
                statusMenuItem.ForeColor = Color.Red;
                startMenuItem.Enabled = true;
                stopMenuItem.Enabled = false;
                UpdateTrayIcon(false);
            }
        }

        private static List<FolderMonitor> GetFoldersSnapshot()
        {
            lock (foldersLock)
            {
                return new List<FolderMonitor>(folders);
            }
        }

        private static void ReplaceFolders(List<FolderMonitor> newFolders)
        {
            lock (foldersLock)
            {
                folders = newFolders;
            }
        }

        private static string BuildFoldersText(List<FolderMonitor> currentFolders)
        {
            StringBuilder builder = new StringBuilder();
            foreach (var folder in currentFolders)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }
                builder.Append(folder.Path).Append("|").Append(folder.City);
            }
            return builder.ToString();
        }

        private static bool TryParseFoldersText(string foldersText, out List<FolderMonitor> parsedFolders, out string errorMessage)
        {
            parsedFolders = new List<FolderMonitor>();
            errorMessage = "";

            string[] lines = foldersText.Replace("\r", "").Split('\n');
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith(";"))
                {
                    continue;
                }

                int separatorIndex = trimmedLine.IndexOf('|');
                if (separatorIndex <= 0 || separatorIndex >= trimmedLine.Length - 1)
                {
                    errorMessage = "Folder list must use format Path|City on every line.";
                    return false;
                }

                string path = trimmedLine.Substring(0, separatorIndex).Trim();
                string city = trimmedLine.Substring(separatorIndex + 1).Trim();
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(city))
                {
                    errorMessage = "Folder list contains an empty path or city.";
                    return false;
                }

                parsedFolders.Add(new FolderMonitor { Path = path, City = city });
            }

            if (parsedFolders.Count == 0)
            {
                errorMessage = "Add at least one folder.";
                return false;
            }

            return true;
        }

        private static bool SaveConfig(string configPath, int newCheckIntervalSeconds, double newTimeoutMinutes, string newWebhookUrl, List<FolderMonitor> newFolders, out string errorMessage)
        {
            try
            {
                using (StreamWriter writer = new StreamWriter(configPath, false, Encoding.UTF8))
                {
                    writer.WriteLine("; Sound Empire Monitor Configuration");
                    writer.WriteLine("; Generated from settings dialog");
                    writer.WriteLine();
                    writer.WriteLine("[Settings]");
                    writer.WriteLine("check_interval_seconds = " + newCheckIntervalSeconds);
                    writer.WriteLine("timeout_minutes = " + newTimeoutMinutes.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine("webhook_url = " + newWebhookUrl);
                    writer.WriteLine();
                    writer.WriteLine("[Folders]");

                    for (int i = 0; i < newFolders.Count; i++)
                    {
                        writer.WriteLine("folder" + (i + 1) + " = " + newFolders[i].Path + "|" + newFolders[i].City);
                    }
                }

                errorMessage = "";
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private void OnSettingsClick(object sender, EventArgs e)
        {
            List<FolderMonitor> currentFolders = GetFoldersSnapshot();
            string foldersText = BuildFoldersText(currentFolders);

            using (SettingsForm form = new SettingsForm(checkIntervalSeconds, timeoutMinutes, webhookUrl, foldersText))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                List<FolderMonitor> parsedFolders;
                string parseError;
                if (!TryParseFoldersText(form.FoldersText, out parsedFolders, out parseError))
                {
                    MessageBox.Show(parseError, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string saveError;
                if (!SaveConfig(ConfigPath, form.CheckIntervalSeconds, form.TimeoutMinutes, form.WebhookUrl, parsedFolders, out saveError))
                {
                    MessageBox.Show("Failed to save config: " + saveError, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                checkIntervalSeconds = form.CheckIntervalSeconds;
                timeoutMinutes = form.TimeoutMinutes;
                webhookUrl = form.WebhookUrl;
                ReplaceFolders(parsedFolders);
                allSourcesUnavailableNotified = false;
                UpdateFoldersMenu();
                trayIcon.ShowBalloonTip(2000, "Sound Empire Monitor", "Settings saved", ToolTipIcon.Info);
            }
        }
        
        private void UpdateTrayIcon(bool isActive)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                if (isActive)
                {
                    g.Clear(Color.FromArgb(0, 120, 215));
                    g.FillEllipse(Brushes.LimeGreen, 24, 4, 6, 6);
                }
                else
                {
                    g.Clear(Color.FromArgb(100, 100, 100));
                    g.FillEllipse(Brushes.Red, 24, 4, 6, 6);
                }
                g.DrawRectangle(Pens.White, 2, 2, 27, 22);
                g.DrawLine(Pens.White, 8, 26, 23, 26);
                g.DrawLine(Pens.White, 12, 28, 19, 28);
                g.DrawLine(Pens.White, 4, 8, 4, 16);
                g.DrawLine(Pens.White, 5, 10, 9, 6);
                g.DrawLine(Pens.White, 5, 14, 9, 18);
            }
            trayIcon.Icon = Icon.FromHandle(bmp.GetHicon());
        }
        
        private void OnStartClick(object sender, EventArgs e)
        {
            isMonitoring = true;
            UpdateMonitoringMenuState();
            trayIcon.ShowBalloonTip(2000, "Sound Empire Monitor", "Monitoring resumed", ToolTipIcon.Info);
        }

        private void OnStopClick(object sender, EventArgs e)
        {
            isMonitoring = false;
            UpdateMonitoringMenuState();
            trayIcon.ShowBalloonTip(2000, "Sound Empire Monitor", "Monitoring stopped", ToolTipIcon.Warning);
        }
        
        private void OnTrayDoubleClick(object sender, EventArgs e)
        {
            string info = "Sound Empire Monitor v" + AppVersion + "\n\n";
            info += "Monitoring: " + (isMonitoring ? "Active" : "Stopped") + "\n";
            info += "Folders: " + GetFoldersSnapshot().Count + "\n\n";
            info += "Folder status:\n";
            foreach (var folder in GetFoldersSnapshot())
            {
                string status = folder.LastKnownAvailable.HasValue
                    ? (folder.LastKnownAvailable.Value ? "[OK] Available" : "[NO] Not available")
                    : "[??] Not checked yet";
                info += folder.City + ": " + status + "\n";
            }
            MessageBox.Show(info, "Sound Empire Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        
        private void OnExitClick(object sender, EventArgs e)
        {
            isRunning = false;
            if (trayIcon != null) trayIcon.Visible = false;
            Application.Exit();
        }

        private void QueueTestMessage()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SendTestMessage();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Test error: " + ex.Message);
                }
            });
        }
        
        private void MonitorLoop()
        {
            while (isRunning)
            {
                if (isMonitoring)
                {
                    try
                    {
                        int availableSources = 0;
                        int unavailableSources = 0;
                        int unavailableRetrySeconds = Math.Max(checkIntervalSeconds * 3, 60);
                        List<FolderMonitor> currentFolders = GetFoldersSnapshot();

                        foreach (var folder in currentFolders)
                        {
                            if (folder.CheckAndSendAlert(webhookUrl, timeoutMinutes, unavailableRetrySeconds))
                            {
                                availableSources++;
                            }
                            else
                            {
                                unavailableSources++;
                            }
                        }

                        if (currentFolders.Count > 0)
                        {
                            if (availableSources == 0 && unavailableSources == currentFolders.Count)
                            {
                                if (!allSourcesUnavailableNotified)
                                {
                                    ShowAllSourcesUnavailable();
                                    allSourcesUnavailableNotified = true;
                                }
                            }
                            else
                            {
                                allSourcesUnavailableNotified = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[" + DateTime.Now + "] Error: " + ex.Message);
                    }
                }
                Thread.Sleep(checkIntervalSeconds * 1000);
            }
        }

        private void ShowAllSourcesUnavailable()
        {
            string info = "Sound Empire Monitor\n\n";
            info += "All sources are unavailable.\n\n";
            info += "Sources:\n";

            foreach (var folder in GetFoldersSnapshot())
            {
                info += folder.City + ": " + folder.Path + "\n";
            }

            MessageBox.Show(info, "Sound Empire Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        
        static bool LoadConfig(string configPath)
        {
            try
            {
                if (!File.Exists(configPath)) return false;
                
                string[] lines = File.ReadAllLines(configPath, Encoding.UTF8);
                string currentSection = "";
                List<FolderMonitor> loadedFolders = new List<FolderMonitor>();
                
                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith(";")) continue;
                    
                    if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                    {
                        currentSection = trimmedLine.Substring(1, trimmedLine.Length - 2);
                        continue;
                    }
                    
                    if (trimmedLine.Contains("="))
                    {
                        string[] parts = trimmedLine.Split(new char[] { '=' }, 2);
                        string key = parts[0].Trim();
                        string value = parts[1].Trim();
                        
                        if (value.StartsWith("\"") && value.EndsWith("\""))
                            value = value.Substring(1, value.Length - 2);
                        
                        if (currentSection == "Settings")
                        {
                            if (key == "check_interval_seconds") int.TryParse(value, out checkIntervalSeconds);
                            else if (key == "timeout_minutes") double.TryParse(value, out timeoutMinutes);
                            else if (key == "webhook_url") webhookUrl = value;
                        }
                        else if (currentSection == "Folders")
                        {
                            if (value.Contains("|"))
                            {
                                string[] parts2 = value.Split('|');
                                string path = parts2[0].Trim();
                                string city = parts2[1].Trim();
                                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(city))
                                {
                                    loadedFolders.Add(new FolderMonitor { Path = path, City = city });
                                }
                            }
                        }
                    }
                }

                lock (foldersLock)
                {
                    folders = loadedFolders;
                }

                return !string.IsNullOrEmpty(webhookUrl) && folders.Count > 0;
            }
            catch { return false; }
        }
        
        static void SendTestMessage()
        {
            try
            {
                string message = ":white_check_mark: **Sound Empire Monitor**\n" +
                               "Monitoring started in background\n" +
                               "**Folders:** " + folders.Count + "\n" +
                               "**Interval:** " + checkIntervalSeconds + " sec\n" +
                               "**Date:** " + DateTime.Now.ToString("yyyy-MM-dd") + "\n" +
                               "**Time:** " + DateTime.Now.ToString("HH:mm:ss");
                SendToMattermost(message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Test error: " + ex.Message);
            }
        }
        
        public static void SendToMattermost(string message)
        {
            try
            {
                string escapedMessage = message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
                string json = "{\"text\": \"" + escapedMessage + "\"}";
                byte[] data = Encoding.UTF8.GetBytes(json);
                
                using (WebClient client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.ContentType] = "application/json";
                    client.UploadData(webhookUrl, "POST", data);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Send error: " + ex.Message);
                throw;
            }
        }
    }
    
    class FolderMonitor
    {
        public string Path { get; set; }
        public string City { get; set; }
        public bool? LastKnownAvailable { get; private set; }
        
        private DateTime? lastFileTime = null;
        private bool errorSent = false;
        private bool folderErrorSent = false;
        private bool firstRun = true;
        private DateTime nextAvailabilityCheck = DateTime.MinValue;
        
        private string GetFileName(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return "";
            int lastBackslash = fullPath.LastIndexOf('\\');
            if (lastBackslash >= 0) return fullPath.Substring(lastBackslash + 1);
            return fullPath;
        }
        
        public bool CheckAndSendAlert(string webhookUrl, double timeoutMinutes, int unavailableRetrySeconds)
        {
            try
            {
                if (!Directory.Exists(Path))
                {
                    LastKnownAvailable = false;
                    if (DateTime.Now < nextAvailabilityCheck)
                    {
                        return false;
                    }

                    if (!folderErrorSent)
                    {
                        string msg = ":warning: **Sound Empire " + City + "**\n" +
                                   "Log folder not found!\n" +
                                   "**Path:** " + Path + "\n" +
                                   "**Date:** " + DateTime.Now.ToString("yyyy-MM-dd") + "\n" +
                                   "**Time:** " + DateTime.Now.ToString("HH:mm:ss");
                        Program.SendToMattermost(msg);
                        folderErrorSent = true;
                    }

                    nextAvailabilityCheck = DateTime.Now.AddSeconds(unavailableRetrySeconds);
                    return false;
                }
                
                if (folderErrorSent)
                {
                    string msg = ":white_check_mark: **Sound Empire " + City + "**\n" +
                               "Folder found! Monitoring resumed\n" +
                               "**Path:** " + Path + "\n" +
                               "**Date:** " + DateTime.Now.ToString("yyyy-MM-dd") + "\n" +
                               "**Time:** " + DateTime.Now.ToString("HH:mm:ss");
                    Program.SendToMattermost(msg);
                    folderErrorSent = false;
                    firstRun = true;
                }

                LastKnownAvailable = true;

                nextAvailabilityCheck = DateTime.MinValue;
                
                string[] files = Directory.GetFiles(Path, "Rep_*.db");
                if (files.Length == 0) return true;
                
                string latestFile = "";
                DateTime latestTime = DateTime.MinValue;
                foreach (string file in files)
                {
                    DateTime writeTime = File.GetLastWriteTime(file);
                    if (writeTime > latestTime)
                    {
                        latestTime = writeTime;
                        latestFile = file;
                    }
                }
                
                if (firstRun)
                {
                    lastFileTime = latestTime;
                    firstRun = false;
                    string fileName = GetFileName(latestFile);
                    string msg = ":white_check_mark: **Sound Empire " + City + "**\n" +
                               "Monitoring started\n" +
                               "**File:** " + fileName + "\n" +
                               "**Last change:** " + latestTime.ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                               "**Date:** " + DateTime.Now.ToString("yyyy-MM-dd") + "\n" +
                               "**Time:** " + DateTime.Now.ToString("HH:mm:ss");
                    Program.SendToMattermost(msg);
                    return true;
                }
                
                if (latestTime == lastFileTime)
                {
                    TimeSpan diff = DateTime.Now - lastFileTime.Value;
                    if (diff.TotalMinutes >= timeoutMinutes && !errorSent)
                    {
                        string msg = ":sos: **Sound Empire " + City + "**\n" +
                                   "Logs frozen!\n" +
                                   "**Last change:** " + lastFileTime.Value.ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                   "**Elapsed:** " + diff.TotalMinutes.ToString("F1") + " minutes\n" +
                                   "**Date:** " + DateTime.Now.ToString("yyyy-MM-dd") + "\n" +
                                   "**Time:** " + DateTime.Now.ToString("HH:mm:ss");
                        Program.SendToMattermost(msg);
                        errorSent = true;
                    }
                }
                else
                {
                    if (errorSent)
                    {
                        string msg = ":white_check_mark: **Sound Empire " + City + "**\n" +
                                   "Logs recovered!\n" +
                                   "**New time:** " + latestTime.ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                   "**Date:** " + DateTime.Now.ToString("yyyy-MM-dd") + "\n" +
                                   "**Time:** " + DateTime.Now.ToString("HH:mm:ss");
                        Program.SendToMattermost(msg);
                        errorSent = false;
                    }
                    lastFileTime = latestTime;
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[" + DateTime.Now + "] [" + City + "] Error: " + ex.Message);
                return false;
            }
        }
    }

    internal class SettingsForm : Form
    {
        private NumericUpDown intervalInput;
        private NumericUpDown timeoutInput;
        private TextBox webhookInput;
        private TextBox foldersInput;

        public int CheckIntervalSeconds { get; private set; }
        public double TimeoutMinutes { get; private set; }
        public string WebhookUrl { get; private set; }
        public string FoldersText { get; private set; }

        public SettingsForm(int currentCheckIntervalSeconds, double currentTimeoutMinutes, string currentWebhookUrl, string currentFoldersText)
        {
            Text = "Настройки Sound Empire Monitor";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(760, 560);
            Font = SystemFonts.MessageBoxFont;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(12);
            layout.ColumnCount = 2;
            layout.RowCount = 4;
            layout.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Label intervalLabel = new Label();
            intervalLabel.Text = "Интервал проверки, сек";
            intervalLabel.Dock = DockStyle.Fill;
            intervalLabel.TextAlign = ContentAlignment.MiddleLeft;

            intervalInput = new NumericUpDown();
            intervalInput.Minimum = 1;
            intervalInput.Maximum = 86400;
            intervalInput.Value = Math.Max(1, currentCheckIntervalSeconds);
            intervalInput.Dock = DockStyle.Left;
            intervalInput.Width = 140;

            Label timeoutLabel = new Label();
            timeoutLabel.Text = "Таймаут без изменений, мин";
            timeoutLabel.Dock = DockStyle.Fill;
            timeoutLabel.TextAlign = ContentAlignment.MiddleLeft;

            timeoutInput = new NumericUpDown();
            timeoutInput.Minimum = 0.1M;
            timeoutInput.Maximum = 10080M;
            timeoutInput.DecimalPlaces = 1;
            timeoutInput.Increment = 0.5M;
            timeoutInput.Value = (decimal)Math.Max(0.1, currentTimeoutMinutes);
            timeoutInput.Dock = DockStyle.Left;
            timeoutInput.Width = 140;

            Label webhookLabel = new Label();
            webhookLabel.Text = "Webhook Mattermost";
            webhookLabel.Dock = DockStyle.Fill;
            webhookLabel.TextAlign = ContentAlignment.MiddleLeft;

            webhookInput = new TextBox();
            webhookInput.Text = currentWebhookUrl;
            webhookInput.Dock = DockStyle.Fill;

            Label foldersLabel = new Label();
            foldersLabel.Text = "Папки мониторинга, по одной в строке: Path|City";
            foldersLabel.Dock = DockStyle.Fill;
            foldersLabel.TextAlign = ContentAlignment.MiddleLeft;

            foldersInput = new TextBox();
            foldersInput.Multiline = true;
            foldersInput.ScrollBars = ScrollBars.Vertical;
            foldersInput.Dock = DockStyle.Fill;
            foldersInput.AcceptsReturn = true;
            foldersInput.Text = currentFoldersText;

            layout.Controls.Add(intervalLabel, 0, 0);
            layout.Controls.Add(intervalInput, 1, 0);
            layout.Controls.Add(timeoutLabel, 0, 1);
            layout.Controls.Add(timeoutInput, 1, 1);
            layout.Controls.Add(webhookLabel, 0, 2);
            layout.Controls.Add(webhookInput, 1, 2);
            layout.Controls.Add(foldersLabel, 0, 3);
            layout.Controls.Add(foldersInput, 1, 3);

            FlowLayoutPanel buttonsPanel = new FlowLayoutPanel();
            buttonsPanel.Dock = DockStyle.Bottom;
            buttonsPanel.Height = 48;
            buttonsPanel.Padding = new Padding(12, 0, 12, 12);
            buttonsPanel.FlowDirection = FlowDirection.RightToLeft;
            buttonsPanel.WrapContents = false;

            Button saveButton = new Button();
            saveButton.Text = "Сохранить";
            saveButton.Width = 110;
            saveButton.Click += OnSaveClick;

            Button cancelButton = new Button();
            cancelButton.Text = "Отмена";
            cancelButton.Width = 110;
            cancelButton.DialogResult = DialogResult.Cancel;

            buttonsPanel.Controls.Add(saveButton);
            buttonsPanel.Controls.Add(cancelButton);

            Controls.Add(layout);
            Controls.Add(buttonsPanel);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(webhookInput.Text))
            {
                MessageBox.Show(this, "Webhook не может быть пустым.", "Настройки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(foldersInput.Text))
            {
                MessageBox.Show(this, "Добавьте хотя бы одну папку мониторинга.", "Настройки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            CheckIntervalSeconds = (int)intervalInput.Value;
            TimeoutMinutes = (double)timeoutInput.Value;
            WebhookUrl = webhookInput.Text.Trim();
            FoldersText = foldersInput.Text;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}