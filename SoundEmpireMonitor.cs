using System;
using System.IO;
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
        private const string AppVersion = "1.2";

        static int checkIntervalSeconds = 60;
        static double timeoutMinutes = 1;
        static string webhookUrl = "";
        static List<FolderMonitor> folders = new List<FolderMonitor>();
        
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem statusMenuItem;
        private ToolStripMenuItem foldersMenuItem;
        private ToolStripMenuItem startStopMenuItem;
        private ToolStripMenuItem exitMenuItem;
        
        private bool isMonitoring = true;
        private Thread monitorThread;
        private bool isRunning = true;
        private bool allSourcesUnavailableNotified = false;
        
        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
            if (!LoadConfig(configPath))
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
            
            SendTestMessage();
            
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
            
            foldersMenuItem = new ToolStripMenuItem("Folders");
            UpdateFoldersMenu();
            trayMenu.Items.Add(foldersMenuItem);
            
            trayMenu.Items.Add(new ToolStripSeparator());
            
            startStopMenuItem = new ToolStripMenuItem("Stop monitoring");
            startStopMenuItem.Click += OnStartStopClick;
            trayMenu.Items.Add(startStopMenuItem);
            
            exitMenuItem = new ToolStripMenuItem("Exit");
            exitMenuItem.Click += OnExitClick;
            trayMenu.Items.Add(exitMenuItem);
            
            trayIcon.ContextMenuStrip = trayMenu;
        }

        private void OnTrayMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            UpdateFoldersMenu();
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
            foreach (var folder in folders)
            {
                string status = Directory.Exists(folder.Path) ? "[OK] " : "[NO] ";
                var item = new ToolStripMenuItem(status + folder.City + " - " + folder.Path);
                item.Enabled = false;
                foldersMenuItem.DropDownItems.Add(item);
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
        
        private void OnStartStopClick(object sender, EventArgs e)
        {
            isMonitoring = !isMonitoring;
            if (isMonitoring)
            {
                startStopMenuItem.Text = "Stop monitoring";
                statusMenuItem.Text = "Status: Monitoring active";
                statusMenuItem.ForeColor = Color.Green;
                UpdateTrayIcon(true);
                trayIcon.ShowBalloonTip(2000, "Sound Empire Monitor", "Monitoring resumed", ToolTipIcon.Info);
            }
            else
            {
                startStopMenuItem.Text = "Start monitoring";
                statusMenuItem.Text = "Status: Monitoring stopped";
                statusMenuItem.ForeColor = Color.Red;
                UpdateTrayIcon(false);
                trayIcon.ShowBalloonTip(2000, "Sound Empire Monitor", "Monitoring stopped", ToolTipIcon.Warning);
            }
        }
        
        private void OnTrayDoubleClick(object sender, EventArgs e)
        {
            string info = "Sound Empire Monitor v" + AppVersion + "\n\n";
            info += "Monitoring: " + (isMonitoring ? "Active" : "Stopped") + "\n";
            info += "Folders: " + folders.Count + "\n\n";
            info += "Folder status:\n";
            foreach (var folder in folders)
            {
                string status = Directory.Exists(folder.Path) ? "[OK] Available" : "[NO] Not available";
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

                        foreach (var folder in folders)
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

                        if (folders.Count > 0)
                        {
                            if (availableSources == 0 && unavailableSources == folders.Count)
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

            foreach (var folder in folders)
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
                                    folders.Add(new FolderMonitor { Path = path, City = city });
                                }
                            }
                        }
                    }
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
}