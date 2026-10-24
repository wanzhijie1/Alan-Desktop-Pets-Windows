太对了 · SunPet · Windows 版 1.2.0

运行要求
Windows 10 / Windows 11，已安装 .NET Framework 4.8 或更新版本。
运行时不需要 Python、Node.js、Visual Studio、额外的 .NET SDK 或网络。

开始使用
1. 将便携包完整解压到一个文件夹。
2. 双击 SunPet.exe。
3. 保留同目录的 Assets 文件夹和 SunPet.exe.config；不能只复制 exe。

操作
单击头像：播放一句原声，显示字幕和说话动画。
再次单击：停止上一句，立即播放下一句，声音不会叠加。
拖动头像：移动位置。
右键头像或使用系统托盘图标：打开菜单，可挑一句、停止播放、调整音量和大小、恢复位置或退出。
“播放方式”支持随机播放和固定播放某一句。
“轮播内容”可以排除不想随机听到的句子；排除项也不用于自动接话。
菜单中的“减少动态效果”可以关闭说话动画，也会遵循 Windows 的动画效果设置。
位置、音量、大小及播放偏好会保存到 %LOCALAPPDATA%\SunPet\settings.json。
程序不会自动设置开机启动。

播放与接话优化
启动时预加载 PCM 音频到内存，去除播放素材首尾多余静音，并保持音频输出设备就绪。
点按和接话直接使用内存声音，减少临时解码和重新打开输出设备造成的停顿。
原始 .m4a 和 clips.json 保留；Assets/PCM 内的 WAV 及 manifest.json 也需随程序保留。
沿用现有 v2 协议和设置路径，升级无需清空设置；建议三宠整包升级，让整条链使用新的播放方式。

三宠联动（SunPet 1.2.0 / GoodThingPet 1.3.0 / ShaolanPet 1.1.0）
请使用本套便携包中的三个新版程序，并先退出之前的旧版。
点薄肌：薄肌 → 太对了 → 峰哥 → 太对了，到第四句停止。
点峰哥：峰哥 → 太对了，只接一次。点太对了不会触发其他桌宠。
仅开启薄肌和太对了时，会接一句；缺少的环节不会被跳过。
正在手动播放时不抢话；右键停止、关闭联动或退出会取消尚未开始的接话。
SunPet 的轮播排除项也不用于自动接话，固定播放偏好仍只控制手动触发。
可在各桌宠右键菜单中关闭捧哏联动。
采用本机当前用户、当前Windows会话的v2命名管道，不联网，不开网络端口。
v2与以前的Windows双宠v1通道及macOS通知协议均不互通，需要一起升级。
平台差异
Windows 版 1.2.0保留原始 28 段 .m4a 音频、图片、字幕、拖动、托盘菜单、播放方式和轮播排除设置。
机身摇晃依赖原项目的 Apple SPU 加速度计接口，只适用于 macOS；本 Windows 版 1.2.0不提供摇晃触发。
Windows 使用系统托盘图标，不使用 macOS 菜单栏或 Dock。

故障排查
缺少 .NET Framework 4.8 时，从微软官方网站安装：
https://dotnet.microsoft.com/download/dotnet-framework/net48
没有声音时，检查桌宠音量、Windows 音量混合器和输出设备，并确认 Assets/PCM 文件夹完整。
若找不到头像，可从任务栏右下角托盘菜单恢复位置，图标可能在隐藏图标区域内。
本地构建未做发布者数字签名。

从源码构建
在项目根目录运行：
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\build.ps1 -SelfTest
默认输出 build\windows；可以添加 -OutputDirectory 指定其他目录。
使用 Windows 自带 .NET Framework 的 csc.exe，无需下载 NuGet 依赖。
源码包已包含预转换 PCM。仅在替换音频或 PCM 缺失时，使用 Python 和 ffmpeg 重新准备：
python Windows/prepare_audio.py --ffmpeg <ffmpeg.exe路径>
转换后再运行 build.ps1，原始 .m4a 和 clips.json 不会被覆盖。
诊断命令：
SunPet.exe --self-test  检查素材和核心逻辑，退出码 0 表示通过。
SunPet.exe --smoke-test 静音验证实际窗口和媒体播放，结束后自动退出。
SunPet.exe --ui-test    启动用于界面验证的诊断模式。

来源与许可
原项目：https://github.com/Ganzhe2028/SunPet
SunPet 代码许可见 LICENSE；复用的好事桌宠 Windows 基础代码许可见 LICENSE-Windows-Base。
图片和音频素材版权归原权利人所有，不属于上述 MIT 代码许可范围。

