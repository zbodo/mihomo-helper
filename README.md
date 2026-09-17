# Mihomo Helper

Windows 上用于裸内核运行 [mihomo](https://github.com/MetaCubeX/mihomo) 的简易窗口工具。

打开窗口即可控制本机内核：切换系统代理与 TUN、启停内核、管理开机计划任务。启动时会请求管理员权限。

需要自行准备 mihomo 内核和配置文件。若外部控制器启用了密钥，可在注册表 `HKCU\Software\MihomoTray\ApiSecret` 中填写。