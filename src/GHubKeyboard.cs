// G Hub Keyboard
// 키보드의 지정한 키로 G Hub 매크로를 실행한다.
//
// 동작 방식
//  1. G Hub 설정(settings.db)에서 게임 프로필별 매크로 목록을 읽어 온다.
//  2. 트리거 키를 누르면 G Hub 에이전트(ws://localhost:9010)에 매크로 START를, 떼면 STOP을 보낸다.
//     G Hub 화면이 매크로를 다룰 때 쓰는 것과 같은 통로라서, 마우스 버튼을 누른 것과 똑같이 동작한다.
//     (누르고 있는 동안 반복 / 토글 / 한 번 실행 등 매크로 설정이 그대로 적용된다)
//  3. 시작할 때 GitHub에서 최신 릴리스 버전 번호를 확인해서, 새 버전이 있으면 하단에 알려 준다.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("G Hub Keyboard")]
[assembly: AssemblyDescription("Trigger Logitech G HUB macros with a keyboard key (unofficial)")]
[assembly: AssemblyProduct("G Hub Keyboard")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Program
{
    const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;

    [DllImport("kernel32.dll")]
    static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);

    [STAThread]
    static void Main()
    {
        // 관리자 권한으로 실행되므로, exe 옆에 심어 둔 가짜 DLL이 대신 로드되지 않게
        // 이후의 DLL 검색을 System32로 제한하고 winsqlite3.dll은 System32에서 미리 로드한다.
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);
        LoadLibraryEx("winsqlite3.dll", IntPtr.Zero, LOAD_LIBRARY_SEARCH_SYSTEM32);

        Mutex mutex = null;
        try
        {
            bool created;
            mutex = new Mutex(true, "GHubKeyboard_SingleInstance", out created);
            if (!created)
            {
                MessageBox.Show("G Hub Keyboard가 이미 실행 중입니다.", "G Hub Keyboard");
                return;
            }
        }
        catch (UnauthorizedAccessException) { }   // 다른 프로그램이 이름을 선점해 막아도 실행은 한다

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        GC.KeepAlive(mutex);
    }
}

// G Hub 설정 파일에서 게임 프로필별 매크로 목록을 읽는다.
static class GHubSettings
{
    public enum Mode { Held, Toggle, Once, PressRelease }

    public class Macro
    {
        public string Id, Name;
        public Mode Mode;
        public override string ToString() { return Name; }
    }

    public class App
    {
        public string Id, Name;
        public List<Macro> Macros = new List<Macro>();
        public override string ToString() { return Name; }
    }

    [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int len, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close(IntPtr db);

    public static List<App> Load()
    {
        string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LGHUB", "settings.db");
        if (!File.Exists(source)) throw new Exception("G Hub 설정 파일을 찾을 수 없습니다.");

        // 복사하지 않고 읽기 전용으로 직접 연다.
        // (관리자 권한으로 사용자 쓰기 가능한 임시 폴더에 파일을 만들면 정션 공격에 악용될 수 있다)
        string json = ReadJson(source);
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var root = (Dictionary<string, object>)serializer.DeserializeObject(json);

        var apps = new Dictionary<string, App>();
        foreach (Dictionary<string, object> a in Items(root, "applications", "applications"))
        {
            var app = new App { Id = Str(a, "applicationId"), Name = Str(a, "name") };
            if (app.Name == "APPLICATION_NAME_DESKTOP") app.Name = "데스크톱 (기본)";
            if (app.Id != null) apps[app.Id] = app;
        }

        foreach (Dictionary<string, object> card in Items(root, "cards", "cards"))
        {
            if (Str(card, "attribute") != "MACRO_PLAYBACK") continue;
            if (card.ContainsKey("readOnly") && Equals(card["readOnly"], true)) continue;
            var macro = Dict(card, "macro");
            if (macro == null || Str(macro, "type") == "ACTION") continue;

            App app;
            string id = Str(card, "id"), name = Str(card, "name"), appId = Str(card, "applicationId");
            // 삭제된 게임의 매크로처럼 프로필이 없는 매크로는 뺀다
            if (id == null || name == null || appId == null || !apps.TryGetValue(appId, out app)) continue;
            app.Macros.Add(new Macro { Id = id, Name = name, Mode = DetectMode(macro) });
        }

        return apps.Values.Where(a => a.Macros.Count > 0).OrderBy(a => a.Name).ToList();
    }

    static Mode DetectMode(Dictionary<string, object> macro)
    {
        var seq = Dict(macro, "sequence");
        if (seq == null) return Mode.Once;
        if (HasComponents(seq, "heldSequence")) return Mode.Held;
        if (HasComponents(seq, "toggleSequence")) return Mode.Toggle;
        if (HasComponents(seq, "pressSequence") || HasComponents(seq, "releaseSequence")) return Mode.PressRelease;
        return Mode.Once;
    }

    static bool HasComponents(Dictionary<string, object> seq, string key)
    {
        var part = Dict(seq, key);
        object list;
        return part != null && part.TryGetValue("components", out list) && list is IEnumerable && ((IEnumerable)list).Cast<object>().Any();
    }

    static string ReadJson(string file)
    {
        IntPtr db, stmt;
        if (sqlite3_open_v2(Encoding.UTF8.GetBytes(file + "\0"), out db, 1 /* READONLY */, IntPtr.Zero) != 0)
            throw new Exception("G Hub 설정 파일을 열 수 없습니다.");
        try
        {
            if (sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes("select file from data order by _id desc limit 1\0"), -1, out stmt, IntPtr.Zero) != 0)
                throw new Exception("G Hub 설정 파일 형식이 예상과 다릅니다.");
            try
            {
                if (sqlite3_step(stmt) != 100 /* SQLITE_ROW */) throw new Exception("G Hub 설정이 비어 있습니다.");
                int len = sqlite3_column_bytes(stmt, 0);
                var bytes = new byte[len];
                Marshal.Copy(sqlite3_column_blob(stmt, 0), bytes, 0, len);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { sqlite3_finalize(stmt); }
        }
        finally { sqlite3_close(db); }
    }

    static IEnumerable Items(Dictionary<string, object> root, string a, string b)
    {
        var outer = Dict(root, a);
        object inner;
        if (outer == null || !outer.TryGetValue(b, out inner)) return new object[0];
        var list = inner as IEnumerable;
        if (list == null) return new object[0];
        return list.OfType<Dictionary<string, object>>();
    }

    static Dictionary<string, object> Dict(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
    }

    static string Str(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) ? v as string : null;
    }
}

// G Hub 에이전트와의 연결. G Hub 화면(lghub.exe)이 쓰는 것과 같은 로컬 웹소켓이다.
class GHubAgent
{
    public event Action<bool> ConnectionChanged;   // 백그라운드 스레드에서 호출됨
    public event Action<string> Error;

    readonly BlockingCollection<string> queue = new BlockingCollection<string>();
    volatile bool connected, stopping;
    int messageId;

    public bool Connected { get { return connected; } }

    public void Start()
    {
        new Thread(Run) { IsBackground = true }.Start();
    }

    public void Stop()
    {
        stopping = true;
    }

    // 연결되지 않았을 때는 버린다 (나중에 늦게 전송되면 엉뚱한 때 매크로가 실행되므로)
    public bool PlayMacro(string macroId, bool start)
    {
        if (!connected) return false;
        // 매크로 ID는 G Hub 설정에서 온 값이므로, JSON을 깨뜨릴 수 있는 형식이면 보내지 않는다
        if (string.IsNullOrEmpty(macroId) || !Regex.IsMatch(macroId, "^[A-Za-z0-9-]+$")) return false;
        string id = Interlocked.Increment(ref messageId).ToString();
        queue.Add("{\"msgId\":\"" + id + "\",\"verb\":\"SET\",\"path\":\"/macro/playback\",\"payload\":{\"macroId\":\""
                  + macroId + "\",\"operation\":\"" + (start ? "START" : "STOP") + "\",\"triggerId\":\"ghubkeyboard\"}}");
        return true;
    }

    void Run()
    {
        while (!stopping)
        {
            using (var ws = new ClientWebSocket())
            {
                try
                {
                    ws.Options.AddSubProtocol("json");
                    ws.Options.SetRequestHeader("Origin", "file://");
                    if (!ws.ConnectAsync(new Uri("ws://localhost:9010"), CancellationToken.None).Wait(3000))
                        throw new TimeoutException();

                    string ignored;
                    while (queue.TryTake(out ignored)) { }
                    SetConnected(true);

                    var reader = new Thread(() => ReadLoop(ws)) { IsBackground = true };
                    reader.Start();

                    while (!stopping && ws.State == WebSocketState.Open)
                    {
                        string message;
                        if (!queue.TryTake(out message, 300)) continue;
                        var bytes = Encoding.UTF8.GetBytes(message);
                        ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
                    }
                }
                catch { }
            }
            SetConnected(false);
            if (!stopping) Thread.Sleep(2000);   // G Hub가 꺼져 있으면 잠시 후 다시 연결
        }
    }

    void ReadLoop(ClientWebSocket ws)
    {
        var buffer = new byte[1 << 16];
        var text = new StringBuilder();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).Result;
                if (result.MessageType == WebSocketMessageType.Close) break;
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (text.Length > 4 * 1024 * 1024) break;   // 비정상적으로 큰 메시지는 연결을 끊는다 (메모리 고갈 방지)
                if (!result.EndOfMessage) continue;

                string message = text.ToString();
                text.Clear();
                if (!message.Contains("/macro/playback")) continue;
                var code = Regex.Match(message, "\"code\"\\s*:\\s*\"([A-Z_]+)\"");
                if (code.Success && code.Groups[1].Value != "SUCCESS" && Error != null)
                {
                    var what = Regex.Match(message, "\"what\"\\s*:\\s*\"([^\"]*)\"");
                    Error(code.Groups[1].Value + (what.Success ? " - " + what.Groups[1].Value : ""));
                }
            }
        }
        catch { }
        try { ws.Abort(); } catch { }   // 읽기가 끝나면 연결을 닫아 다시 연결하게 한다
    }

    void SetConnected(bool value)
    {
        if (connected == value) return;
        connected = value;
        if (ConnectionChanged != null) ConnectionChanged(value);
    }
}

class MainForm : Form
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const uint LLKHF_INJECTED = 0x10;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    const uint WM_QUIT = 0x0012;

    // Raw Input: 어느 장치에서 온 키 입력인지 알 수 있다.
    // G Hub 매크로는 "G Hub 가상 키보드"에서 나오므로, 이걸로 실제 키보드 입력과 구분한다.
    const int WM_INPUT = 0x00FF;
    const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    const uint RIDEV_INPUTSINK = 0x100, RIDEV_DEVNOTIFY = 0x2000, RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007;
    const ushort RI_KEY_BREAK = 0x1, RI_KEY_E0 = 0x2;
    const string GHubVirtualKeyboard = "VID_046D&PID_C232";

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);

    const string AppVersion = "1.0.0";
    const string Repo = "byseop/ghub-keyboard";
    const string RepoUrl = "https://github.com/" + Repo;
    const string SiteUrl = "https://gamer4.info";

    readonly HookProc proc;   // GC에 수거되지 않도록 필드로 보관
    IntPtr hook;
    uint hookThreadId;
    IntPtr heldDevice;
    readonly Dictionary<IntPtr, bool> isGHubDevice = new Dictionary<IntPtr, bool>();

    readonly GHubAgent agent = new GHubAgent();

    // 설정 (키 감지 스레드에서도 읽는다)
    volatile Keys trigger = Keys.F8;
    volatile bool suppress = true;
    bool checkUpdates = true;
    string selectedAppId, selectedMacroId;

    const string SettingsKey = @"Software\GHubKeyboard";
    readonly string legacySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GHubKeyboard", "settings.txt");

    List<GHubSettings.App> apps = new List<GHubSettings.App>();
    volatile bool capturing;
    bool loadingUi;

    // 아래 상태는 키 감지 스레드와 화면 스레드가 함께 쓰므로 sync로 보호한다
    readonly object sync = new object();
    bool held;
    GHubSettings.Macro currentMacro;   // 트리거 키로 실행할 매크로
    GHubSettings.Macro playing;        // 지금 실행 중인 매크로 (STOP을 보낼 대상)
    GHubSettings.Macro toggledOn;      // 켜져 있는 토글형 매크로 (끌 때 한 번 더 눌러 주기 위해)

    ComboBox appBox, macroBox;
    Label modeLabel, keyLabel, connectionLabel, stateLabel;
    CheckBox suppressBox, updateBox;
    LinkLabel updateLink;

    public MainForm()
    {
        LoadSettings();
        BuildUi();
        ReloadMacros(false);
        proc = HookCallback;
        StartHookThread();

        agent.ConnectionChanged += connected =>
        {
            if (!connected) ResetPlayback();
            Ui(UpdateConnection);
        };
        agent.Error += message => Ui(() =>
        {
            stateLabel.Text = "G Hub 오류: " + message;
            stateLabel.BackColor = Color.FromArgb(255, 222, 200);
        });
        agent.Start();
    }

    GHubSettings.Macro SelectedMacro { get { return macroBox.SelectedItem as GHubSettings.Macro; } }

    // ───────────────────────── 화면 ─────────────────────────

    void BuildUi()
    {
        loadingUi = true;
        Text = "G Hub Keyboard v" + AppVersion;
        Font = new Font("Malgun Gothic", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 368);

        Controls.Add(new Label { Text = "게임 프로필", Location = new Point(20, 22), AutoSize = true });
        appBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 18), Size = new Size(185, 28) };
        appBox.SelectedIndexChanged += (s, e) => { if (!loadingUi) OnAppChanged(); };
        Controls.Add(appBox);

        var reloadButton = new Button { Text = "새로고침", Location = new Point(302, 17), Size = new Size(78, 29) };
        reloadButton.Click += (s, e) => ReloadMacros(true);
        Controls.Add(reloadButton);

        Controls.Add(new Label { Text = "매크로", Location = new Point(20, 58), AutoSize = true });
        macroBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 54), Size = new Size(270, 28) };
        macroBox.SelectedIndexChanged += (s, e) => { if (!loadingUi) OnMacroChanged(); };
        Controls.Add(macroBox);

        modeLabel = new Label { Location = new Point(110, 86), AutoSize = true, ForeColor = SystemColors.GrayText };
        Controls.Add(modeLabel);

        keyLabel = new Label { Location = new Point(20, 122), AutoSize = true, Font = new Font("Malgun Gothic", 12f, FontStyle.Bold) };
        Controls.Add(keyLabel);
        var changeButton = new Button { Text = "키 변경", Location = new Point(270, 117), Size = new Size(110, 32) };
        changeButton.Click += (s, e) => StartCapture();
        Controls.Add(changeButton);

        suppressBox = new CheckBox { Text = "트리거 키의 원래 입력 막기", Location = new Point(20, 162), AutoSize = true, Checked = suppress };
        suppressBox.CheckedChanged += (s, e) =>
        {
            if (suppress == suppressBox.Checked) return;
            ReleaseTrigger();
            suppress = suppressBox.Checked;
            SaveSettings();
        };
        Controls.Add(suppressBox);

        updateBox = new CheckBox { Text = "시작할 때 새 버전 확인", Location = new Point(20, 188), AutoSize = true, Checked = checkUpdates };
        updateBox.CheckedChanged += (s, e) => { checkUpdates = updateBox.Checked; SaveSettings(); };
        Controls.Add(updateBox);

        connectionLabel = new Label { Location = new Point(20, 222), Size = new Size(360, 22) };
        Controls.Add(connectionLabel);

        stateLabel = new Label { Location = new Point(20, 254), Size = new Size(360, 48), TextAlign = ContentAlignment.MiddleCenter, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(stateLabel);

        // 제작자 사이트
        var siteLink = new LinkLabel { Text = "지금 할인 중인 스팀 게임 보기 → gamer4.info", Location = new Point(20, 312), AutoSize = true };
        siteLink.LinkClicked += (s, e) => OpenUrl(SiteUrl);
        Controls.Add(siteLink);

        // 하단: 버전 / GitHub 링크, 새 버전 알림
        var versionLink = new LinkLabel { Text = "v" + AppVersion + " · GitHub", Location = new Point(20, 338), AutoSize = true, LinkColor = SystemColors.GrayText };
        versionLink.LinkClicked += (s, e) => OpenUrl(RepoUrl);
        Controls.Add(versionLink);

        updateLink = new LinkLabel { Location = new Point(180, 338), Size = new Size(200, 22), TextAlign = ContentAlignment.TopRight, Visible = false };
        updateLink.LinkClicked += (s, e) => OpenUrl(updateLink.Tag as string);
        Controls.Add(updateLink);

        UpdateKeyLabel();
        UpdateConnection();
        UpdateState();
        loadingUi = false;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (checkUpdates) CheckForUpdate();
    }

    // GitHub에서 최신 릴리스 버전 번호만 확인한다. 다운로드나 설치는 하지 않는다.
    void CheckForUpdate()
    {
        new Thread(() =>
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
                var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
                request.UserAgent = "GHubKeyboard/" + AppVersion;
                request.Accept = "application/vnd.github+json";
                request.Timeout = 5000;
                string json;
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    json = reader.ReadToEnd();

                var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
                Version latest;
                if (!tag.Success || !Version.TryParse(tag.Groups[1].Value, out latest)) return;
                if (latest <= Version.Parse(AppVersion)) return;

                var page = Regex.Match(json, "\"html_url\"\\s*:\\s*\"([^\"]*/releases/tag/[^\"]*)\"");
                // 이 저장소의 릴리스 페이지 주소일 때만 쓴다 (조작된 응답으로 엉뚱한 주소가 열리지 않게)
                string url = RepoUrl + "/releases/latest";
                if (page.Success && Regex.IsMatch(page.Groups[1].Value, "^" + Regex.Escape(RepoUrl + "/releases/tag/") + "[A-Za-z0-9._-]+$"))
                    url = page.Groups[1].Value;
                Ui(() =>
                {
                    updateLink.Text = "새 버전 v" + tag.Groups[1].Value + " 있음 →";
                    updateLink.Tag = url;
                    updateLink.Visible = true;
                });
            }
            catch { }   // 오프라인이거나 저장소가 비공개면 조용히 넘어간다
        }) { IsBackground = true }.Start();
    }

    // 관리자 권한으로 실행 중이므로, 브라우저가 관리자 권한으로 뜨지 않게 탐색기를 거쳐 연다.
    // 탐색기는 전체 경로로 실행하고(exe 폴더의 가짜 explorer.exe 방지), https 주소만 연다.
    static void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://") || url.IndexOf('"') >= 0) return;
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try { Process.Start(explorer, "\"" + url + "\""); } catch { }
    }

    void ReloadMacros(bool showError)
    {
        try
        {
            apps = GHubSettings.Load();
        }
        catch (Exception ex)
        {
            apps = new List<GHubSettings.App>();
            if (showError) MessageBox.Show("G Hub 매크로를 읽지 못했습니다.\n" + ex.Message, "G Hub Keyboard");
        }

        loadingUi = true;
        appBox.Items.Clear();
        foreach (var app in apps) appBox.Items.Add(app);
        var selected = apps.FirstOrDefault(a => a.Id == selectedAppId) ?? apps.FirstOrDefault();
        if (selected != null) appBox.SelectedItem = selected;
        loadingUi = false;
        OnAppChanged();
    }

    void OnAppChanged()
    {
        var app = appBox.SelectedItem as GHubSettings.App;
        loadingUi = true;
        macroBox.Items.Clear();
        if (app != null)
        {
            selectedAppId = app.Id;
            foreach (var m in app.Macros) macroBox.Items.Add(m);
            var macro = app.Macros.FirstOrDefault(m => m.Id == selectedMacroId) ?? app.Macros.FirstOrDefault();
            if (macro != null) macroBox.SelectedItem = macro;
        }
        loadingUi = false;
        OnMacroChanged();
    }

    void OnMacroChanged()
    {
        ReleaseTrigger();
        TurnOffToggle();
        var macro = SelectedMacro;
        lock (sync) currentMacro = macro;
        selectedMacroId = macro == null ? null : macro.Id;
        modeLabel.Text = macro == null ? "" : "종류: " + ModeText(macro.Mode);
        SaveSettings();
        UpdateState();
    }

    static string ModeText(GHubSettings.Mode mode)
    {
        switch (mode)
        {
            case GHubSettings.Mode.Held: return "누르고 있는 동안 반복";
            case GHubSettings.Mode.Toggle: return "토글 (한 번 누르면 켜지고, 다시 누르면 꺼짐)";
            case GHubSettings.Mode.PressRelease: return "누를 때 / 뗄 때 실행";
            default: return "한 번 실행";
        }
    }

    void UpdateKeyLabel()
    {
        keyLabel.Text = "트리거 키: " + trigger;

        // 매크로가 보조키를 누르는 경우가 많고, 입력을 막으면 G Hub 매크로 입력까지 막히므로 보조키는 막지 않는다
        bool modifier = trigger == Keys.LControlKey || trigger == Keys.RControlKey
                     || trigger == Keys.LShiftKey || trigger == Keys.RShiftKey
                     || trigger == Keys.LMenu || trigger == Keys.RMenu;
        if (modifier) suppressBox.Checked = false;
        suppressBox.Enabled = !modifier;
        suppressBox.Text = modifier ? "트리거 키의 원래 입력 막기 (Ctrl/Shift/Alt는 불가)" : "트리거 키의 원래 입력 막기";
    }

    void UpdateConnection()
    {
        bool ok = agent.Connected;
        connectionLabel.Text = ok ? "● G Hub 연결됨" : "● G Hub에 연결하는 중... (G Hub가 켜져 있는지 확인하세요)";
        connectionLabel.ForeColor = ok ? Color.FromArgb(0, 120, 60) : Color.FromArgb(192, 80, 0);
        UpdateState();
    }

    void UpdateState()
    {
        GHubSettings.Macro running, macro;
        lock (sync)
        {
            running = playing ?? toggledOn;
            macro = currentMacro;
        }
        if (running != null)
        {
            stateLabel.Text = "'" + running.Name + "' 실행 중";
            stateLabel.BackColor = Color.FromArgb(198, 239, 206);
        }
        else
        {
            stateLabel.Text = macro == null ? "G Hub에 매크로가 없습니다" : "대기 중 - " + trigger + " 키를 누르면 '" + macro.Name + "' 실행";
            stateLabel.BackColor = SystemColors.Control;
        }
    }

    // ───────────────────────── 매크로 실행 ─────────────────────────
    // 키 감지 스레드와 화면 스레드 양쪽에서 호출된다.

    void TriggerDown(IntPtr device)
    {
        bool sent;
        lock (sync)
        {
            if (held || currentMacro == null) return;
            held = true;
            heldDevice = device;
            sent = agent.PlayMacro(currentMacro.Id, true);
            if (sent)
            {
                playing = currentMacro;
                if (currentMacro.Mode == GHubSettings.Mode.Toggle)
                    toggledOn = toggledOn == null ? currentMacro : null;
            }
        }
        if (sent) Ui(UpdateState);
        else Ui(() =>
        {
            stateLabel.Text = "G Hub에 연결되지 않아 실행할 수 없습니다";
            stateLabel.BackColor = Color.FromArgb(255, 222, 200);
        });
    }

    void TriggerUp(IntPtr device)
    {
        lock (sync)
        {
            if (!held || device != heldDevice) return;
            held = false;
            if (playing == null) return;
            agent.PlayMacro(playing.Id, false);
            playing = null;
        }
        Ui(UpdateState);
    }

    void ReleaseTrigger()
    {
        lock (sync)
        {
            if (!held) return;
            TriggerUp(heldDevice);
        }
    }

    // 토글형 매크로가 켜진 채로 남지 않게 한 번 더 눌러서 끈다
    void TurnOffToggle()
    {
        lock (sync)
        {
            if (toggledOn == null) return;
            agent.PlayMacro(toggledOn.Id, true);
            agent.PlayMacro(toggledOn.Id, false);
            toggledOn = null;
        }
        Ui(UpdateState);
    }

    void ResetPlayback()
    {
        lock (sync)
        {
            held = false;
            playing = null;
            toggledOn = null;
        }
    }

    // ───────────────────────── 키 입력 ─────────────────────────

    // 키보드 후킹은 전용 스레드에서 돌린다.
    // 후킹 처리가 늦으면 윈도우가 후킹을 조용히 해제하는데, 화면 스레드가 바쁠 때도 영향을 받지 않게 하기 위해서다.
    void StartHookThread()
    {
        var thread = new Thread(() =>
        {
            hookThreadId = GetCurrentThreadId();
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Ui(() => MessageBox.Show("키보드 후킹에 실패했습니다. (오류 " + error + ")", "G Hub Keyboard"));
                return;
            }
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(hook);
        }) { IsBackground = true };
        thread.Start();
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            bool injected = (info.flags & LLKHF_INJECTED) != 0;
            // 다른 프로그램이 보낸 가상 입력은 무시. 키 변경 중에는 아무 키도 막지 않는다.
            if (!injected && !capturing)
            {
                int msg = wParam.ToInt32();
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                var key = (Keys)info.vkCode;

                // 입력을 막는 경우에만 후킹으로 처리한다.
                // 막지 않는 경우에는 장치를 구분할 수 있는 Raw Input(WndProc)에서 처리한다.
                if (suppress && key == trigger)
                {
                    if (down) TriggerDown(IntPtr.Zero);
                    else if (up) TriggerUp(IntPtr.Zero);
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var device = new RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, Target = Handle };
        if (!RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
            MessageBox.Show("Raw Input 등록에 실패했습니다. (오류 " + Marshal.GetLastWin32Error() + ")", "G Hub Keyboard");
    }

    // 키 변경은 Raw Input으로 받는다.
    // 이 창이 맨 앞에 있으면 (Raw Input을 등록한 탓에) 키보드 후킹이 호출되지 않기 때문이다.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT && (capturing || !suppress))
            HandleRawInput(m.LParam);
        else if (m.Msg == WM_INPUT_DEVICE_CHANGE)
            isGHubDevice.Clear();   // 장치를 뽑았다 꽂으면 핸들이 재사용될 수 있으므로 다시 확인한다
        base.WndProc(ref m);
    }

    // 키 변경 중에는 이 창의 버튼/목록이 키 입력에 반응하지 않게 한다 (Space로 버튼이 눌리는 등)
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (capturing) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void HandleRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)(8 + 2 * IntPtr.Size);   // RAWINPUTHEADER
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize) != size) return;
            if (Marshal.ReadInt32(buffer, 0) != 1) return;   // RIM_TYPEKEYBOARD
            IntPtr device = Marshal.ReadIntPtr(buffer, 8);
            int offset = (int)headerSize;                     // RAWKEYBOARD
            ushort makeCode = (ushort)Marshal.ReadInt16(buffer, offset);
            ushort flags = (ushort)Marshal.ReadInt16(buffer, offset + 2);
            ushort vkey = (ushort)Marshal.ReadInt16(buffer, offset + 6);

            // 다른 프로그램이 보낸 입력(device 0)과 G Hub 매크로 입력은 무시
            bool fromKeyboard = device != IntPtr.Zero && !IsGHubDevice(device);
            if (!fromKeyboard) return;
            var key = NormalizeKey(vkey, makeCode, flags);
            bool up = (flags & RI_KEY_BREAK) != 0;

            if (capturing)
            {
                if (!up) FinishCapture(key);
                return;
            }

            if (key != trigger) return;
            if (!up) TriggerDown(device);
            else TriggerUp(device);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    bool IsGHubDevice(IntPtr device)
    {
        bool result;
        if (isGHubDevice.TryGetValue(device, out result)) return result;
        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, null, ref size);
        var name = new StringBuilder((int)size + 1);
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, name, ref size);
        result = name.ToString().ToUpperInvariant().Contains(GHubVirtualKeyboard);
        isGHubDevice[device] = result;
        return result;
    }

    // Raw Input은 Ctrl/Shift/Alt를 좌우 구분 없이 주므로 후킹과 같은 키 코드로 맞춘다
    static Keys NormalizeKey(ushort vkey, ushort makeCode, ushort flags)
    {
        bool e0 = (flags & RI_KEY_E0) != 0;
        switch ((Keys)vkey)
        {
            case Keys.ControlKey: return e0 ? Keys.RControlKey : Keys.LControlKey;
            case Keys.Menu: return e0 ? Keys.RMenu : Keys.LMenu;
            case Keys.ShiftKey: return makeCode == 0x36 ? Keys.RShiftKey : Keys.LShiftKey;
            default: return (Keys)vkey;
        }
    }

    void StartCapture()
    {
        ReleaseTrigger();
        ActiveControl = null;   // 누른 키가 버튼이나 목록에 전달되지 않게 포커스를 뺀다
        capturing = true;
        keyLabel.Text = "새 키를 누르세요 (Esc: 취소)";
    }

    void FinishCapture(Keys key)
    {
        if (!capturing) return;
        capturing = false;
        if (key != Keys.Escape)
        {
            trigger = key;
            SaveSettings();
        }
        UpdateKeyLabel();
        UpdateState();
    }

    void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    // ───────────────────────── 설정 저장 ─────────────────────────

    // 설정은 레지스트리(HKCU\Software\GHubKeyboard)에 저장한다.
    // 관리자 권한으로 사용자 폴더에 파일을 쓰면 링크/정션을 이용한 임의 파일 쓰기에 악용될 수 있기 때문이다.
    void LoadSettings()
    {
        try
        {
            using (var reg = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                if (reg != null)
                {
                    foreach (var name in reg.GetValueNames())
                        ApplySetting(name, reg.GetValue(name) as string);
                    return;
                }
            }
            // 예전 버전의 설정 파일이 있으면 한 번 읽어 온다 (읽기만 한다)
            if (!File.Exists(legacySettingsPath)) return;
            foreach (var line in File.ReadAllLines(legacySettingsPath, Encoding.UTF8))
            {
                var parts = line.Split(new[] { '=' }, 2);
                if (parts.Length == 2) ApplySetting(parts[0], parts[1]);
            }
        }
        catch { }
    }

    void ApplySetting(string key, string value)
    {
        if (value == null) return;
        if (key == "key")
        {
            Keys k;
            if (Enum.TryParse(value, out k)) trigger = k;
        }
        else if (key == "suppress") suppress = value == "1";
        else if (key == "updateCheck") checkUpdates = value == "1";
        else if (key == "app") selectedAppId = value;
        else if (key == "macroId") selectedMacroId = value;
    }

    void SaveSettings()
    {
        if (loadingUi) return;
        try
        {
            using (var reg = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                reg.SetValue("key", trigger.ToString());
                reg.SetValue("suppress", suppress ? "1" : "0");
                reg.SetValue("updateCheck", checkUpdates ? "1" : "0");
                reg.SetValue("app", selectedAppId ?? "");
                reg.SetValue("macroId", selectedMacroId ?? "");
            }
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 매크로가 켜진 채로 남지 않게 정리하고, 전송될 시간을 잠깐 준다
        bool pending;
        lock (sync) pending = playing != null || toggledOn != null;
        ReleaseTrigger();
        TurnOffToggle();
        if (pending) Thread.Sleep(300);
        agent.Stop();
        if (hookThreadId != 0) PostThreadMessage(hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        base.OnFormClosing(e);
    }
}
