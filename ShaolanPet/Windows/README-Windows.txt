薄肌桌宠 · ShaolanPet · Windows 版 1.1.0

运行要求
Windows 10 / Windows 11，已安装 .NET Framework 4.8 或更新版本。
不需要 Python、Node.js、Visual Studio、额外的 .NET SDK；运行时不需要网络。

开始使用
1. 将便携包完整解压到一个文件夹。
2. 双击 ShaolanPet.exe。
3. 保留同目录的 Assets 文件夹和 ShaolanPet.exe.config；不能只复制 exe。

基本操作
单击人物：播放一句原声，显示字幕和说话动画。
再次单击：停止上一句，立即播放下一句，声音不会叠加。
拖动人物：移动位置。
右键人物或系统托盘图标：打开菜单，可挑一句、停止播放、调整音量和大小、恢复位置或退出。
“播放方式”支持随机播放和固定播放某一句；固定模式下手动挑选会更换固定句。
“人物大小”提供 190 / 260 / 340 三档。
“减少动态效果”可以关闭说话动画，也会遵循 Windows 的动画效果设置。

轮播内容
素材共有 37 句。首次启动根据 Assets/clips.json 的 defaultOff 标记启用 8 句：
07 AI 救不了你；16 数字不会骗人；17 我不相信你没有空健身；19 蛋白粉不是科技；
26 过度健身吸引到的是同性；30 薄肌的核心逻辑就是要帅；31 我已经是版本之子了；32 中国人必须得吃饭。
右键 →“轮播内容”打开选择窗口，可以连续勾选，不会每选一句就关闭。
取消勾选只影响随机轮播，固定播放和手动挑一句不受影响；至少保留一条随机语音。
“全部恢复”会开启全部 37 句；“完成”关闭选择窗口。
选择会自动保存，下次打开继续使用。

播放与接话优化
启动时将 WAV 音频预加载为内存 PCM，去除播放素材首尾多余静音，并保持音频输出设备就绪。
点按和接话直接使用内存中的声音，减少逐句打开文件和输出设备造成的停顿；原始素材文件保留。

三宠自动接话
建议整包升级到配套新版：薄肌桌宠 1.1.0、SunPet 1.2.0、好事桌宠 1.3.0，并分别启动：
ShaolanPet.exe（薄肌） / SunPet.exe（太对了） / GoodThingPet.exe（峰哥）。
点薄肌：薄肌 → SunPet → 峰哥 → SunPet，共四句，第四句后结束。
点峰哥：峰哥 → SunPet，接一句后结束。
点 SunPet：只播放 SunPet，不带动其他桌宠。
薄肌负责起头，不自动接别人的话；单独运行也可以使用。
桌宠正在播放时可能跳过接话，不保证每次点按都接完四句。
沿用现有 v2 本机联动协议和设置路径，升级无需清空旧设置；同时升级三只桌宠可让整条链使用新的播放方式。
联动只在本机 Windows 程序之间进行。更早的 v1 包不支持三宠协议，请勿混用。
Windows 与 macOS 的 DistributedNotificationCenter 通知不互通。
本 Windows 版不提供机身摇晃触发。

设置与故障排查
位置、音量、大小及播放偏好保存到 %LOCALAPPDATA%\ShaolanPet\settings.json。
程序不会自动设置开机启动。
缺少 .NET Framework 4.8 时，从微软官方网站安装：
https://dotnet.microsoft.com/download/dotnet-framework/net48
没有声音时，检查桌宠菜单音量、Windows 音量混合器和当前输出设备。
若找不到人物，可从任务栏右下角托盘菜单恢复位置，图标可能在隐藏图标区域内。
本地构建未做发布者数字签名。

从源码构建
在项目根目录运行：
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\build.ps1 -SelfTest
默认输出 build\windows；可以添加 -OutputDirectory 指定其他目录。
使用 Windows 自带 .NET Framework 的 csc.exe，无需下载 NuGet 依赖。
诊断命令：
ShaolanPet.exe --self-test  检查素材和核心逻辑，退出码 0 表示通过。
ShaolanPet.exe --smoke-test 静音验证实际窗口和媒体播放，结束后自动退出。
ShaolanPet.exe --ui-test    启动用于界面验证的诊断模式。

来源与许可
原项目：https://github.com/Ganzhe2028/ShaolanPet
代码许可见 LICENSE；SunPet 参考代码许可见 LICENSE-SunPet。
复用的好事桌宠 Windows 基础代码许可见 LICENSE-Windows-Base。
图片和音频素材版权归原权利人所有，不属于上述 MIT 代码许可范围。
