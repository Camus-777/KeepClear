# 历史本机验证记录

以下内容归并自原界面、可靠性和托盘文档，保留其原有版本、数字与限制，不代表此次排版整理重新执行了这些测试。当前验证范围见 [VALIDATION.md](VALIDATION.md)。文中 artifacts 和 release 路径为开发记录的位置，未随源码包提供。

## 0.2.1 界面与音频基线

- 29 项 C#、8 项 Python 基础检查通过。
- 中英文界面各检查 4 个状态，全部控件在 480 × 456 客户区内，提示文字未溢出；语言即时切换通过。预览为本应用的控件渲染，不是桌面录屏。
- 监听播放器合成音回环：144480 样本，RMS 0.030934，测得主频 996.875 Hz，幅度 0.043905。监听有独立时钟校正，测试允许主频在 997 Hz 附近轻微偏移。
- `--monitor-engine` 使用本机 HyperX 耳机真实端点，连续两次开启/关闭监听；块计数持续增加，主输出欠载 0；禁止监听 VB-CABLE 的检查通过。没有保存麦克风音频，未进行人耳听感评分。
- 双模型管道每 16 ms 块平均往返 8.118 ms，最大 9.559 ms；主设备路由 30/4/4 秒三次运行均无输出欠载，每次各报告一次采集间断，不宣称全程无间断。
- worker/model_worker.py 与图标 SVG/ICO 的 SHA256 与 UI 调整前备份一致，模型 MANIFEST 和三个权重文件校验通过。

记录：artifacts/compact-ui/report.txt、monitor-loopback.txt、compact-monitor-engine.txt、compact-model-pipe.txt、compact-hardware.txt。上述测试不能替代长时间和跨设备使用验证。

基准源文件备份：release/KeepQuiet-before-compact-ui-20261114.zip。

## 0.3.0 早期可靠性基线

- 82 项 C# 基础检查、8 项 Python 检查通过。
- 独立配置文件覆盖 XML 空对象、损坏 XML、文件占用和实际读取权限被拒绝。权限测试仅修改新建的 artifacts/settings-errors 文件，并在 finally 恢复 ACL；不修改用户实际 settings.xml。
- 模拟卡住的采集线程，验证超时不标记完全释放、其他线程和监听仍清理、资源保持有效、阻塞结束后再次释放成功；并覆盖取消回调抛出异常、并发释放及释放后禁止启动。
- 设备夹具覆盖名称误导、其他 VB-Audio 产品、普通 USB 声卡、端点重命名及缺失元数据。本机 HyperX 和基础版 VB-CABLE 仍正常枚举。
- 中英文主界面检查包含保存语言的 I/O/权限失败，提示即时显示且可以退出。记录：artifacts/reliability-ui-0.3.0/report.txt。
- 启动界面与语言/重启替身检查通过。记录：artifacts/startup-ui-0.3.0/report.txt；不执行真实重启或驱动安装。
- 本机真实双模型与设备检查：关闭窗口后处理继续，约 2 秒推进 126 块，输出欠载 0；停止过程中请求托盘退出后成功停止。记录：artifacts/reliability-tray-hardware-0.3.0.txt。
- HyperX 耳机监听连续开关两次，主处理继续、输出欠载 0，VB-CABLE 作为监听设备被拒绝。记录：artifacts/reliability-monitor-0.3.0.txt。

真实设备检查未保存录音。模拟线程阻塞不等于复现所有驱动死锁；未替代长时间运行、休眠恢复、其他硬件和全新 Windows 的验收。独立运行包另外执行隔离搬移及真实双模型合成音自检。

## 托盘记录位置

artifacts/tray-ui/report.txt 和 artifacts/tray-hardware.txt；无新增录音或用户设置副作用。独立包沿用此前的运行时隔离和模型哈希验证。

0.2.8 托盘交互检查见 artifacts/tray-ui-0.2.8/report.txt，使用 NotifyIcon 鼠标事件和本应用控件验证，不操作真实桌面托盘，不打开音频流或保存用户设置。
