// G Hub Keyboard
// 키보드의 지정한 키로 G Hub 매크로를 실행한다.
//
// 동작 방식
//  1. G Hub 설정(settings.db)에서 게임 프로필별 매크로 목록을 읽어 온다.
//  2. 트리거 키를 누르면 G Hub 에이전트(ws://localhost:9010)에 매크로 START를, 떼면 STOP을 보낸다.
//     G Hub 화면이 매크로를 다룰 때 쓰는 것과 같은 통로라서, 마우스 버튼을 누른 것과 똑같이 동작한다.
//     (누르고 있는 동안 반복 / 토글 / 한 번 실행 등 매크로 설정이 그대로 적용된다)
//     키와 매크로의 짝을 여러 개 등록할 수 있고, 키마다 다른 트리거 ID를 써서 각각 독립적으로 동작한다.
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
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

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

    // 연결되지 않았을 때는 버린다 (나중에 늦게 전송되면 엉뚱한 때 매크로가 실행되므로).
    // triggerId는 G Hub가 "어느 버튼이 눌렸는지"를 구분하는 값이라, 키마다 다르게 주면 매크로를 동시에 따로 돌릴 수 있다.
    public bool PlayMacro(string macroId, string triggerId, bool start)
    {
        if (!connected) return false;
        // 매크로 ID는 G Hub 설정에서 온 값이므로, JSON을 깨뜨릴 수 있는 형식이면 보내지 않는다
        if (string.IsNullOrEmpty(macroId) || !Regex.IsMatch(macroId, "^[A-Za-z0-9-]+$")) return false;
        if (string.IsNullOrEmpty(triggerId) || !Regex.IsMatch(triggerId, "^[A-Za-z0-9_-]+$")) return false;
        string id = Interlocked.Increment(ref messageId).ToString();
        queue.Add("{\"msgId\":\"" + id + "\",\"verb\":\"SET\",\"path\":\"/macro/playback\",\"payload\":{\"macroId\":\""
                  + macroId + "\",\"operation\":\"" + (start ? "START" : "STOP") + "\",\"triggerId\":\"" + triggerId + "\"}}");
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

// 트리거 키 하나와 G Hub 매크로 하나의 짝
class Mapping
{
    public Keys Key;
    public string AppId, MacroId;
    public volatile bool Enabled = true;   // 끄면 키를 눌러도 실행하지 않고, 키 입력도 막지 않는다

    // 실행 상태 (MainForm.sync로 보호)
    public bool Held, Playing, ToggledOn;
    public IntPtr HeldDevice;

    // 키마다 다른 트리거 ID를 써서 G Hub가 각 키를 별개의 버튼처럼 다루게 한다
    public string TriggerId { get { return "ghubkeyboard-" + Key; } }
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

    const string AppVersion = "1.1.0";
    const string Repo = "byseop/ghub-keyboard";
    const string RepoUrl = "https://github.com/" + Repo;
    const string SiteUrl = "https://gamer4.info";

    readonly HookProc proc;   // GC에 수거되지 않도록 필드로 보관
    IntPtr hook;
    uint hookThreadId;
    readonly Dictionary<IntPtr, bool> isGHubDevice = new Dictionary<IntPtr, bool>();

    readonly GHubAgent agent = new GHubAgent();

    // 설정. 키 감지 스레드에서도 읽으므로, 목록은 바꿀 때마다 새 배열로 통째로 교체한다.
    volatile Mapping[] mappings = new Mapping[0];
    volatile bool suppress = true;
    bool checkUpdates = true;

    // G Hub 매크로 정보 (ID → 매크로 / 게임 프로필). 다시 읽을 때 통째로 교체한다.
    volatile Dictionary<string, GHubSettings.Macro> macrosById = new Dictionary<string, GHubSettings.Macro>();
    Dictionary<string, GHubSettings.App> appByMacroId = new Dictionary<string, GHubSettings.App>();
    List<GHubSettings.App> apps = new List<GHubSettings.App>();

    const string SettingsKey = @"Software\GHubKeyboard";
    readonly string legacySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GHubKeyboard", "settings.txt");

    volatile bool capturing;   // 키 변경 중
    volatile bool paused;      // 목록 편집 중에는 매크로를 실행하지 않는다
    Action<Keys> captureCallback;

    // 매핑의 실행 상태는 키 감지 스레드와 화면 스레드가 함께 쓰므로 sync로 보호한다
    readonly object sync = new object();

    ListView list;
    bool refreshingList, ignoreNextCheck, shown;
    Button editButton, deleteButton;
    Label connectionLabel, statusLabel;
    CheckBox suppressBox, updateBox;
    LinkLabel updateLink;

    public bool Capturing { get { return capturing; } }

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
            Ui(() => { UpdateConnection(); RefreshStates(); });
        };
        agent.Error += message => Ui(() => ShowStatus("G Hub 오류: " + message, true));
        agent.Start();
    }

    // ───────────────────────── 화면 ─────────────────────────

    void BuildUi()
    {
        Text = "G Hub Keyboard v" + AppVersion;
        Font = new Font("Malgun Gothic", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(540, 432);

        list = new ListView
        {
            View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, MultiSelect = false,
            CheckBoxes = true, Location = new Point(20, 20), Size = new Size(500, 190)
        };
        list.Columns.Add("사용 / 트리거 키", 118);
        list.Columns.Add("게임 프로필", 85);
        list.Columns.Add("매크로", 100);
        list.Columns.Add("종류", 132);
        list.Columns.Add("상태", 58);
        list.ItemChecked += (s, e) => OnItemChecked(e.Item);
        // 체크박스 목록은 두 번 클릭하면 체크가 바뀌는데, 두 번 클릭은 수정용으로 쓰므로 그때는 체크를 바꾸지 않는다
        list.MouseDown += (s, e) => ignoreNextCheck = e.Clicks > 1;
        list.ItemCheck += (s, e) => { if (ignoreNextCheck) { e.NewValue = e.CurrentValue; ignoreNextCheck = false; } };
        list.DoubleClick += (s, e) => EditMapping();
        list.SelectedIndexChanged += (s, e) => UpdateButtons();
        list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) DeleteMapping(); };
        Controls.Add(list);

        var addButton = new Button { Text = "추가", Location = new Point(20, 220), Size = new Size(80, 30) };
        addButton.Click += (s, e) => AddMapping();
        Controls.Add(addButton);

        editButton = new Button { Text = "수정", Location = new Point(106, 220), Size = new Size(80, 30) };
        editButton.Click += (s, e) => EditMapping();
        Controls.Add(editButton);

        deleteButton = new Button { Text = "삭제", Location = new Point(192, 220), Size = new Size(80, 30) };
        deleteButton.Click += (s, e) => DeleteMapping();
        Controls.Add(deleteButton);

        var reloadButton = new Button { Text = "새로고침", Location = new Point(440, 220), Size = new Size(80, 30) };
        reloadButton.Click += (s, e) => ReloadMacros(true);
        Controls.Add(reloadButton);

        // 매크로가 보조키를 누르는 경우가 많고, 입력을 막으면 G Hub 매크로 입력까지 막히므로 보조키는 막지 않는다
        suppressBox = new CheckBox { Text = "트리거 키의 원래 입력 막기 (Ctrl/Shift/Alt는 항상 전달)", Location = new Point(20, 262), AutoSize = true, Checked = suppress };
        suppressBox.CheckedChanged += (s, e) =>
        {
            if (suppress == suppressBox.Checked) return;
            StopAll();
            suppress = suppressBox.Checked;
            SaveSettings();
        };
        Controls.Add(suppressBox);

        updateBox = new CheckBox { Text = "시작할 때 새 버전 확인", Location = new Point(20, 288), AutoSize = true, Checked = checkUpdates };
        updateBox.CheckedChanged += (s, e) => { checkUpdates = updateBox.Checked; SaveSettings(); };
        Controls.Add(updateBox);

        connectionLabel = new Label { Location = new Point(20, 320), Size = new Size(500, 22) };
        Controls.Add(connectionLabel);

        statusLabel = new Label { Location = new Point(20, 344), Size = new Size(500, 22), AutoEllipsis = true };
        Controls.Add(statusLabel);

        // 제작자 사이트
        var siteLink = new LinkLabel { Text = "지금 할인 중인 스팀 게임 보기 → gamer4.info", Location = new Point(20, 378), AutoSize = true };
        siteLink.LinkClicked += (s, e) => OpenUrl(SiteUrl);
        Controls.Add(siteLink);

        // 하단: 버전 / GitHub 링크, 새 버전 알림
        var versionLink = new LinkLabel { Text = "v" + AppVersion + " · GitHub", Location = new Point(20, 404), AutoSize = true, LinkColor = SystemColors.GrayText };
        versionLink.LinkClicked += (s, e) => OpenUrl(RepoUrl);
        Controls.Add(versionLink);

        updateLink = new LinkLabel { Location = new Point(320, 404), Size = new Size(200, 22), TextAlign = ContentAlignment.TopRight, Visible = false };
        updateLink.LinkClicked += (s, e) => OpenUrl(updateLink.Tag as string);
        Controls.Add(updateLink);

        UpdateConnection();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        shown = true;
        RefreshList();   // 창이 만들어지는 동안 바뀌었을 수 있는 체크 상태를 설정값으로 맞춘다
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
        StopAll();
        try
        {
            apps = GHubSettings.Load();
        }
        catch (Exception ex)
        {
            apps = new List<GHubSettings.App>();
            if (showError) MessageBox.Show("G Hub 매크로를 읽지 못했습니다.\n" + ex.Message, "G Hub Keyboard");
        }

        var byId = new Dictionary<string, GHubSettings.Macro>();
        var appOf = new Dictionary<string, GHubSettings.App>();
        foreach (var app in apps)
            foreach (var macro in app.Macros)
            {
                byId[macro.Id] = macro;
                appOf[macro.Id] = app;
            }
        appByMacroId = appOf;
        macrosById = byId;
        RefreshList();
    }

    void UpdateConnection()
    {
        bool ok = agent.Connected;
        connectionLabel.Text = ok ? "● G Hub 연결됨" : "● G Hub에 연결하는 중... (G Hub가 켜져 있는지 확인하세요)";
        connectionLabel.ForeColor = ok ? Color.FromArgb(0, 120, 60) : Color.FromArgb(192, 80, 0);
    }

    void ShowStatus(string text, bool error)
    {
        statusLabel.Text = text;
        statusLabel.ForeColor = error ? Color.FromArgb(192, 60, 0) : SystemColors.ControlText;
    }

    // 목록 전체를 다시 그린다 (매핑이나 G Hub 매크로가 바뀌었을 때)
    void RefreshList()
    {
        int selected = list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1;
        refreshingList = true;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var m in mappings)
        {
            GHubSettings.Macro macro;
            GHubSettings.App app;
            bool found = macrosById.TryGetValue(m.MacroId ?? "", out macro);
            appByMacroId.TryGetValue(m.MacroId ?? "", out app);
            var item = new ListViewItem(KeyName(m.Key));
            item.SubItems.Add(app != null ? app.Name : "-");
            item.SubItems.Add(found ? macro.Name : "(G Hub에 없음)");
            item.SubItems.Add(found ? ModeText(macro.Mode) : "-");
            item.SubItems.Add("");
            item.Checked = m.Enabled;
            list.Items.Add(item);
        }
        if (selected >= 0 && selected < list.Items.Count) list.Items[selected].Selected = true;
        list.EndUpdate();
        refreshingList = false;
        UpdateButtons();
        RefreshStates();
    }

    // 실행 상태만 갱신한다
    void RefreshStates()
    {
        var current = mappings;
        var running = new List<string>();
        lock (sync)
        {
            for (int i = 0; i < current.Length && i < list.Items.Count; i++)
            {
                var item = list.Items[i];
                bool on = current[i].Playing || current[i].ToggledOn;
                bool usable = current[i].Enabled && macrosById.ContainsKey(current[i].MacroId ?? "");
                item.UseItemStyleForSubItems = false;
                foreach (ListViewItem.ListViewSubItem sub in item.SubItems)
                    sub.ForeColor = usable ? SystemColors.ControlText : SystemColors.GrayText;
                item.SubItems[4].Text = on ? "● 실행" : current[i].Enabled ? "대기" : "꺼짐";
                if (on) item.SubItems[4].ForeColor = Color.FromArgb(0, 120, 60);
                if (on)
                {
                    GHubSettings.Macro macro;
                    running.Add(macrosById.TryGetValue(current[i].MacroId ?? "", out macro) ? macro.Name : KeyName(current[i].Key));
                }
            }
        }
        if (current.Length == 0) ShowStatus("[추가]를 눌러 트리거 키와 매크로를 연결하세요.", false);
        else if (running.Count > 0) ShowStatus("실행 중: " + string.Join(", ", running), false);
        else ShowStatus("등록한 키를 누르면 매크로가 실행됩니다. 여러 키를 동시에 눌러도 됩니다.", false);
    }

    // 체크박스로 사용 여부를 바꿨을 때
    void OnItemChecked(ListViewItem item)
    {
        // 목록 창이 처음 만들어질 때도 이 이벤트가 오므로, 창이 다 뜬 뒤 사용자가 바꾼 것만 처리한다
        if (refreshingList || !shown) return;
        var current = mappings;
        if (item.Index < 0 || item.Index >= current.Length) return;
        var m = current[item.Index];
        if (m.Enabled == item.Checked) return;
        if (!item.Checked)
            lock (sync) StopMappingLocked(m);   // 끄기 전에 돌고 있던 매크로를 멈춘다
        m.Enabled = item.Checked;
        SaveSettings();
        RefreshStates();
    }

    void UpdateButtons()
    {
        bool any = list.SelectedIndices.Count > 0;
        editButton.Enabled = any;
        deleteButton.Enabled = any;
    }

    static string ModeText(GHubSettings.Mode mode)
    {
        switch (mode)
        {
            case GHubSettings.Mode.Held: return "누르는 동안 반복";
            case GHubSettings.Mode.Toggle: return "토글";
            case GHubSettings.Mode.PressRelease: return "누를 때/뗄 때";
            default: return "한 번 실행";
        }
    }

    public static string KeyName(Keys key)
    {
        switch (key)
        {
            case Keys.None: return "(지정 안 됨)";
            case Keys.LShiftKey: return "왼쪽 Shift";
            case Keys.RShiftKey: return "오른쪽 Shift";
            case Keys.LControlKey: return "왼쪽 Ctrl";
            case Keys.RControlKey: return "오른쪽 Ctrl";
            case Keys.LMenu: return "왼쪽 Alt";
            case Keys.RMenu: return "오른쪽 Alt";
        }
        if (key >= Keys.D0 && key <= Keys.D9) return ((int)(key - Keys.D0)).ToString();
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "Num " + (int)(key - Keys.NumPad0);
        return key.ToString();
    }

    public static bool IsModifier(Keys key)
    {
        return key == Keys.LControlKey || key == Keys.RControlKey
            || key == Keys.LShiftKey || key == Keys.RShiftKey
            || key == Keys.LMenu || key == Keys.RMenu;
    }

    // ───────────────────────── 목록 편집 ─────────────────────────

    Mapping SelectedMapping
    {
        get
        {
            var current = mappings;
            int i = list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1;
            return i >= 0 && i < current.Length ? current[i] : null;
        }
    }

    void AddMapping()
    {
        EditMapping(null);
    }

    void EditMapping()
    {
        var m = SelectedMapping;
        if (m != null) EditMapping(m);
    }

    // existing이 null이면 새로 추가한다
    void EditMapping(Mapping existing)
    {
        if (apps.Count == 0)
        {
            MessageBox.Show("G Hub 매크로가 없습니다. G Hub에서 매크로를 만든 뒤 [새로고침]을 누르세요.", "G Hub Keyboard");
            return;
        }

        StopAll();
        paused = true;
        try
        {
            using (var dialog = new MappingDialog(this, apps, existing, key => mappings.Any(x => x != existing && x.Key == key)))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var updated = new Mapping { Key = dialog.Key, AppId = dialog.AppId, MacroId = dialog.MacroId, Enabled = existing == null || existing.Enabled };
                var next = mappings.ToList();
                int index = existing == null ? -1 : next.IndexOf(existing);
                if (index >= 0) next[index] = updated;
                else next.Add(updated);
                mappings = next.ToArray();
                SaveSettings();
                RefreshList();
                list.SelectedIndices.Clear();
                list.Items[index >= 0 ? index : list.Items.Count - 1].Selected = true;
            }
        }
        finally
        {
            CancelCapture();
            paused = false;
        }
    }

    void DeleteMapping()
    {
        var m = SelectedMapping;
        if (m == null) return;
        StopAll();
        mappings = mappings.Where(x => x != m).ToArray();
        SaveSettings();
        RefreshList();
    }

    // ───────────────────────── 매크로 실행 ─────────────────────────
    // 키 감지 스레드와 화면 스레드 양쪽에서 호출된다.

    Mapping FindMapping(Keys key)
    {
        foreach (var m in mappings)
            if (m.Key == key && m.Enabled) return m;
        return null;
    }

    // 이 키를 후킹으로 처리할지 (입력을 막는 경우). 아니면 장치를 구분할 수 있는 Raw Input으로 처리한다.
    bool UsesHook(Keys key)
    {
        return suppress && !IsModifier(key);
    }

    void TriggerDown(Mapping m, IntPtr device)
    {
        GHubSettings.Macro macro;
        if (!macrosById.TryGetValue(m.MacroId ?? "", out macro)) return;
        bool sent;
        lock (sync)
        {
            if (m.Held) return;   // 키를 누르고 있으면 반복 입력이 들어오므로 무시
            m.Held = true;
            m.HeldDevice = device;
            sent = agent.PlayMacro(macro.Id, m.TriggerId, true);
            if (sent)
            {
                m.Playing = true;
                if (macro.Mode == GHubSettings.Mode.Toggle) m.ToggledOn = !m.ToggledOn;
            }
        }
        if (sent) Ui(RefreshStates);
        else Ui(() => ShowStatus("G Hub에 연결되지 않아 실행할 수 없습니다.", true));
    }

    void TriggerUp(Mapping m, IntPtr device)
    {
        lock (sync)
        {
            if (!m.Held || device != m.HeldDevice) return;
            m.Held = false;
            if (!m.Playing) return;
            agent.PlayMacro(m.MacroId, m.TriggerId, false);
            m.Playing = false;
        }
        Ui(RefreshStates);
    }

    // 실행 중인 매크로를 모두 멈춘다. 켜져 있는 토글형 매크로는 한 번 더 눌러서 끈다.
    void StopAll()
    {
        lock (sync)
        {
            foreach (var m in mappings) StopMappingLocked(m);
        }
        if (IsHandleCreated) Ui(RefreshStates);
    }

    // sync를 잡은 상태에서 호출한다
    void StopMappingLocked(Mapping m)
    {
        if (m.Held)
        {
            m.Held = false;
            if (m.Playing) agent.PlayMacro(m.MacroId, m.TriggerId, false);
            m.Playing = false;
        }
        if (m.ToggledOn)
        {
            agent.PlayMacro(m.MacroId, m.TriggerId, true);
            agent.PlayMacro(m.MacroId, m.TriggerId, false);
            m.ToggledOn = false;
        }
    }

    void ResetPlayback()
    {
        lock (sync)
        {
            foreach (var m in mappings)
            {
                m.Held = false;
                m.Playing = false;
                m.ToggledOn = false;
            }
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
            // 다른 프로그램이 보낸 가상 입력은 무시. 키 변경·목록 편집 중에는 아무 키도 막지 않는다.
            if (!injected && !capturing && !paused)
            {
                int msg = wParam.ToInt32();
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                var key = (Keys)info.vkCode;

                if (UsesHook(key))
                {
                    var m = FindMapping(key);
                    if (m != null)
                    {
                        if (down) TriggerDown(m, IntPtr.Zero);
                        else if (up) TriggerUp(m, IntPtr.Zero);
                        return (IntPtr)1;
                    }
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

    // 입력을 막지 않는 키와 키 변경은 Raw Input으로 받는다.
    // (이 프로그램의 창이 맨 앞에 있으면 Raw Input을 등록한 탓에 키보드 후킹이 호출되지 않는다)
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
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
            if (device == IntPtr.Zero || IsGHubDevice(device)) return;
            var key = NormalizeKey(vkey, makeCode, flags);
            bool up = (flags & RI_KEY_BREAK) != 0;

            if (capturing)
            {
                if (!up) FinishCapture(key);
                return;
            }
            if (paused || UsesHook(key)) return;

            var mapping = FindMapping(key);
            if (mapping == null) return;
            if (!up) TriggerDown(mapping, device);
            else TriggerUp(mapping, device);
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

    // 다음에 누르는 키를 callback으로 넘긴다. Esc를 누르면 Keys.Escape가 넘어간다.
    public void BeginCapture(Action<Keys> callback)
    {
        captureCallback = callback;
        capturing = true;
    }

    public void CancelCapture()
    {
        capturing = false;
        captureCallback = null;
    }

    void FinishCapture(Keys key)
    {
        if (!capturing) return;
        var callback = captureCallback;
        CancelCapture();
        if (callback != null) callback(key);
    }

    void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    // ───────────────────────── 설정 저장 ─────────────────────────

    // 설정은 레지스트리(HKCU\Software\GHubKeyboard)에 저장한다.
    // 관리자 권한으로 사용자 폴더에 파일을 쓰면 링크/정션을 이용한 임의 파일 쓰기에 악용될 수 있기 때문이다.
    // 매핑은 "키|게임 프로필 ID|매크로 ID|사용 여부(1/0)" 형식의 여러 줄 문자열로 저장한다.
    void LoadSettings()
    {
        var legacy = new Dictionary<string, string>();
        var loaded = new List<Mapping>();
        try
        {
            using (var reg = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                if (reg != null)
                {
                    foreach (var name in reg.GetValueNames())
                    {
                        if (name == "mappings")
                        {
                            var lines = reg.GetValue(name) as string[];
                            if (lines != null)
                                foreach (var line in lines)
                                {
                                    var m = ParseMapping(line);
                                    if (m != null && !loaded.Any(x => x.Key == m.Key)) loaded.Add(m);
                                }
                        }
                        else
                        {
                            var value = reg.GetValue(name) as string;
                            if (value != null) legacy[name] = value;
                        }
                    }
                }
                else if (File.Exists(legacySettingsPath))
                {
                    // v1.0.0 이전의 설정 파일이 있으면 한 번 읽어 온다 (읽기만 한다)
                    foreach (var line in File.ReadAllLines(legacySettingsPath, Encoding.UTF8))
                    {
                        var parts = line.Split(new[] { '=' }, 2);
                        if (parts.Length == 2) legacy[parts[0]] = parts[1];
                    }
                }
            }
        }
        catch { }

        string value2;
        if (legacy.TryGetValue("suppress", out value2)) suppress = value2 == "1";
        if (legacy.TryGetValue("updateCheck", out value2)) checkUpdates = value2 == "1";

        // v1.0.0의 키 하나짜리 설정은 목록의 첫 줄로 옮긴다
        Keys oldKey;
        string oldMacro, oldApp;
        if (loaded.Count == 0 && legacy.TryGetValue("macroId", out oldMacro) && !string.IsNullOrEmpty(oldMacro)
            && legacy.TryGetValue("key", out value2) && Enum.TryParse(value2, out oldKey))
        {
            legacy.TryGetValue("app", out oldApp);
            loaded.Add(new Mapping { Key = oldKey, AppId = oldApp, MacroId = oldMacro });
        }
        mappings = loaded.ToArray();
    }

    static Mapping ParseMapping(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var parts = line.Split('|');
        Keys key;
        if (parts.Length < 3 || !Enum.TryParse(parts[0], out key) || key == Keys.None || string.IsNullOrEmpty(parts[2])) return null;
        return new Mapping { Key = key, AppId = parts[1], MacroId = parts[2], Enabled = parts.Length < 4 || parts[3] != "0" };
    }

    void SaveSettings()
    {
        try
        {
            using (var reg = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                reg.SetValue("mappings", mappings.Select(m => m.Key + "|" + m.AppId + "|" + m.MacroId + "|" + (m.Enabled ? "1" : "0")).ToArray(), RegistryValueKind.MultiString);
                reg.SetValue("suppress", suppress ? "1" : "0");
                reg.SetValue("updateCheck", checkUpdates ? "1" : "0");
                // v1.0.0의 키 하나짜리 설정은 목록으로 옮겼으므로 지운다
                foreach (var old in new[] { "key", "app", "macroId" })
                    reg.DeleteValue(old, false);
            }
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 매크로가 켜진 채로 남지 않게 정리하고, 전송될 시간을 잠깐 준다
        bool pending;
        lock (sync) pending = mappings.Any(m => m.Playing || m.ToggledOn);
        StopAll();
        if (pending) Thread.Sleep(300);
        agent.Stop();
        if (hookThreadId != 0) PostThreadMessage(hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        base.OnFormClosing(e);
    }
}

// 트리거 키와 매크로를 고르는 창
class MappingDialog : Form
{
    readonly MainForm owner;
    readonly Func<Keys, bool> keyInUse;
    ComboBox appBox, macroBox;
    Label modeLabel, keyLabel;

    public Keys Key { get; private set; }
    public string AppId { get; private set; }
    public string MacroId { get; private set; }

    public MappingDialog(MainForm owner, List<GHubSettings.App> apps, Mapping existing, Func<Keys, bool> keyInUse)
    {
        this.owner = owner;
        this.keyInUse = keyInUse;
        Key = existing != null ? existing.Key : Keys.None;

        Text = existing == null ? "매크로 추가" : "매크로 수정";
        Font = owner.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(400, 230);

        Controls.Add(new Label { Text = "게임 프로필", Location = new Point(20, 22), AutoSize = true });
        appBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 18), Size = new Size(270, 28) };
        foreach (var app in apps) appBox.Items.Add(app);
        appBox.SelectedIndexChanged += (s, e) => FillMacros(null);
        Controls.Add(appBox);

        Controls.Add(new Label { Text = "매크로", Location = new Point(20, 58), AutoSize = true });
        macroBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 54), Size = new Size(270, 28) };
        macroBox.SelectedIndexChanged += (s, e) => UpdateMode();
        Controls.Add(macroBox);

        modeLabel = new Label { Location = new Point(110, 86), AutoSize = true, ForeColor = SystemColors.GrayText };
        Controls.Add(modeLabel);

        keyLabel = new Label { Location = new Point(20, 124), AutoSize = true, Font = new Font(owner.Font.FontFamily, 12f, FontStyle.Bold) };
        Controls.Add(keyLabel);
        var keyButton = new Button { Text = "키 지정", Location = new Point(270, 119), Size = new Size(110, 32) };
        keyButton.Click += (s, e) => StartCapture();
        Controls.Add(keyButton);

        var okButton = new Button { Text = "확인", Location = new Point(214, 180), Size = new Size(80, 32) };
        okButton.Click += (s, e) => Confirm();
        Controls.Add(okButton);
        var cancelButton = new Button { Text = "취소", Location = new Point(300, 180), Size = new Size(80, 32), DialogResult = DialogResult.Cancel };
        Controls.Add(cancelButton);
        CancelButton = cancelButton;

        // 기존 매핑의 게임 프로필/매크로를 선택해 둔다
        GHubSettings.App selectedApp = null;
        if (existing != null)
            selectedApp = apps.FirstOrDefault(a => a.Macros.Any(m => m.Id == existing.MacroId))
                       ?? apps.FirstOrDefault(a => a.Id == existing.AppId);
        appBox.SelectedItem = selectedApp ?? apps.FirstOrDefault();
        FillMacros(existing != null ? existing.MacroId : null);
        UpdateKeyLabel();
    }

    void FillMacros(string selectId)
    {
        var app = appBox.SelectedItem as GHubSettings.App;
        macroBox.Items.Clear();
        if (app == null) return;
        foreach (var m in app.Macros) macroBox.Items.Add(m);
        var macro = app.Macros.FirstOrDefault(m => m.Id == selectId) ?? app.Macros.FirstOrDefault();
        if (macro != null) macroBox.SelectedItem = macro;
        UpdateMode();
    }

    void UpdateMode()
    {
        var macro = macroBox.SelectedItem as GHubSettings.Macro;
        if (macro == null) { modeLabel.Text = ""; return; }
        switch (macro.Mode)
        {
            case GHubSettings.Mode.Held: modeLabel.Text = "종류: 누르고 있는 동안 반복"; break;
            case GHubSettings.Mode.Toggle: modeLabel.Text = "종류: 토글 (한 번 누르면 켜지고, 다시 누르면 꺼짐)"; break;
            case GHubSettings.Mode.PressRelease: modeLabel.Text = "종류: 누를 때 / 뗄 때 실행"; break;
            default: modeLabel.Text = "종류: 한 번 실행"; break;
        }
    }

    void UpdateKeyLabel()
    {
        keyLabel.Text = "트리거 키: " + MainForm.KeyName(Key);
    }

    void StartCapture()
    {
        ActiveControl = null;   // 누른 키가 버튼이나 목록에 전달되지 않게 포커스를 뺀다
        keyLabel.Text = "새 키를 누르세요 (Esc: 취소)";
        owner.BeginCapture(key =>
        {
            if (key != Keys.Escape)
            {
                if (keyInUse(key))
                    MessageBox.Show(this, MainForm.KeyName(key) + " 키는 이미 다른 매크로에 쓰고 있습니다.", "G Hub Keyboard");
                else
                    Key = key;
            }
            UpdateKeyLabel();
        });
    }

    void Confirm()
    {
        var app = appBox.SelectedItem as GHubSettings.App;
        var macro = macroBox.SelectedItem as GHubSettings.Macro;
        if (macro == null)
        {
            MessageBox.Show(this, "매크로를 고르세요.", "G Hub Keyboard");
            return;
        }
        if (Key == Keys.None)
        {
            MessageBox.Show(this, "[키 지정]을 눌러 트리거 키를 정하세요.", "G Hub Keyboard");
            return;
        }
        if (keyInUse(Key))
        {
            MessageBox.Show(this, MainForm.KeyName(Key) + " 키는 이미 다른 매크로에 쓰고 있습니다.", "G Hub Keyboard");
            return;
        }
        AppId = app != null ? app.Id : "";
        MacroId = macro.Id;
        DialogResult = DialogResult.OK;
    }

    // 키를 기다리는 동안에는 이 창의 버튼/목록이 키 입력에 반응하지 않게 한다
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (owner.Capturing) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        owner.CancelCapture();
        base.OnFormClosed(e);
    }
}
