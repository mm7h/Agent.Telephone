# Agent.Telephone 运行包

这是包含 .NET 8 运行时的电话服务，适用于压缩包名称标明的系统及 CPU 架构。

1. 解压到独立目录，进入该目录操作；程序按当前工作目录读取 `configs/config.json`。
2. 修改 `configs/config.json` 中的模型服务凭据和电话网关配置，按需编辑 `configs/AssistantPrompts/`。
3. 从 [XiaoZhi.Net 资源页](https://github.com/mm7h/XiaoZhi.Net/releases/tag/resources) 下载对应平台的 FFmpeg 7.1.1 动态库，以及选用的模型，分别放入 `ffmpeg/`、`models/`。默认启动需要 `models/vad/silero-native/model.onnx`。
4. Windows：执行 `./Agent.Telephone.Sample.Server.exe`。
5. Linux：执行 `chmod +x ./Agent.Telephone.Sample.Server`，然后执行 `./Agent.Telephone.Sample.Server`。

如使用 Codex 助手，请在运行服务的同一账户下另行安装并登录 Codex CLI。

自包含程序仍依赖操作系统提供的基础库；Linux 运行包在 Ubuntu 24.04 runner 上构建，不适用于 Alpine/musl。单文件程序首次启动会解压原生库；使用 systemd 时设置可写的 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 并正确配置 `WorkingDirectory`。

`data/`、`logs/`、`ffmpeg/`、`models/` 在发行包中为空。升级时先备份自己的配置、模型及数据，再解压到新目录并迁移所需文件。

`package.json` 记录版本、tag、源码提交和 RID；`assembly-versions.json` 记录项目程序集版本。部署后需结合资源、凭据与电话网关完成通话验收。

完整部署步骤见 [快速开始与部署](docs/00-快速开始与部署.md)，Codex 配置见 [Codex 助手](docs/10-Codex助手.md)。
