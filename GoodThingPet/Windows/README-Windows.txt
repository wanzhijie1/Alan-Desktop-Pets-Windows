三宠联动（SunPet 1.2.0 / GoodThingPet 1.3.0 / ShaolanPet Windows）
请使用本套便携包中的三个新版程序，并先退出之前的旧版。
点薄肌：薄肌 → 太对了 → 峰哥 → 太对了，到第四句停止。
点峰哥：峰哥 → 太对了，只接一次。点太对了不会触发其他桌宠。
仅开启薄肌和太对了时，会接一句；缺少的环节不会被跳过。
正在手动播放时不抢话；右键停止、关闭联动或退出会取消尚未开始的接话。
SunPet 的轮播排除项也不用于自动接话，固定播放偏好仍只控制手动触发。
可在各桌宠右键菜单中关闭捧哏联动。
采用本机当前用户、当前Windows会话的v2命名管道，不联网，不开网络端口。
v2与以前的Windows双宠v1通道及macOS通知协议均不互通，需要一起升级。
好事桌宠 · Windows 版 1.3.0

播放与接话优化
启动时预加载 WAV 为内存 PCM，去除播放素材首尾多余静音，并保持音频输出设备就绪。
点按和接话直接使用内存声音，减少临时打开文件和音频设备带来的停顿；原始素材文件保留。
沿用 v2 协议和原设置路径，升级无需清空设置；建议与 SunPet 1.2.0、薄肌桌宠 1.1.0 一起整包升级。

运行要求
Windows 10 / Windows 11，已安装 .NET Framework 4.8 或更新版本。
不需要安装 Python、Node.js、Visual Studio 或额外的 .NET SDK；运行时不需要网络。

开始使用
1. 完整解压压缩包到一个文件夹。
2. 双击 GoodThingPet.exe，人物会显示在桌面上。
3. 请保留同目录的 Assets 文件夹和 GoodThingPet.exe.config，不能只复制 exe。

操作
单击人物：随机播放一句原声，同时显示字幕和动画。
连续单击：切换到下一句，上一句立即停止。
拖动人物：移动位置。
右键人物或系统托盘图标：打开菜单，可挑一句、停止播放、调整音量和大小、恢复位置或退出。
菜单中的“减少动态效果”可以关闭说话动画，也会遵循 Windows 的动画效果设置。
位置、音量、大小与动态效果偏好会自动保存，下次打开时恢复。
设置文件：%LOCALAPPDATA%\GoodThingPet\settings.json
程序不会自动设置开机启动。

故障排查
如果系统提示缺少 .NET Framework 4.8，请从微软官方网站安装：
https://dotnet.microsoft.com/download/dotnet-framework/net48
没有声音时，检查桌宠菜单音量、Windows 音量混合器以及当前音频输出设备。
若找不到人物，可从任务栏右下角托盘菜单恢复位置；托盘图标可能在隐藏图标区域内。
此本地构建未做发布者数字签名。

从源码重新构建
在 Windows PowerShell 中运行：
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\build.ps1 -SelfTest
默认产物目录为 build\windows，可使用 -OutputDirectory 指定其他目录。
编译器使用 Windows 的 .NET Framework 自带 csc.exe，无需下载 NuGet 依赖。
命令行自检：GoodThingPet.exe --self-test（退出码 0 表示通过）。

来源与许可
原项目：https://github.com/Ganzhe2028/FengGe-Desktop-Pet
代码和文档许可见 LICENSE；参考项目 SunPet 许可见 LICENSE-SunPet。
人物图片和声音素材不属于上述代码许可范围。


