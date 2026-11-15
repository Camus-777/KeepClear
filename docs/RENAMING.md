# KeepClear（保持清晰）首发名称与配置

KeepClear 首次公开发布的版本为 v0.3.0，中文名称为“保持清晰”。本次为首发版本，不提供开发阶段配置的数据迁移。

程序标题、启动动画、EXE、程序集、C# 命名空间、工程与图标文件名、配置/日志目录、单实例与驱动安装互斥锁、构建工具及发布附件均使用 KeepClear。音波图形、模型权重和串联音频参数保留。

用户配置仅从 %LOCALAPPDATA%/KeepClear/settings.xml 读取和保存。驱动安装状态仅由 KeepClear/driver-setup/install-state.xml 管理，不读取旧开发名目录中的设置或安装记录；没有设置时使用英文等安全默认值。已保存的 KeepClear 设置继续生效。

没有有效已保存虚拟输出时，启动自检和主界面刷新优先选择普通 CABLE Input。已通过硬件/服务身份核验的端点按 Windows 类型排序：普通 Speakers 端口优先；可选类型信息缺失时，才用普通端口名称辅助排序。名称不参与虚拟声卡身份认定。有效的已保存多声道选择仍被保留；只有多声道端口可用时允许回退使用它。

自研代码、文档与图标统一署名为 KeepClear contributors；第三方作者及原始许可保持不变。VB-CABLE 的厂商名称、硬件 ID、服务、原始安装包及其数字签名不变。当前验证范围见 [VALIDATION.md](VALIDATION.md)。
