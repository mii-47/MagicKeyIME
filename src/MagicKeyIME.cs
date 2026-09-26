// MagicKeyIME - タスクトレイからApple Magic Keyboardの英数/かな有効化を
// 適用/解除するための常駐アプリ。
//
// 仕組みはペアリング済みBluetoothデバイスのキャッシュSDP記述子を走査し、
// キー範囲上限 0x65 を 0xE7 に広げる(論理最大値は2バイト26E700に変換、SDP 長さフィールドは構造解析で自動修正)。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MagicKeyIME
{
    static class Sdp
    {
        public const string DevicesPath = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";
        public static readonly string[] Subs = { "CachedServices", "DynamicCachedServices" };

        public static int Find(byte[] hay, byte[] needle, int from = 0)
        {
            if (hay == null || hay.Length < needle.Length) return -1;
            for (int i = from; i <= hay.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++) { if (hay[i + j] != needle[j]) { ok = false; break; } }
                if (ok) return i;
            }
            return -1;
        }

        public static bool IsKbd(byte[] o) { return Find(o, new byte[] { 0x05, 0x01, 0x09, 0x06 }) >= 0; }
        public static bool IsPatched(byte[] o) { return Find(o, new byte[] { 0x05, 0x07, 0x19, 0x00, 0x29, 0xE7 }) >= 0; }
        public static bool IsUnpatched(byte[] o) { return Find(o, new byte[] { 0x05, 0x07, 0x19, 0x00, 0x29, 0x65 }) >= 0; }

        static void Bump(byte[] a, int pos, int size, int delta)
        {
            int val = 0;
            for (int j = 0; j < size; j++) val = (val << 8) | a[pos + j];
            val += delta;
            for (int j = size - 1; j >= 0; j--) { a[pos + j] = (byte)(val & 0xFF); val >>= 8; }
        }

        // ins を含む(先祖)要素の長さフィールド位置/サイズを返す
        static List<int[]> EnclosingLenFields(byte[] b, int ins)
        {
            var fields = new List<int[]>();
            var stack = new Stack<int[]>();
            stack.Push(new int[] { 0, b.Length });
            while (stack.Count > 0)
            {
                int[] r = stack.Pop(); int i = r[0], end = r[1];
                while (i < end)
                {
                    byte desc = b[i]; int type = desc >> 3, si = desc & 7;
                    int lenSize = 0, dataLen = 0;
                    switch (si)
                    {
                        case 0: dataLen = (type == 0) ? 0 : 1; break;
                        case 1: dataLen = 2; break;
                        case 2: dataLen = 4; break;
                        case 3: dataLen = 8; break;
                        case 4: dataLen = 16; break;
                        case 5: lenSize = 1; dataLen = b[i + 1]; break;
                        case 6: lenSize = 2; dataLen = (b[i + 1] << 8) | b[i + 2]; break;
                        case 7: lenSize = 4; dataLen = (b[i + 1] << 24) | (b[i + 2] << 16) | (b[i + 3] << 8) | b[i + 4]; break;
                    }
                    int dataStart = i + 1 + lenSize, dataEnd = dataStart + dataLen;
                    if (dataEnd > end) break;
                    if (ins >= dataStart && ins < dataEnd)
                    {
                        if (lenSize > 0) fields.Add(new int[] { i + 1, lenSize });
                        if (type == 6 || type == 7) stack.Push(new int[] { dataStart, dataEnd });
                        break;
                    }
                    i = dataEnd;
                }
            }
            return fields;
        }

        // 成功したら書き換え後のバイト列、対象外/既適用なら null
        public static byte[] PatchBlob(byte[] o)
        {
            if (IsPatched(o)) return null;
            int u = Find(o, new byte[] { 0x05, 0x07, 0x19, 0x00, 0x29, 0x65 });
            if (u < 0) return null;
            if (o[u - 2] == 0x25 && o[u - 1] == 0x65)
            {
                // Case A: 1バイト論理最大値 -> 2バイト(挿入)
                var lf = EnclosingLenFields(o, u - 1);
                var n = new List<byte>();
                for (int k = 0; k <= u - 3; k++) n.Add(o[k]);
                n.Add(0x26); n.Add(0xE7); n.Add(0x00);
                for (int k = u; k < o.Length; k++) n.Add(o[k]);
                var arr = n.ToArray();
                arr[u + 6] = 0xE7;
                foreach (var f in lf) Bump(arr, f[0], f[1], 1);
                return arr;
            }
            else if (o[u - 3] == 0x26 && o[u - 2] == 0x65 && o[u - 1] == 0x00)
            {
                // Case B: 既に2バイト -> 値だけ更新
                var arr = (byte[])o.Clone();
                arr[u - 2] = 0xE7; arr[u + 5] = 0xE7;
                return arr;
            }
            return null;
        }
    }

    enum Status { NoKeyboard, Applied, NotApplied, Mixed }

    class TrayApp : ApplicationContext
    {
        const string TaskName = "MagicKeyIME";
        readonly NotifyIcon _tray = new NotifyIcon();
        readonly ToolStripMenuItem _miStatus, _miApply, _miRestore, _miStartup;
        Icon _icoOn, _icoOff;

        // バックアップはProgramDataに配置
        // (C:\ProgramData\MagicKeyIME\sdp-backup.txt)。
        static string BackupDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MagicKeyIME"); } }
        static string BackupPath { get { return Path.Combine(BackupDir, "sdp-backup.txt"); } }

        public TrayApp()
        {
            _icoOn = MakeIcon(true);
            _icoOff = MakeIcon(false);

            var menu = new ContextMenuStrip();
            _miStatus = new ToolStripMenuItem("状態: 確認中") { Enabled = false };
            _miApply = new ToolStripMenuItem("英数/かなを有効化(適用)", null, (s, e) => DoApply());
            _miRestore = new ToolStripMenuItem("元に戻す(解除)", null, (s, e) => DoRestore());
            _miStartup = new ToolStripMenuItem("Windows 起動時に常駐", null, (s, e) => ToggleStartup());
            menu.Items.Add(_miStatus);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_miApply);
            menu.Items.Add(_miRestore);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_miStartup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("終了", null, (s, e) => ExitApp()));
            menu.Opening += (s, e) => Refresh();

            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += (s, e) => Refresh();
            Refresh();
        }

        // レジストリを走査して状態を判定
        static Status GetStatus()
        {
            bool anyPatched = false, anyUnpatched = false, anyKbd = false;
            try
            {
                using (var devs = Registry.LocalMachine.OpenSubKey(Sdp.DevicesPath, false))
                {
                    if (devs == null) return Status.NoKeyboard;
                    foreach (var dev in devs.GetSubKeyNames())
                    {
                        foreach (var sub in Sdp.Subs)
                        {
                            using (var k = Registry.LocalMachine.OpenSubKey(Sdp.DevicesPath + "\\" + dev + "\\" + sub, false))
                            {
                                if (k == null) continue;
                                foreach (var vn in k.GetValueNames())
                                {
                                    var o = k.GetValue(vn) as byte[];
                                    if (o == null || !Sdp.IsKbd(o)) continue;
                                    anyKbd = true;
                                    if (Sdp.IsPatched(o)) anyPatched = true;
                                    else if (Sdp.IsUnpatched(o)) anyUnpatched = true;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            if (!anyKbd) return Status.NoKeyboard;
            if (anyPatched && anyUnpatched) return Status.Mixed;
            if (anyPatched) return Status.Applied;
            return Status.NotApplied;
        }

        void Refresh()
        {
            Status st = GetStatus();
            string txt;
            bool applied = false;
            switch (st)
            {
                case Status.Applied: txt = "状態: 適用済み(有効)"; applied = true; break;
                case Status.NotApplied: txt = "状態: 未適用"; break;
                case Status.Mixed: txt = "状態: 一部のみ適用"; break;
                default: txt = "状態: 対象キーボード未検出"; break;
            }
            _miStatus.Text = txt;
            _miApply.Enabled = (st == Status.NotApplied || st == Status.Mixed);
            _miRestore.Enabled = (st == Status.Applied || st == Status.Mixed) && File.Exists(BackupPath);
            _tray.Icon = applied ? _icoOn : _icoOff;
            _tray.Text = "MagicKeyIME - " + txt;
            _miStartup.Checked = IsStartupRegistered();
        }

        void DoApply()
        {
            int patched = 0, already = 0;
            try
            {
                using (var devs = Registry.LocalMachine.OpenSubKey(Sdp.DevicesPath, false))
                {
                    if (devs == null) { Info("Bluetooth デバイス情報が見つかりません。"); return; }
                    foreach (var dev in devs.GetSubKeyNames())
                    {
                        foreach (var sub in Sdp.Subs)
                        {
                            using (var k = Registry.LocalMachine.OpenSubKey(Sdp.DevicesPath + "\\" + dev + "\\" + sub, true))
                            {
                                if (k == null) continue;
                                foreach (var vn in k.GetValueNames())
                                {
                                    var o = k.GetValue(vn) as byte[];
                                    if (o == null || !Sdp.IsKbd(o)) continue;
                                    if (Sdp.IsPatched(o)) { already++; continue; }
                                    var np = Sdp.PatchBlob(o);
                                    if (np == null) continue;
                                    BackupOnce(dev, sub, vn, o);
                                    k.SetValue(vn, np, RegistryValueKind.Binary);
                                    patched++;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Error("適用に失敗しました:\n" + ex.Message); return; }

            Refresh();
            if (patched > 0)
                AskReboot(string.Format("英数/かなを有効化しました({0}件)。\n反映には再起動が必要です。今すぐ再起動しますか?", patched));
            else if (already > 0)
                Info("すでに適用済みです。");
            else
                Info("対象のキーボードが見つかりませんでした。\nMagic Keyboard が Bluetooth 接続されているか確認してください。");
        }

        void DoRestore()
        {
            if (!File.Exists(BackupPath)) { Info("バックアップ(sdp-backup.txt)が見つかりません。"); return; }
            int restored = 0;
            try
            {
                string[] lines = File.ReadAllLines(BackupPath);
                for (int i = 0; i < lines.Length - 1; i++)
                {
                    var line = lines[i].Trim();
                    if (!line.StartsWith("DEV=")) continue;
                    string dev = Field(line, "DEV="), sub = Field(line, "SUB="), val = Field(line, "VAL=");
                    if (dev == null || sub == null || val == null) continue;
                    byte[] bytes = ParseHex(lines[i + 1].Trim());
                    if (bytes == null) continue;
                    using (var k = Registry.LocalMachine.OpenSubKey(Sdp.DevicesPath + "\\" + dev + "\\" + sub, true))
                    {
                        if (k == null) continue;
                        k.SetValue(val, bytes, RegistryValueKind.Binary);
                        restored++;
                    }
                }
            }
            catch (Exception ex) { Error("解除に失敗しました:\n" + ex.Message); return; }

            Refresh();
            if (restored > 0)
                AskReboot(string.Format("元に戻しました({0}件)。\n反映には再起動が必要です。今すぐ再起動しますか?", restored));
            else
                Info("戻せる項目がありませんでした(対象デバイス不在)。");
        }

        // ---- backup helpers ----
        static void BackupOnce(string dev, string sub, string val, byte[] bytes)
        {
            string key = "DEV=" + dev + " SUB=" + sub + " VAL=" + val;
            if (File.Exists(BackupPath) && File.ReadAllText(BackupPath).Contains(key)) return;
            Directory.CreateDirectory(BackupDir);
            var sb = new StringBuilder();
            if (!File.Exists(BackupPath))
                sb.AppendLine("# MagicKeyIME - original HID descriptor backup (device-aware). Do not delete.");
            sb.AppendLine(key + " LEN=" + bytes.Length);
            var hex = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++) { if (i > 0) hex.Append(' '); hex.Append(bytes[i].ToString("X2")); }
            sb.AppendLine(hex.ToString());
            File.AppendAllText(BackupPath, sb.ToString(), new UTF8Encoding(false));
        }

        static string Field(string line, string tag)
        {
            int p = line.IndexOf(tag); if (p < 0) return null;
            p += tag.Length; int e = line.IndexOf(' ', p);
            return e < 0 ? line.Substring(p) : line.Substring(p, e - p);
        }

        static byte[] ParseHex(string s)
        {
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var b = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out b[i])) return null;
            }
            return b;
        }

        // ---- startup (scheduled task, elevated at logon) ----
        static bool IsStartupRegistered()
        {
            try
            {
                var p = Run("schtasks.exe", "/query /tn \"" + TaskName + "\"");
                return p == 0;
            }
            catch { return false; }
        }

        void ToggleStartup()
        {
            try
            {
                if (IsStartupRegistered())
                {
                    Run("schtasks.exe", "/delete /tn \"" + TaskName + "\" /f");
                    Info("常駐(自動起動)を解除しました。");
                }
                else
                {
                    string exe = Application.ExecutablePath;
                    // 管理者権限(最上位)でログオン時に起動
                    string args = "/create /tn \"" + TaskName + "\" /tr \"\\\"" + exe + "\\\"\" /sc onlogon /rl highest /f";
                    if (Run("schtasks.exe", args) == 0)
                        Info("Windows起動時に(管理者権限で)常駐するよう登録しました。");
                    else
                        Error("自動起動の登録に失敗しました。");
                }
            }
            catch (Exception ex) { Error(ex.Message); }
            _miStartup.Checked = IsStartupRegistered();
        }

        static int Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
        }

        void AskReboot(string msg)
        {
            if (MessageBox.Show(msg, "MagicKeyIME", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true }); }
                catch { }
            }
        }

        void Info(string m) { _tray.ShowBalloonTip(4000, "MagicKeyIME", m, ToolTipIcon.Info); }
        void Error(string m) { MessageBox.Show(m, "MagicKeyIME", MessageBoxButtons.OK, MessageBoxIcon.Error); }

        void ExitApp() { _tray.Visible = false; Application.Exit(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _tray.Visible = false; _tray.Dispose(); }
            base.Dispose(disposing);
        }

        static Icon MakeIcon(bool on)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color bg = on ? Color.FromArgb(38, 138, 74) : Color.FromArgb(90, 90, 96);
                using (var b = new SolidBrush(bg))
                using (var p = Rounded(new Rectangle(0, 0, 31, 31), 7))
                    g.FillPath(b, p);
                using (Font f = new Font("Segoe UI", 19f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("M", f, Brushes.White, new RectangleF(0, 0, 32, 32), sf);
                }
                IntPtr h = bmp.GetHicon();
                Icon ic = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                return ic;
            }
        }
        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "Local\\MagicKeyIME_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("MagicKeyIMEは既に起動しています。", "MagicKeyIME", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }
}
