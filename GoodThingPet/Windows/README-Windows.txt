峰哥 · 好事桌宠 · Windows

运行
需要 Windows 10 / 11 和 .NET Framework 4.8 或更新版本。
完整解压后双击 GoodThingPet.exe，保留同目录的 Assets 和 GoodThingPet.exe.config。
也可用合集根目录的“启动三宠.cmd”一起启动；更新前先退出旧版。

使用
点击人物随机播放一句，显示字幕和动画；再次点击切换，拖动可移动位置。
右键人物或托盘可挑一句、停止播放、调整音量、大小和动画或退出。
预加载 PCM 音频并处理首尾多余静音，使切换和接话更顺滑。

接话
邵艾伦 → 太对了 → 峰哥 → 太对了，第四句后结束。
单点峰哥后太对了接一句；单点太对了只播放自己。忙碌时可能跳过接话。
推荐使用同一新版合集；旧版与 macOS 版不互通。Windows 版不包含机身摇晃。

构建
在 GoodThingPet 项目目录运行：
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\build.ps1
输出：build\windows\GoodThingPet.exe。使用系统自带 .NET Framework 编译器，无需额外 SDK。

设置自动保存在 %LOCALAPPDATA%\GoodThingPet\settings.json。
运行不需要联网，不会添加开机启动。

下载：https://github.com/wanzhijie1/Alan-Desktop-Pets-Windows/releases/latest
原项目：https://github.com/Ganzhe2028/FengGe-Desktop-Pet
原许可证保留于项目目录。
