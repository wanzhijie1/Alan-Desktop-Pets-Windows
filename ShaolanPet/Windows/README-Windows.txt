邵艾伦 · ShaolanPet · Windows

运行
需要 Windows 10 / 11 和 .NET Framework 4.8 或更新版本。
完整解压后双击 ShaolanPet.exe，保留同目录的 Assets 和 ShaolanPet.exe.config。
也可用合集根目录的“启动三宠.cmd”一起启动；更新前先退出旧版。

使用
点击人物说话，再次点击切换；拖动可移动位置。
右键人物或托盘可挑一句、切换随机/固定播放、筛选轮播内容、调整音量和大小或退出。
共 37 句，默认启用 8 句；在筛选窗口点“全部恢复”可开启全部。
取消轮播勾选只影响随机轮播，固定播放、手动挑一句不受影响。
默认 8 句：07 AI 救不了你；16 数字不会骗人；17 我不相信你没有空健身；19 蛋白粉不是科技；
26 过度健身吸引到的是同性；30 薄肌的核心逻辑就是要帅；31 我已经是版本之子了；32 中国人必须得吃饭。
预加载 PCM 音频并处理首尾多余静音，使切换和接话更顺滑。

接话
邵艾伦 → 太对了 → 峰哥 → 太对了，第四句后结束。
单点峰哥后太对了接一句；单点太对了只播放自己。忙碌时可能跳过接话。
推荐使用同一新版合集；旧版与 macOS 版不互通。Windows 版不包含机身摇晃。

构建
在 ShaolanPet 项目目录运行：
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\build.ps1
输出：build\windows\ShaolanPet.exe。使用系统自带 .NET Framework 编译器，无需额外 SDK。

设置自动保存在 %LOCALAPPDATA%\ShaolanPet\settings.json。
运行不需要联网，不会添加开机启动。

下载：https://github.com/wanzhijie1/Alan-Desktop-Pets-Windows/releases/latest
原项目：https://github.com/Ganzhe2028/ShaolanPet
原许可证保留于项目目录。
