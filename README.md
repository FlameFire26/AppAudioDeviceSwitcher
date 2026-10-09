# AppAudioDeviceSwitcher

本地项目名称：AppAudioDeviceSwitcher。

按进程切换 Windows 应用音频输出设备的命令行工具。

## 开发与运行

技术：C# / .NET 8 Windows。

入口：`AppAudioDeviceSwitcher.slnx`。

使用安装了对应 SDK/工作负载的 Visual Studio 打开解决方案；较旧的工程可能需要兼容性调整。

## 项目记录

从本地历史项目迁移，源码和资源按原文件编码保留。本次迁移没有验证编译或运行结果。

开发记录：AI 参与过本项目的开发。

MSIX 开发证书和 EarTrumpet 参考源码副本未上传。`AppAudioDeviceSwitcher.Package/pack-msix.ps1` 包含创建开发证书的步骤；Windows SDK 路径需要按本机安装位置调整。主程序支持的参数可查看 `Program.cs`。
