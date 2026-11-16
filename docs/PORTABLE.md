# KeepClear 0.3.1 独立运行包

## 使用与边界

交付目录为 `release/KeepClear-0.3.1-win-x64`。解压完整 ZIP，双击 KeepClear.exe，不需要另装 Python、pip、PyTorch、NumPy、Alkove 或 CUDA。正常推理只使用随包文件。

仍需要 Windows 10/11 x64、.NET Framework 4.8。VB-CABLE 基础版官方原始安装包已嵌入 EXE，缺少驱动时自动请求管理员授权安装；健康的已有驱动不重装；缺失/不完整时尝试一次安装，最多等待约 20 秒，未就绪则保存等待状态避免重复安装；之后按真实状态继续或提示重启，不改变系统默认音频设备。详见 [STARTUP-INSTALL.md](STARTUP-INSTALL.md)。整个文件夹可以移动；程序始终优先使用自身目录的 `runtime/python.exe`，旧设置中即使有可用的开发机 Python 绝对路径也不会优先使用它。标记为独立包但缺失运行时的情况会报错，不会悄悄改用系统 Python。

普通 `build.ps1` 生成的 dist 仍是开发输出；完整运行环境位于上述 release 目录。

当前版本包含固定窗口、单实例和关闭到托盘行为，使用与验证见 [TRAY.md](TRAY.md)。退出应用请使用托盘菜单；关闭窗口不会停止音频。

## 打包方法

```
powershell -NoProfile -ExecutionPolicy Bypass -File ./build-portable.ps1
.venv/Scripts/python.exe tools/validate_portable.py release/KeepClear-0.3.1-win-x64
```

build-portable.ps1 会依次编译、组装、隔离验证、压缩，再逐文件核验 ZIP 内容，并输出同名 .zip.sha256。第二行可用于单独重跑验证。首次需要下载官方 CPython 嵌入式包；缓存已存在时可离线组装。目标目录必须在项目 release 内且原先不存在，脚本不会覆盖既有交付目录。

- 官方 Windows x64 CPython 3.12.10 嵌入式包，SHA256 固定为 `4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3`。下载及原始许可均保留来源。
- `python312._pth` 仅列出相对路径：标准库 ZIP、运行时根目录和本地 site-packages，不执行 import site；不读取注册表/用户 site-packages/环境 PYTHONPATH，不带 venv 重定向配置。
- 工作进程使用 -I -B 启动，不依赖环境变量，不向程序目录写入 Python 字节码缓存。用户设置仍写入用户的 LocalAppData。
- 从现有已锁定环境复制 12 个分发包，逐文件核对 wheel RECORD 哈希；保留 Python 源码、原生 DLL/PYD、包元数据及许可证。没有运行 pip 安装到系统或修改全局 Python。
- 去掉静态 `.lib`、编译头文件/CMake 文件及调试符号；保留模型推理所需文件。没有增加冻结/安装框架。
- Python 嵌入式包自带 VCRUNTIME140/140_1；PyTorch 要求的 MSVCP140 使用已锁定 NumPy wheel 自带的未修改 DLL，以标准名另存，避免依赖开发机 System32 的该 DLL。原始文件和来源也保留。此工程来源记录不是全依赖授权审计结论。
- Focus、Cue、DNS48 三个原始权重全部校验；不携带已废弃的 Timbre/声纹文件。
- PORTABLE.json 记录所有交付文件 SHA256、环境版本、来源及省略的编译文件体积；不记录用户设备配置、录音或声纹。

## 环境版本说明

3.12.10 是为了复现本项目已经验证的依赖和处理结果选用的兼容基准，不是 Python 3.12 的最新安全补丁。官方后续 3.12 为安全维护、仅源码发行。此工作解决运行环境自包含和可搬移问题；正式长期公开维护时，应另行验证更新 Python/依赖组合并重建锁文件，不将旧兼容基准当成永久发行版本。

来源：

- https://www.python.org/downloads/release/python-31210/
- https://docs.python.org/3.12/using/windows.html#the-embeddable-package
- https://devguide.python.org/versions/

## 验证方法

validate_portable.py 检查文件清单、模型、原生 DLL 依赖，将整个包复制到含中文和空格的新位置，收窄 PATH，并注入错误的 PYTHONHOME/PYTHONPATH。检查解释器前缀、模块路径、实际加载的 Python/MSVC DLL 都在随包 runtime 内；再通过真实 EXE 的 --self-test 走 C# → Python → 双模型管道。

该测试不修改/卸载本机 Python，不修改用户设置，不录音、不打开音频设备。它证明本机受控环境中的自包含行为；不能替代另一台全新 Windows 机器的验收，也不能保证所有游戏/声卡兼容性。硬件链路和 UI 另外测试。

第三方许可仍分别适用，Denoiser 的 CC BY-NC 4.0 非商业限制不会因打包改变。
