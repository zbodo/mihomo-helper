using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace MihomoTray;

internal sealed class ManagedTaskInfo
{
    public string TaskName { get; init; } = "";
    public string TaskPath { get; init; } = "";
    public string KernelPath { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string Description { get; init; } = "";

    public string FullName
    {
        get
        {
            string folder = string.IsNullOrEmpty(TaskPath) || TaskPath == "\\" ? "\\" : TaskPath.TrimEnd('\\') + "\\";
            return folder + TaskName;
        }
    }
}

internal static class MihomoService
{
    public const string TaskUuid = "6B8E2C14-A91F-4D53-B7E0-3C1A9F8D2465";

    private const int DefaultControllerPort = 9090;
    private const int DefaultMixedPort = 7890;
    private const string DefaultTaskName = "mihomo";
    private const string KernelArgs = "-d .\\ -f config.yaml";
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string SettingsPath = @"Software\MihomoTray";
    private const string ProxyOverride = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private static string? _activeKernelPath;

    public static void Run(string action, string? kernelPath = null, bool skipConfirm = false)
    {
        if (!string.IsNullOrWhiteSpace(kernelPath))
        {
            SaveKernelPath(kernelPath);
        }

        switch (action)
        {
            case "Proxy":
                StartKernel();
                SetProxy(true);
                SetTun(false).GetAwaiter().GetResult();
                break;
            case "Tun":
                StartKernel();
                SetProxy(false);
                SetTun(true).GetAwaiter().GetResult();
                break;
            case "Off":
                SetProxy(false);
                break;
            case "Toggle":
                Run(IsProxyEnabled() ? "Tun" : "Proxy");
                break;
            case "ToggleProxy":
                ToggleSystemProxy();
                break;
            case "ToggleTun":
                ToggleTunMode();
                break;
            case "StartKernel":
                StartKernel();
                break;
            case "Stop":
                StopKernel();
                break;
            case "OpenWebUi":
                if (!TryOpenDashboardFromConfig())
                {
                    throw new InvalidOperationException("无法打开 WEBUI");
                }

                break;
            case "InstallTask":
                EnsureAdmin("InstallTask", GetKernelPath());
                InstallTask(GetKernelPath());
                StartManagedTask();
                break;
            case "RemoveTask":
                RemoveTask(skipConfirm);
                break;
            case "RemoveTaskConfirm":
                RemoveTask(true);
                break;
            case "StartTask":
                StartManagedTask();
                break;
            case "RunKernel":
                StartKernelHidden(string.IsNullOrWhiteSpace(kernelPath) ? GetKernelPath() : kernelPath);
                break;
            case "Status":
                break;
            default:
                throw new InvalidOperationException("未知操作: " + action);
        }
    }

    public static string GetStatusText()
    {
        var status = GetStatus();
        return $"系统代理: {status.Proxy}{Environment.NewLine}TUN: {status.Tun}{Environment.NewLine}内核: {status.Kernel}";
    }

    public static (string Proxy, string Tun, string Kernel) GetStatus()
    {
        string proxy = IsProxyEnabled() ? "启用" : "关闭";
        string tun = QueryTun();
        string kernel = GetCurrentTaskKernelPath()
            ?? (TryGetSavedKernelPath(out string saved) ? saved : "未设置");
        return (proxy, tun, kernel);
    }

    public static (bool ProxyEnabled, bool TunEnabled) QueryModeEnabled()
    {
        return (IsProxyEnabled(), IsTunEnabled());
    }

    public static string GetSavedKernelPathOrEmpty()
    {
        return TryGetSavedKernelPath(out string path) ? path : "";
    }

    public static string? GetCurrentTaskKernelPath()
    {
        if (TryFindManagedTask(out ManagedTaskInfo? task) &&
            task is not null &&
            !string.IsNullOrWhiteSpace(task.KernelPath))
        {
            return task.KernelPath;
        }

        return null;
    }

    public static void SetActiveKernelPath(string? path)
    {
        _activeKernelPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    public static bool TryPrepareRuntimeConfig()
    {
        return TryLoadRuntimeConfig(out _);
    }

    public static bool TryEnableSystemProxyFromConfig()
    {
        try
        {
            if (!TryLoadRuntimeConfig(out _))
            {
                return false;
            }

            EnableSystemProxyExclusive();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryValidateWebUiConfig()
    {
        try
        {
            return TryLoadRuntimeConfig(out KernelRuntimeConfig cfg) && IsWebUiReady(cfg);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryOpenDashboardFromConfig()
    {
        try
        {
            if (!TryLoadRuntimeConfig(out KernelRuntimeConfig cfg) || !IsWebUiReady(cfg))
            {
                return false;
            }

            string url = "http://127.0.0.1:" + cfg.ControllerPort +
                "/ui/#/setup?hostname=127.0.0.1&port=" + cfg.ControllerPort +
                "&secret=" + Uri.EscapeDataString(cfg.Secret);
            NativeMethods.ShellExecute(IntPtr.Zero, "open", url, null, null, NativeMethods.SwShowNoActivate);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWebUiReady(KernelRuntimeConfig cfg)
    {
        return IsControllerAuthorized(cfg) && IsWebUiPageReachable(cfg);
    }

    private static bool IsControllerAuthorized(KernelRuntimeConfig cfg)
    {
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(1));
            using HttpRequestMessage request = ControllerRequest(HttpMethod.Get, "/configs");
            using HttpResponseMessage response = Http.Send(request, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWebUiPageReachable(KernelRuntimeConfig cfg)
    {
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(1));
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                "http://127.0.0.1:" + cfg.ControllerPort + "/ui/");
            using HttpResponseMessage response = Http.Send(request, cts.Token);
            int code = (int)response.StatusCode;
            return code is >= 200 and < 400;
        }
        catch
        {
            return false;
        }
    }

    public static void RevealInExplorer(string path)
    {
        path = path.Trim().Trim('"');
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("当前没有可打开的路径");
        }

        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + path + "\"",
                UseShellExecute = true
            });
            return;
        }

        string? directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new InvalidOperationException("路径不存在: " + path);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "\"" + directory + "\"",
            UseShellExecute = true
        });
    }

    public static void SaveKernelPath(string path)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsPath);
        key.SetValue("KernelPath", path.Trim(), RegistryValueKind.String);
    }

    public static bool TryFindManagedTask(out ManagedTaskInfo? info)
    {
        return TryFindTask(task => HasTaskUuid(task), out info);
    }

    public static bool TryFindTaskToRemove(string? kernelPath, out ManagedTaskInfo? info)
    {
        if (TryFindManagedTask(out info) && info is not null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(kernelPath))
        {
            kernelPath = ResolveKernelPathHint();
        }

        if (string.IsNullOrWhiteSpace(kernelPath))
        {
            info = null;
            return false;
        }

        return TryFindTask(task => PathsEqual(task.KernelPath, kernelPath), out info);
    }

    private static bool TryFindTask(Func<ManagedTaskInfo, bool> match, out ManagedTaskInfo? info)
    {
        info = null;
        try
        {
            Type? type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                return false;
            }

            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            info = FindInFolder(service.GetFolder("\\"), match);
            return info is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveKernelPathHint()
    {
        if (!string.IsNullOrWhiteSpace(_activeKernelPath))
        {
            return _activeKernelPath;
        }

        if (TryGetSavedKernelPath(out string saved) && saved.Length > 0)
        {
            return saved;
        }

        return GetCurrentTaskKernelPath();
    }

    private static bool TryFindTaskByKernelPath(string? kernelPath, out ManagedTaskInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(kernelPath))
        {
            return false;
        }

        if (TryFindManagedTask(out ManagedTaskInfo? uuidTask) &&
            uuidTask is not null &&
            PathsEqual(uuidTask.KernelPath, kernelPath))
        {
            info = uuidTask;
            return true;
        }

        return TryFindTask(task => PathsEqual(task.KernelPath, kernelPath), out info);
    }

    private static bool IsAdmin()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void EnsureAdmin(string action, string? extra = null)
    {
        if (IsAdmin())
        {
            return;
        }

        string arguments = string.IsNullOrEmpty(extra) ? action : $"{action} \"{extra}\"";
        ProcessStartInfo psi = new()
        {
            FileName = Environment.ProcessPath,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            using Process? process = Process.Start(psi);
            process?.WaitForExit();
        }
        catch
        {
            throw new InvalidOperationException("已取消管理员授权，无法修改计划任务");
        }

        throw new OperationCanceledException();
    }

    private static void ToggleSystemProxy()
    {
        if (IsProxyEnabled())
        {
            SetProxy(false);
            return;
        }

        EnableSystemProxyExclusive();
    }

    private static void ToggleTunMode()
    {
        if (IsTunEnabled())
        {
            SetTun(false).GetAwaiter().GetResult();
            return;
        }

        EnableTunExclusive();
    }

    private static void EnableSystemProxyExclusive()
    {
        SetProxy(true);
        if (!IsProxyEnabled())
        {
            throw new InvalidOperationException("开启系统代理失败");
        }

        try
        {
            if (IsTunEnabled())
            {
                SetTun(false).GetAwaiter().GetResult();
            }
        }
        catch
        {
        }
    }

    private static void EnableTunExclusive()
    {
        StartKernel();
        try
        {
            SetTun(true).GetAwaiter().GetResult();
        }
        catch
        {
        }

        if (!IsTunEnabled())
        {
            RestartKernelAsSystemOrElevated();
            SetTun(true).GetAwaiter().GetResult();
        }

        if (!IsTunEnabled())
        {
            throw new InvalidOperationException("无法打开 TUN。请先添加 SYSTEM 计划任务，或允许管理员权限");
        }

        try
        {
            if (IsProxyEnabled())
            {
                SetProxy(false);
            }
        }
        catch
        {
        }
    }

    private static bool IsTunEnabled()
    {
        return string.Equals(QueryTun(), "启用", StringComparison.Ordinal);
    }

    private static void SetProxy(bool enable)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
        if (key is null)
        {
            throw new InvalidOperationException("无法打开代理注册表项");
        }

        key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", enable ? $"127.0.0.1:{LoadRuntimeConfig().MixedPort}" : string.Empty, RegistryValueKind.String);
        if (enable)
        {
            key.SetValue("ProxyOverride", ProxyOverride, RegistryValueKind.String);
        }

        NativeMethods.InternetSetOption(IntPtr.Zero, NativeMethods.InternetOptionSettingsChanged, IntPtr.Zero, 0);
        NativeMethods.InternetSetOption(IntPtr.Zero, NativeMethods.InternetOptionRefresh, IntPtr.Zero, 0);
    }

    private static bool IsProxyEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
        object? value = key?.GetValue("ProxyEnable", 0);
        return Convert.ToInt32(value) != 0;
    }

    private static async Task SetTun(bool enable)
    {
        string payload = enable
            ? "{\"tun\":{\"enable\":true}}"
            : "{\"tun\":{\"enable\":false}}";
        using StringContent content = new(payload, Encoding.UTF8, "application/json");
        using HttpRequestMessage request = ControllerRequest(HttpMethod.Patch, "/configs", content);
        using HttpResponseMessage response = await Http.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = (await response.Content.ReadAsStringAsync()).Trim();
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(body)
                ? "设置 TUN 失败: HTTP " + (int)response.StatusCode
                : "设置 TUN 失败: " + body);
    }

    private static string QueryTun()
    {
        try
        {
            using HttpRequestMessage request = ControllerRequest(HttpMethod.Get, "/configs");
            using HttpResponseMessage response = Http.Send(request);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return "认证失败";
            }

            response.EnsureSuccessStatusCode();
            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return TryReadTunEnabled(json, out bool enabled)
                ? (enabled ? "启用" : "关闭")
                : "未知";
        }
        catch
        {
            return "无法连接内核";
        }
    }

    private static bool TryReadTunEnabled(string json, out bool enabled)
    {
        enabled = false;
        int tunAt = json.IndexOf("\"tun\"", StringComparison.OrdinalIgnoreCase);
        if (tunAt < 0)
        {
            return false;
        }

        int objStart = json.IndexOf('{', tunAt);
        if (objStart < 0)
        {
            return false;
        }

        int enableAt = -1;
        int depth = 0;
        for (int i = objStart; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                if (depth <= 0)
                {
                    break;
                }

                continue;
            }

            if (depth == 1 &&
                enableAt < 0 &&
                i + 8 <= json.Length &&
                json.AsSpan(i, 8).Equals("\"enable\"".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                enableAt = i;
                break;
            }
        }

        if (enableAt < 0)
        {
            return false;
        }

        int colon = json.IndexOf(':', enableAt);
        if (colon < 0)
        {
            return false;
        }

        string value = json.Substring(colon + 1).TrimStart();
        if (value.StartsWith("true", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
            return true;
        }

        if (value.StartsWith("false", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
            return true;
        }

        return false;
    }

    private static bool TryGetSavedKernelPath(out string path)
    {
        path = "";
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsPath, false);
        object? value = key?.GetValue("KernelPath");
        path = Convert.ToString(value)?.Trim() ?? "";
        return path.Length > 0;
    }

    private static string GetKernelPath()
    {
        if (TryGetSavedKernelPath(out string saved) && saved.Length > 0)
        {
            if (!File.Exists(saved))
            {
                throw new InvalidOperationException("找不到内核: " + saved);
            }

            return saved;
        }

        string? current = GetCurrentTaskKernelPath();
        if (!string.IsNullOrEmpty(current) && File.Exists(current))
        {
            return current;
        }

        throw new InvalidOperationException("请先填写 mihomo 内核路径");
    }

    private static string GetProcessName(string kernelExe)
    {
        return Path.GetFileNameWithoutExtension(kernelExe);
    }

    private static bool IsKernelReachable()
    {
        try
        {
            using HttpRequestMessage request = ControllerRequest(HttpMethod.Get, "/configs");
            using HttpResponseMessage response = Http.Send(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void WaitForKernel()
    {
        DateTime deadline = DateTime.Now.AddSeconds(8);
        while (DateTime.Now < deadline)
        {
            if (IsKernelReachable())
            {
                return;
            }

            Thread.Sleep(400);
        }

        throw new InvalidOperationException("启动内核失败");
    }

    private static void StartKernel()
    {
        if (IsKernelReachable())
        {
            return;
        }

        if (TryFindManagedTask(out ManagedTaskInfo? managed) && managed is not null)
        {
            StartTask(managed.FullName);
            WaitForKernel();
            return;
        }

        try
        {
            StartTask("\\" + DefaultTaskName);
            WaitForKernel();
            return;
        }
        catch
        {
        }

        if (TryGetSavedKernelPath(out string kernelExe) && File.Exists(kernelExe))
        {
            string processName = GetProcessName(kernelExe);
            if (Process.GetProcessesByName(processName).Length == 0)
            {
                StartKernelHidden(kernelExe);
            }

            WaitForKernel();
            return;
        }

        throw new InvalidOperationException("启动内核失败：未找到计划任务 mihomo");
    }

    private static void StartKernelHidden(string kernelExe)
    {
        kernelExe = kernelExe.Trim().Trim('"');
        if (string.IsNullOrEmpty(kernelExe) || !File.Exists(kernelExe))
        {
            throw new InvalidOperationException("找不到内核: " + kernelExe);
        }

        if (IsKernelReachable())
        {
            return;
        }

        string processName = GetProcessName(kernelExe);
        if (Process.GetProcessesByName(processName).Length > 0)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = kernelExe,
            Arguments = KernelArgs,
            WorkingDirectory = Path.GetDirectoryName(kernelExe) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static void RestartKernelAsSystemOrElevated()
    {
        StopKernel();
        if (TryFindManagedTask(out ManagedTaskInfo? managed) && managed is not null)
        {
            StartTask(managed.FullName);
            WaitForKernel();
            return;
        }

        try
        {
            StartTask("\\" + DefaultTaskName);
            WaitForKernel();
            return;
        }
        catch
        {
        }

        StartKernelElevated();
    }

    private static void StartKernelElevated()
    {
        string? kernelExe = ResolveKernelPathHint();
        if (string.IsNullOrWhiteSpace(kernelExe) || !File.Exists(kernelExe))
        {
            kernelExe = GetKernelPath();
        }

        string? host = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(host) || !File.Exists(host))
        {
            throw new InvalidOperationException("找不到当前程序，无法提权启动内核");
        }

        ProcessStartInfo psi = new()
        {
            FileName = host,
            Arguments = "RunKernel \"" + kernelExe + "\"",
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            using Process? process = Process.Start(psi);
            process?.WaitForExit();
        }
        catch
        {
            throw new InvalidOperationException("已取消管理员授权，无法打开 TUN");
        }

        WaitForKernel();
    }

    private static bool TryParseRunKernelPath(string actionPath, string arguments, out string kernel)
    {
        kernel = "";
        string? self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self) || !PathsEqual(actionPath, self))
        {
            return false;
        }

        List<string> args = SplitArgs(arguments);
        if (args.Count < 2 || !args[0].Equals("RunKernel", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        kernel = args[1].Trim().Trim('"');
        return kernel.Length > 0;
    }

    private static void StopKernel()
    {
        if (TryFindManagedTask(out ManagedTaskInfo? managed) && managed is not null)
        {
            try { RunHidden("schtasks.exe", $"/End /TN \"{managed.FullName}\""); } catch { }
            if (!string.IsNullOrEmpty(managed.KernelPath))
            {
                KillProcess(GetProcessName(managed.KernelPath));
            }
        }

        try { RunHidden("schtasks.exe", $"/End /TN \"\\{DefaultTaskName}\""); } catch { }

        if (TryGetSavedKernelPath(out string saved))
        {
            KillProcess(GetProcessName(saved));
        }

        KillProcess("mihomo-windows-amd64-v3");
        KillProcess("mihomo-windows-amd64");
    }

    private static void KillProcess(string processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return;
        }

        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try { process.Kill(); } catch { }
        }
    }

    private static void StartManagedTask()
    {
        if (TryFindManagedTask(out ManagedTaskInfo? managed) && managed is not null)
        {
            StartTask(managed.FullName);
            return;
        }

        StartTask("\\" + DefaultTaskName);
    }

    private static void StartTask(string taskName)
    {
        RunHidden("schtasks.exe", $"/Run /TN \"{taskName}\"");
    }

    private static void InstallTask(string kernelExe)
    {
        if (TryFindManagedTask(out ManagedTaskInfo? existing) && existing is not null)
        {
            throw new InvalidOperationException(
                "已存在带本应用 UUID 的计划任务，未重复创建。" + Environment.NewLine +
                "任务: " + existing.FullName + Environment.NewLine +
                "内核: " + existing.KernelPath);
        }

        EnsureExampleConfig(kernelExe);

        Type type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("无法访问任务计划程序");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = "Mihomo Helper " + TaskUuid;

        definition.Triggers.Create(8);
        dynamic action = definition.Actions.Create(0);
        action.Path = kernelExe;
        action.Arguments = KernelArgs;
        action.WorkingDirectory = Path.GetDirectoryName(kernelExe) ?? "";

        definition.Principal.UserId = "S-1-5-18";
        definition.Principal.LogonType = 5;
        definition.Principal.RunLevel = 1;
        definition.Settings.Hidden = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.Enabled = true;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.MultipleInstances = 2;
        definition.Settings.RestartInterval = "PT1M";
        definition.Settings.RestartCount = 3;

        try
        {
            folder.RegisterTaskDefinition(DefaultTaskName, definition, 2, null, null, 5);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "无法创建 SYSTEM 计划任务。若已存在同名任务 " + DefaultTaskName + "，请先删除后再添加。" +
                Environment.NewLine + ex.Message);
        }
    }

    private static void EnsureExampleConfig(string kernelExe)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(kernelExe) ||
                !kernelExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string? kernelDir = Path.GetDirectoryName(kernelExe);
            if (string.IsNullOrWhiteSpace(kernelDir))
            {
                return;
            }

            string kernelName = Path.GetFileName(kernelExe);
            string kernelFullPath = Path.GetFullPath(kernelExe);
            var directory = new DirectoryInfo(kernelDir);
            FileInfo[] files = directory.GetFiles("*", SearchOption.TopDirectoryOnly);

            bool hasKernel = false;
            bool hasConfig = false;
            foreach (FileInfo file in files)
            {
                if (file.Name.Equals(kernelName, StringComparison.OrdinalIgnoreCase) &&
                    PathsEqual(file.FullName, kernelFullPath))
                {
                    hasKernel = true;
                }

                if (file.Name.Equals("config.yaml", StringComparison.OrdinalIgnoreCase) ||
                    file.Name.Equals("config.yml", StringComparison.OrdinalIgnoreCase))
                {
                    hasConfig = true;
                }
            }

            if (!hasKernel || hasConfig)
            {
                return;
            }

            string example = Path.Combine(AppContext.BaseDirectory, "Assets", "config.example.yaml");
            FileInfo exampleFile = new(example);
            if (!exampleFile.Exists)
            {
                return;
            }

            exampleFile.CopyTo(Path.Combine(directory.FullName, "config.yaml"), false);
        }
        catch
        {
        }
    }

    private static void RemoveTask(bool skipConfirm)
    {
        if (!TryFindTaskToRemove(ResolveKernelPathHint(), out ManagedTaskInfo? existing) || existing is null)
        {
            throw new InvalidOperationException("未找到可删除的计划任务（UUID 或内核路径均无匹配）");
        }

        if (!skipConfirm)
        {
            int answer = NativeMethods.MessageBox(
                IntPtr.Zero,
                "即将删除计划任务" + Environment.NewLine +
                "任务: " + existing.FullName + Environment.NewLine +
                "内核: " + existing.KernelPath + Environment.NewLine + Environment.NewLine +
                "确认删除？",
                "Mihomo Helper",
                NativeMethods.MessageBoxYesNo | NativeMethods.MessageBoxWarning);
            if (answer != NativeMethods.IdYes)
            {
                throw new OperationCanceledException();
            }
        }

        try { RunHidden("schtasks.exe", $"/End /TN \"{existing.FullName}\""); } catch { }
        if (!string.IsNullOrEmpty(existing.KernelPath))
        {
            KillProcess(GetProcessName(existing.KernelPath));
        }

        try
        {
            RunHidden("schtasks.exe", $"/Delete /TN \"{existing.FullName}\" /F");
        }
        catch (Exception ex)
        {
            if (!IsAdmin())
            {
                EnsureAdmin(skipConfirm ? "RemoveTaskConfirm" : "RemoveTask", ResolveKernelPathHint());
            }

            throw new InvalidOperationException("删除计划任务失败: " + ex.Message);
        }
    }

    private static bool HasTaskUuid(ManagedTaskInfo task)
    {
        return task.Description.IndexOf(TaskUuid, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left.Trim().Trim('"')),
                Path.GetFullPath(right.Trim().Trim('"')),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class KernelRuntimeConfig
    {
        public string Secret { get; init; } = "";
        public int ControllerPort { get; init; } = DefaultControllerPort;
        public int MixedPort { get; init; } = DefaultMixedPort;

        public string ControllerApi => "http://127.0.0.1:" + ControllerPort;
    }

    private static HttpRequestMessage ControllerRequest(HttpMethod method, string path, HttpContent? content = null)
    {
        KernelRuntimeConfig cfg = LoadRuntimeConfig();
        HttpRequestMessage request = new(method, cfg.ControllerApi + path);
        if (cfg.Secret.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Secret);
        }

        request.Content = content;
        return request;
    }

    private static KernelRuntimeConfig LoadRuntimeConfig()
    {
        return TryLoadRuntimeConfig(out KernelRuntimeConfig cfg) ? cfg : new KernelRuntimeConfig();
    }

    private static bool TryLoadRuntimeConfig(out KernelRuntimeConfig cfg)
    {
        cfg = new KernelRuntimeConfig();
        try
        {
            if (!TryResolveConfigFile(out string configPath))
            {
                return false;
            }

            string text = File.ReadAllText(configPath);
            string secret = "";
            int controllerPort = DefaultControllerPort;
            int mixedPort = DefaultMixedPort;
            if (TryReadTopLevelYamlScalar(text, "secret", out string parsedSecret))
            {
                secret = parsedSecret;
            }

            if (TryReadTopLevelYamlScalar(text, "external-controller", out string bind) &&
                TryParseControllerPort(bind, out int parsedPort))
            {
                controllerPort = parsedPort;
            }

            if (TryReadTopLevelYamlScalar(text, "mixed-port", out string mixedText) &&
                int.TryParse(mixedText, out int parsedMixed) &&
                parsedMixed is > 0 and <= 65535)
            {
                mixedPort = parsedMixed;
            }

            cfg = new KernelRuntimeConfig
            {
                Secret = secret,
                ControllerPort = controllerPort,
                MixedPort = mixedPort
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveConfigFile(out string configPath)
    {
        configPath = "";
        string? kernelPath = ResolveKernelPathHint();
        if (TryFindTaskByKernelPath(kernelPath, out ManagedTaskInfo? task) && task is not null &&
            TryResolveTaskConfigFile(task, out configPath))
        {
            return true;
        }

        return TryFindConfigInDirectory(Path.GetDirectoryName(kernelPath ?? ""), out configPath);
    }

    private static bool TryResolveTaskConfigFile(ManagedTaskInfo task, out string configPath)
    {
        configPath = "";
        List<string> args = SplitArgs(task.Arguments);
        TryGetFlagValue(args, "-f", "--config", out string config);
        TryGetFlagValue(args, "-d", "--dir", out string dir);

        string work = task.WorkingDirectory.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(work))
        {
            work = Path.GetDirectoryName(task.KernelPath) ?? "";
        }

        string home = work;
        if (!string.IsNullOrWhiteSpace(dir))
        {
            home = Path.IsPathRooted(dir) ? dir : Path.GetFullPath(Path.Combine(work, dir));
        }

        if (string.IsNullOrWhiteSpace(config))
        {
            return TryFindConfigInDirectory(home, out configPath);
        }

        string full = Path.IsPathRooted(config) ? config : Path.GetFullPath(Path.Combine(home, config));
        if (!File.Exists(full))
        {
            return false;
        }

        configPath = full;
        return true;
    }

    private static bool TryFindConfigInDirectory(string? directory, out string configPath)
    {
        configPath = "";
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        foreach (string name in new[] { "config.yaml", "config.yml" })
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                configPath = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetFlagValue(List<string> args, string shortName, string longName, out string value)
    {
        value = "";
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg.Equals(shortName, StringComparison.OrdinalIgnoreCase) ||
                arg.Equals(longName, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    value = args[i + 1].Trim().Trim('"');
                    return value.Length > 0;
                }

                return false;
            }

            if (TryStripFlagPrefix(arg, shortName, out value) ||
                TryStripFlagPrefix(arg, longName, out value))
            {
                return value.Length > 0;
            }
        }

        return false;
    }

    private static bool TryStripFlagPrefix(string arg, string flag, out string value)
    {
        value = "";
        if (arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = arg.Substring(flag.Length + 1).Trim().Trim('"');
            return true;
        }

        if (flag.Length == 2 &&
            arg.StartsWith(flag, StringComparison.OrdinalIgnoreCase) &&
            arg.Length > flag.Length &&
            arg[flag.Length] != '-')
        {
            value = arg.Substring(flag.Length).Trim().Trim('"');
            return true;
        }

        return false;
    }

    private static List<string> SplitArgs(string? commandLine)
    {
        List<string> result = [];
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return result;
        }

        var current = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static bool TryReadTopLevelYamlScalar(string text, string key, out string value)
    {
        value = "";
        using StringReader reader = new(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#')
            {
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (!line.Substring(0, colon).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string raw = line.Substring(colon + 1).Trim();
            if (raw.Length == 0 || raw[0] is '|' or '>' or '{' or '[')
            {
                return false;
            }

            value = UnquoteYamlScalar(raw);
            return true;
        }

        return false;
    }

    private static string UnquoteYamlScalar(string raw)
    {
        if (raw.Length >= 2)
        {
            char quote = raw[0];
            if ((quote == '"' || quote == '\'') && raw[^1] == quote)
            {
                string inner = raw.Substring(1, raw.Length - 2);
                return quote == '"'
                    ? inner.Replace("\\\"", "\"").Replace("\\\\", "\\")
                    : inner;
            }
        }

        int comment = raw.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0)
        {
            raw = raw.Substring(0, comment).TrimEnd();
        }

        return raw.Trim();
    }

    private static bool TryParseControllerPort(string bind, out int port)
    {
        port = 0;
        bind = bind.Trim();
        if (bind.StartsWith('['))
        {
            int close = bind.LastIndexOf(']');
            int colon = bind.LastIndexOf(':');
            if (close >= 0 && colon > close)
            {
                return int.TryParse(bind.AsSpan(colon + 1), out port) && port is > 0 and <= 65535;
            }

            return false;
        }

        int last = bind.LastIndexOf(':');
        if (last < 0)
        {
            return false;
        }

        return int.TryParse(bind.AsSpan(last + 1), out port) && port is > 0 and <= 65535;
    }

    private static ManagedTaskInfo ReadTaskInfo(dynamic folder, dynamic task)
    {
        string description = Convert.ToString(task.Definition.RegistrationInfo.Description) ?? "";
        string kernel = "";
        string arguments = "";
        string workingDirectory = "";
        foreach (dynamic action in task.Definition.Actions)
        {
            try
            {
                string path = Convert.ToString(action.Path) ?? "";
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                arguments = Convert.ToString(action.Arguments) ?? "";
                workingDirectory = Convert.ToString(action.WorkingDirectory) ?? "";
                kernel = TryParseRunKernelPath(path, arguments, out string parsedKernel)
                    ? parsedKernel
                    : path;
                break;
            }
            catch
            {
            }
        }

        return new ManagedTaskInfo
        {
            TaskName = Convert.ToString(task.Name) ?? "",
            TaskPath = Convert.ToString(folder.Path) ?? "\\",
            KernelPath = kernel,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Description = description
        };
    }

    private static ManagedTaskInfo? FindInFolder(dynamic folder, Func<ManagedTaskInfo, bool> match)
    {
        foreach (dynamic task in folder.GetTasks(1))
        {
            ManagedTaskInfo info = ReadTaskInfo(folder, task);
            if (match(info))
            {
                return info;
            }
        }

        foreach (dynamic sub in folder.GetFolders(0))
        {
            ManagedTaskInfo? found = FindInFolder(sub, match);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static void RunHidden(string fileName, string arguments)
    {
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + fileName);
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? fileName + " 失败" : error.Trim());
        }
    }
}
