# 三宠 Windows 流畅版

把薄肌、太对了和峰哥放到 Windows 桌面上。点一下说一句，同时运行时还能互相接话。

| 薄肌桌宠 | 太对了 · SunPet | 好事桌宠 · 峰哥 |
| :---: | :---: | :---: |
| <img src="ShaolanPet/Assets/face.png" width="120" alt="薄肌桌宠头像"> | <img src="SunPet/Assets/face.png" width="120" alt="SunPet头像"> | <img src="GoodThingPet/Assets/face.png" width="120" alt="好事桌宠头像"> |
| 37 句，默认启用 8 句 | 28 句原声 | 11 句原声 |

这是三个原项目的 **Windows 移植与联动整合版**。原作者、原仓库与素材来源见 [CREDITS.md](CREDITS.md)。

## 下载和运行

从 [最新 Release](https://github.com/wanzhijie1/Trio-Desktop-Pets-Windows/releases/latest) 下载 `Trio-Desktop-Pets-Windows-v1.0.0.zip`。完整解压后，先退出旧版桌宠，再双击 `启动三宠.cmd`。

也可以单独打开子文件夹中的 `ShaolanPet.exe`、`SunPet.exe` 或 `GoodThingPet.exe`。请保留各程序旁的 `Assets` 文件夹和 `.exe.config` 文件。

运行要求为 Windows 10 / 11、.NET Framework 4.8 或更新版本，以及可用的音频输出设备。Windows 11 build 26200 / .NET Framework 4.8.1 已实测；尚未在 Windows 10 实机、其他声卡或蓝牙输出环境验证。程序为未做发布者数字签名的便携构建。

不需要 Python、Node.js、Visual Studio 或 ffmpeg。运行时不联网，不修改开机启动。

## 怎么用

| 操作 | 效果 |
| --- | --- |
| 单击人物 | 播放一句，显示字幕和动画 |
| 播放中再次单击 | 切换到下一句，旧声音立即停止 |
| 拖动人物 | 移动位置，下次启动会恢复 |
| 右键人物或系统托盘图标 | 选择语句、调整音量和大小、减少动态效果、退出 |
| SunPet / 薄肌的“播放方式” | 随机播放或固定某一句 |
| SunPet / 薄肌的“轮播内容” | 连续勾选参与轮播的语句；固定播放和手动挑选不受影响 |

薄肌首次按原清单启用 8 句。在“轮播内容”中点“全部恢复”可开启全部 37 句。已有保存位置优先，启动器不会强制重新排列。

三只桌宠的设置分别保存在 `%LOCALAPPDATA%\SunPet`、`%LOCALAPPDATA%\GoodThingPet` 和 `%LOCALAPPDATA%\ShaolanPet`。升级无需清空设置。Windows 版不提供机身摇晃触发。

## 接话规则

三只桌宠空闲且开启联动时：

```text
点薄肌：薄肌 → 太对了 → 峰哥 → 太对了，到第四句结束
点峰哥：峰哥 → 太对了，接一句后结束
点太对了：只播放自己
```

手动播放优先，忙时可能跳过接话。停止播放、关闭联动或退出会取消尚未开始的接话。只开薄肌和太对了时，保留一次接话；缺少的环节不会被跳过。

联动使用当前用户、当前 Windows 会话内的 v2 命名管道，不使用网络端口。请使用同一整包里的版本；早期 Windows 双宠 v1 和 macOS 通知协议不互通。

## 本版改了什么

- 移植透明悬浮窗口、系统托盘、拖动、字幕、动画和设置保存。
- 音频在启动时预加载到内存，长期复用 WinMM 输出设备，减少每次打开文件、解码和重开设备造成的停顿。
- SunPet 随包提供 28 条预转换 PCM；原始 `.m4a`、原始 WAV、清单和图片均保留。
- 保守裁去首尾多余静音，并保留弱音保护和短淡入淡出；根据每句活动边界调整接话时机。
- 使用同机单调时钟安排接话，修复停止消息乱序后重新接话的问题，保留四句上限。

整包发布版本为 **v1.0.0**，三个程序各自的版本如下：

| 程序 | 版本 | 源码 |
| --- | --- | --- |
| SunPet | 1.2.0 | [SunPet/Windows](SunPet/Windows) |
| GoodThingPet | 1.3.0 | [GoodThingPet/Windows](GoodThingPet/Windows) |
| ShaolanPet | 1.1.0 | [ShaolanPet/Windows](ShaolanPet/Windows) |

## 从源码构建

在 Windows PowerShell 中进入仓库目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-all.ps1 -SelfTest
.\start-trio.cmd
```

产物分别在三个子目录的 `build/windows/` 中。构建使用 .NET Framework 自带的 C# 编译器，不需要 Visual Studio、额外 .NET SDK 或 NuGet。各桌宠也可以单独执行 `Windows/build.ps1 -SelfTest`。

SunPet 的 `Assets/PCM/` 已包含生成后的 WAV 和校验清单。只有更换音频或重新生成 PCM 时，才需要 Python 与 ffmpeg：

```powershell
python .\SunPet\Windows\prepare_audio.py --ffmpeg "C:\path\to\ffmpeg.exe"
python .\SunPet\Windows\prepare_audio.py --verify-only
```

## 测试范围

```powershell
# 构建、自检、音频引擎测试、桥接测试和五组独立进程联动测试
powershell -NoProfile -ExecutionPolicy Bypass -File .\test.ps1

# 使用已有构建，并逐条通过 Windows 音频输出播放全部素材（静音）
powershell -NoProfile -ExecutionPolicy Bypass -File .\test.ps1 -SkipBuild -MediaSmoke
```

2026-10-24 在上述 Windows 11 环境验证了全部 76 段声音、随机与固定模式、轮播排除、设置读写、连续切换与停止，以及四句链、峰哥起头、SunPet 起头、两宠和途中停止五种联动情况。

还使用仅捕获新启动测试进程树的 WASAPI 回录检查了真实输出，没有采集麦克风或其他应用声音。一组四句样本的波形参考拟合未发现 10ms 级整块掉音；这是一台设备上的样本验证，不代表所有组合或所有设备，也不承诺零延迟。可复现的诊断源码见 [tools/audio-probe](tools/audio-probe/README.txt)，不附本机测试日志或录音。

## 署名和许可

原项目是 [SunPet](https://github.com/Ganzhe2028/SunPet)、[FengGe-Desktop-Pet](https://github.com/Ganzhe2028/FengGe-Desktop-Pet) 和 [ShaolanPet](https://github.com/Ganzhe2028/ShaolanPet)。Windows 整合与发布由 [wanzhijie1](https://github.com/wanzhijie1) 维护，具体来源见 [CREDITS.md](CREDITS.md)。

代码采用 MIT，原许可证和版权声明保留在各子目录。头像、人物图片和语音素材的权利归原权利人所有，**不在 MIT 代码许可范围内**；生成 PCM 或程序图标不会改变原素材的权利归属。
