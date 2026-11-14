# 实现来源与依赖边界

本次从空目录编写的部分：桌面界面、设备路由封装、音频线程调度、有界 FIFO、流式 windowed-sinc FIR 重采样、PCM 转换、串联接口、模型工作进程协议、增益平滑/限幅、配置持久化与测试。

Windows COM 的 GUID、接口签名与音频格式按公开平台规范声明；算法采用公开的 FIR/windowed-sinc 方法，不主张发明通用算法。界面与设备路由没有复制第三方应用或 UI 模板；模型推理使用明确隔离的上游代码，不将模型或模型效果称作本项目原创。

| 项目 | 本次状态 | 作用与注意事项 |
|---|---|---|
| Windows WASAPI / WinForms / .NET Framework 4.8 | 使用系统已有组件；不复制运行时 | 音频 API、界面与托管运行时，遵循 Microsoft 平台条款 |
| VB-CABLE | 官方基础包 VBCABLE_Driver_Pack46.zip 原样嵌入 EXE；缺少驱动才调用原安装程序 | VB-Audio Donationware，公开分发条款允许符合要求的原包捆绑/静默部署；来源及支持方式见 third_party/vbcable/NOTICE.txt。不含 A+B/C+D，不将驱动视为原创 |
| Python | 开发使用 3.12.10 .venv；独立包附官方 x64 嵌入式解释器 | Python 原始许可保留在 runtime/LICENSE.txt，来源/哈希见 PORTABLE.json |
| Alkove / NumPy | 项目 .venv 安装 0.4.0 / 2.2.6，独立包附对应运行依赖；Focus 和 Cue 权重位于 models | Alkove 为 Apache-2.0；LICENSE/NOTICE 位于 third_party/alkove；NumPy 许可保留在对应 dist-info 内 |
| Facebook Denoiser | third_party/denoiser 保留上游 4 个未改动的 Python 推理模块、LICENSE、README、UPSTREAM.json；下载官方 DNS48 权重 | CC BY-NC 4.0，含非商业限制。提交 8afd7c166699bb3c8b2d95b6dd706f71e1075df0；上游已归档 |
| PyTorch | 项目 .venv 安装 2.8.0+cpu；独立包包含运行文件，省略编译静态库/头文件 | PyTorch 及其必需依赖的许可保留在 runtime/Lib/site-packages 对应 dist-info 内，版本及 wheel 哈希见 requirements-models.lock.txt |
| Microsoft C++ runtime DLL | Python 嵌入式包自带 VCRUNTIME；MSVCP140 来自已锁定 NumPy wheel 的同一未修改 DLL，并另存标准文件名 | 不从开发机 System32 随意复制 DLL；来源记录见 PORTABLE.json，原始发行文件及其许可保持随包 |

音频/界面核心没有 NuGet、pip 或 npm 依赖。启用模型时需要上述 Python 运行环境。12 个 Python 分发包均由锁文件固定；未安装 Denoiser 的训练、评分、torchaudio、sounddevice 等无关依赖。

“原创内容占比”取决于统计口径。本项目不以增加代码量来凑百分比，也不把系统运行时、驱动、开源模型或训练权重计为原创。没有做全依赖许可证审计或逐行法律鉴定；本文件是工程来源记录，不是权属/合规结论。自研代码、项目文档和图标采用 CC BY-NC 4.0；许可正文及范围见根目录 LICENSE、NOTICE 和 LICENSING.md。

参考：[Microsoft WASAPI](https://learn.microsoft.com/en-us/windows/win32/coreaudio/wasapi)、[VB-Audio](https://vb-audio.com/Cable/)、[Alkove](https://github.com/alkove-ai/alkove)、[Denoiser](https://github.com/facebookresearch/denoiser)。
