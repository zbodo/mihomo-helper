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

    private const string ControllerApi = "http://127.0.0.1:9090";
    private const string DefaultTaskName = "mihomo";
    private const string KernelArgs = "-d .\\ -f config.yaml";
    private const int ProxyPort = 7890;
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string SettingsPath = @"Software\MihomoTray";
    private const string ProxyOverride = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";

    private static readonly HttpClient Http = CreateClient();

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
            case "Stop":
                SetProxy(false);
                StopKernel();
                break;
            case "InstallTask":
                EnsureAdmin("InstallTask", GetKernelPath());
                InstallTask(GetKernelPath());
                StartManagedTask();
                break;
            case "RemoveTask":
                EnsureAdmin(skipConfirm ? "RemoveTaskConfirm" : "RemoveTask", ResolveKernelPathHint());
                RemoveTask(skipConfirm);
                break;
            case "RemoveTaskConfirm":
                EnsureAdmin("RemoveTaskConfirm", ResolveKernelPathHint());
                RemoveTask(true);
                break;
            case "StartTask":
                EnsureAdmin("StartTask");
                StartManagedTask();
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

    public static string GetSavedKernelPathOrEmpty()
    {
        return TryGetSavedKernelPath(out string path) ? path : "";
    }

    public static string? GetCurrentTaskKernelPath()
    {
        if (TryFindManagedTask(out ManagedTaskInfo? managed) &&
            managed is not null &&
            !string.IsNullOrWhiteSpace(managed.KernelPath))
        {
            return managed.KernelPath;
        }

        try
        {
            Type? type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                return null;
            }

            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            dynamic task = service.GetFolder("\\").GetTask(DefaultTaskName);
            foreach (dynamic action in task.Definition.Actions)
            {
                string? path = Convert.ToString(action.Path);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
        }
        catch
        {
        }

        return null;
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
        if (TryGetSavedKernelPath(out string saved) && saved.Length > 0)
        {
            return saved;
        }

        return GetCurrentTaskKernelPath();
    }

    private static string GetApiSecret()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsPath, false);
        return Convert.ToString(key?.GetValue("ApiSecret"))?.Trim() ?? "";
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        string secret = GetApiSecret();
        if (secret.Length > 0)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
        return client;
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

    private static void SetProxy(bool enable)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
        if (key is null)
        {
            throw new InvalidOperationException("无法打开代理注册表项");
        }

        key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", enable ? $"127.0.0.1:{ProxyPort}" : string.Empty, RegistryValueKind.String);
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
        using StringContent content = new(
            enable ? "{\"tun\":{\"enable\":true}}" : "{\"tun\":{\"enable\":false}}",
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await Http.PatchAsync(ControllerApi + "/configs", content);
        response.EnsureSuccessStatusCode();
    }

    private static string QueryTun()
    {
        try
        {
            string json = Http.GetStringAsync(ControllerApi + "/configs").GetAwaiter().GetResult();
            int tunAt = json.IndexOf("\"tun\"", StringComparison.OrdinalIgnoreCase);
            if (tunAt < 0)
            {
                return "未知";
            }

            string slice = json.Substring(tunAt, Math.Min(120, json.Length - tunAt));
            return slice.Contains("\"enable\":true", StringComparison.OrdinalIgnoreCase) ||
                   slice.Contains("\"enable\": true", StringComparison.OrdinalIgnoreCase)
                ? "启用"
                : "关闭";
        }
        catch
        {
            return "无法连接内核";
        }
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
            using HttpResponseMessage response = Http.GetAsync(ControllerApi + "/configs").GetAwaiter().GetResult();
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
                Process.Start(new ProcessStartInfo
                {
                    FileName = kernelExe,
                    Arguments = KernelArgs,
                    WorkingDirectory = Path.GetDirectoryName(kernelExe) ?? "",
                    UseShellExecute = false
                });
            }

            WaitForKernel();
            return;
        }

        throw new InvalidOperationException("启动内核失败：未找到计划任务 mihomo");
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

        Type type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("无法访问任务计划程序");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = "MihomoTray " + TaskUuid;

        definition.Triggers.Create(8);
        dynamic action = definition.Actions.Create(0);
        action.Path = kernelExe;
        action.Arguments = KernelArgs;
        action.WorkingDirectory = Path.GetDirectoryName(kernelExe) ?? "";

        definition.Principal.UserId = "S-1-5-18";
        definition.Principal.LogonType = 5;
        definition.Principal.RunLevel = 0;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.Enabled = true;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.MultipleInstances = 2;
        definition.Settings.RestartInterval = "PT1M";
        definition.Settings.RestartCount = 3;

        folder.RegisterTaskDefinition(DefaultTaskName, definition, 2, null, null, 5);
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
                "Mihomo",
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

        RunHidden("schtasks.exe", $"/Delete /TN \"{existing.FullName}\" /F");
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

    private static ManagedTaskInfo ReadTaskInfo(dynamic folder, dynamic task)
    {
        string description = Convert.ToString(task.Definition.RegistrationInfo.Description) ?? "";
        string kernel = "";
        foreach (dynamic action in task.Definition.Actions)
        {
            try
            {
                kernel = Convert.ToString(action.Path) ?? "";
            }
            catch
            {
            }

            if (!string.IsNullOrEmpty(kernel))
            {
                break;
            }
        }

        return new ManagedTaskInfo
        {
            TaskName = Convert.ToString(task.Name) ?? "",
            TaskPath = Convert.ToString(folder.Path) ?? "\\",
            KernelPath = kernel,
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
