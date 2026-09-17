# Mihomo Tray

Windows 上用于裸内核运行 [mihomo](https://github.com/MetaCubeX/mihomo) 的简易窗口工具。

不是系统托盘客户端，也不附带完整 Clash 面板或订阅管理。打开窗口即可控制本机内核：切换系统代理与 TUN、启停内核、管理开机计划任务。

需要自行准备 mihomo 内核和配置文件。若外部控制器启用了密钥，可在注册表 `HKCU\Software\MihomoTray\ApiSecret` 中填写。
