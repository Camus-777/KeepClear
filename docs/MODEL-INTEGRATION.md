# 模型接入与复现（0.3.3）

**已在本机接入真实 Alkove Focus → Facebook Denoiser DNS48。** 已验证实际推理、中文合成语音、C# 管道和 WASAPI→VB-CABLE 设备链路。真人听感、多人重叠与游戏内效果仍待验证。

## 本机直接使用

普通用户打开完整独立包中的 `KeepClear.exe`；开发者也可打开 `dist/KeepClear.exe`。两级模型固定全开，按 Alkove → Denoiser 运行。选择真实麦克风和 CABLE Input，点击“启动”。游戏/通话里选择 CABLE Output 作为麦克风。

当前 `release/KeepClear-0.3.3-win-x64` 已包含独立 Python、模型和运行依赖，详见 [PORTABLE.md](PORTABLE.md)。普通构建生成的 dist 仍为开发输出，只有 dist 时需要项目 .venv 或显式配置的 Python，不能把它当作完整独立包。

## 固定版本

| 部分 | 已验证版本/来源 |
|---|---|
| Python | Windows x64 CPython 3.12.10 |
| Alkove | 0.4.0，Windows abi3 官方 wheel |
| NumPy | 2.2.6 |
| PyTorch | 2.8.0+cpu，官方 CPU wheel |
| Denoiser | 上游提交 `8afd7c166699bb3c8b2d95b6dd706f71e1075df0` 的原始推理模块 |
| Denoiser 权重 | 官方 DNS48 纯 tensor state_dict，`dns48-11decc9d8e3f0998.th`，下载后原样保存为 `dns48-state.pt` |

`requirements-models.lock.txt` 记录所有 12 个 Python 运行分发包的精确版本和 wheel SHA256。`models/MANIFEST.json` 记录模型 SHA256、Alkove 模型 revision、Denoiser 源码提交；`third_party/denoiser/UPSTREAM.json` 记录原始源码文件 SHA256。没有安装 Denoiser 的 Hydra、torchaudio、sounddevice、评分或训练依赖。

## 从源码恢复环境

Windows x64 Python 3.12、.NET Framework 4.8 和官方 VB-CABLE 驱动需已安装。首次执行需要联网下载依赖与模型：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./setup-models.ps1
```

该脚本只在本项目 `.venv` 安装依赖，不升级全局 Python 或 pip。所有 wheel 使用 `--require-hashes`；模型下载与 Denoiser 原始源码均检查 SHA256。已有且校验成功的模型不会重复下载。模型准备后调用 `build.ps1`，将推理代码及权重部署到 `dist`。

正常运行时仅加载本地模型，不下载、不录音、不上传。原始推理模块在源码包中保留，没有运行时 GitHub 下载步骤。

## 路径与流式约定

```text
.venv/Scripts/python.exe
models/MANIFEST.json
third_party/denoiser/denoiser/    # 未改动的上游模块
dist/
  KeepClear.exe
  workers/model_worker.py
  third_party/denoiser/denoiser/
  models/alkove/alkove-focus-1-preview.alk
  models/alkove/alkove-cue-1-preview.alk
  models/denoiser/dns48-state.pt
```

- 内部格式：16 kHz、单声道 float32，每块 256 样本。状态在整个通话期间保留。
- Alkove 0.4.0 的 `Processor` 即使 `cue=False` 也校验 Focus 与 Cue，因此需提供两个 .alk 文件；不开启 VAD 门控、声纹注册或自动增益，`normalize=False`。
- Denoiser 采用 DNS48 因果结构和 `DemucsStreamer.feed()`，不逐块调用离线 forward。CPU 执行使用 ATen 路径，关闭该 worker 的 oneDNN/MKLDNN 分派。至少 4 个逻辑处理器时使用 2 个计算线程，否则使用 1 个，interop 固定为 1，并请求清零极小非正规浮点数。保持 float32、原始权重、dry=0 和 num_frames=1。启动时先预热内核，再创建全新流状态，预热音频不进入输出。
- DNS48 使用 `weights_only=True`、严格 state_dict 结构校验及完整文件 SHA256；不使用不受限 pickle 加载。
- 支持上一级返回空块；完整串联首次非空回复出现在第 4 个 256 样本请求，持续流通常暂留 768 样本（48 ms）。停止时丢弃尾部，不在停止后继续输出。
- 输出还包含框架的弹性缓冲、设备缓冲和重采样开销。本机测量的推理耗时**不是端到端延迟**。
- 主界面固定启用两级模型；单级比较仅用于开发测试，界面不提供模型开关或动态增益调节。

## 验证命令

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./build.ps1 -Test
# 需要 artifacts/fixtures/tts-clean.wav 本地 16 kHz mono PCM16 测试语音
./.venv/Scripts/python.exe ./tools/validate_models.py
./dist/KeepClear.Tests.exe --models ./artifacts/model-pipe.txt
./dist/KeepClear.Tests.exe --hardware-models ./artifacts/hardware-models.txt
```

`--hardware-models` 会实际打开麦克风并输出至 VB-CABLE；三次运行分别约 30/4/4 秒。测试不保存用户麦克风音频。验证结果和限制见 [VALIDATION.md](VALIDATION.md)。

## 来源与许可

- [Alkove Python API](https://github.com/alkove-ai/alkove/blob/main/python/README.md)：Apache-2.0，保留 LICENSE/NOTICE。
- [Denoiser](https://github.com/facebookresearch/denoiser)：CC BY-NC 4.0，包含非商业限制；原始 LICENSE 与来源保留在 third_party。
- [PyTorch CPU 分发](https://download.pytorch.org/whl/cpu)：运行环境保留其自带许可。

商业发布前需要处理 Denoiser 授权；不可将这些模型、权重、上游推理模块计为 KeepClear 原创内容。自研部分采用 CC BY-NC 4.0，第三方许可分别保留。
