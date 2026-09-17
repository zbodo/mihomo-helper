# Mihomo Helper

Windows 上用于裸内核运行 [mihomo](https://github.com/MetaCubeX/mihomo) 的简易窗口工具。不托管内核配置，只负责本机启停、系统代理、TUN，以及开机计划任务。

程序以管理员身份启动（`requireAdministrator`）。目标框架为 .NET 10 / Windows App SDK，unpackaged 自包含，仅 x64。

## 功能

窗口会显示系统代理、TUN、内核路径，并提供：

- **内核路径**：选择或粘贴 mihomo 的 `.exe`。有内容时写入 `HKCU\Software\MihomoTray\KernelPath`；留空则使用本应用标记过的计划任务内核路径。
- **工作模式**：独立开关系统代理与 TUN。二者互斥，开启一侧会尽量关掉另一侧。
- **启动 / 终止内核**：优先跑本应用创建的计划任务；没有任务时，用已保存的内核路径在后台启动。终止前会确认。
- **启动 WEBUI**：仅当内核 API 可认证，且 `external-controller` 的 `/ui/` 可访问时按钮才可用。浏览器打开 zashboard 等面板，并带上本机地址与 `secret`。
- **计划任务**：添加名为 `mihomo` 的 SYSTEM 开机任务；删除时按任务描述里的 UUID 匹配，找不到再按内核路径匹配。
- **刷新状态**：重新读取代理、TUN 和内核路径。

启动后会尝试从配置打开一次系统代理。之后通过 WebSocket `/traffic` 监视内核是否在线，用来刷新按钮状态。

## 准备内核和配置

自行准备 mihomo Windows 内核，并把配置放在内核同目录（或计划任务 `-d` / `-f` 指向的位置）：

- `config.yaml` 或 `config.yml`
- 内核启动参数固定为 `-d .\ -f config.yaml`

本工具只读取配置里的：

| 字段 | 用途 | 缺省 |
| --- | --- | --- |
| `mixed-port` | 系统代理地址 `127.0.0.1:<端口>` | `7890` |
| `external-controller` | 内核 HTTP API / WebUI 端口 | `9090` |
| `secret` | API Bearer 认证；打开 WebUI 时写入 URL | 空 |

仓库自带 `Assets/config.example.yaml`。添加计划任务时，若内核目录里还没有 `config.yaml` / `config.yml`，会把这份示例复制过去。

WebUI 需要配置里启用外部界面（示例使用 `external-ui` / zashboard）。按钮可用条件是 `GET /configs` 认证通过，且 `http://127.0.0.1:<控制器端口>/ui/` 返回 2xx/3xx。

## 计划任务

「添加任务」会创建 `\mihomo`：

- 描述中写入 UUID `6B8E2C14-A91F-4D53-B7E0-3C1A9F8D2465`，用于识别本应用创建的任务，已存在则不会重复创建
- 以 `SYSTEM`（`S-1-5-18`）最高权限运行，登录触发、允许按需启动、失败后每分钟重试最多 3 次
- 工作目录为内核所在目录，参数为 `-d .\ -f config.yaml`

开启 TUN 时，若当前进程权限不够，会尝试先跑这条 SYSTEM 任务；仍失败再提权启动内核。没有 SYSTEM 任务、又拒绝管理员授权时，TUN 无法打开。

删除任务需要确认。删除成功后会结束对应内核进程。

## 构建

需要 .NET 10 SDK。unpackaged 运行：

```powershell
dotnet run -c Release
```

Visual Studio 选择 **Mihomo Helper (Unpackaged)** 配置文件。产物程序名为 `MihomoHelper.exe`。

无参数启动打开窗口。带操作名的命令行用于提权子进程（例如 `InstallTask`、`RemoveTaskConfirm`、`RunKernel`），一般不必手动调用。
