# Alan-Desktop-Pets-Windows

邵艾伦、太对了和峰哥的 Windows 桌宠合集，可单独使用，也能自动接话。

| 邵艾伦 | 太对了 · SunPet | 峰哥 · 好事桌宠 |
| :---: | :---: | :---: |
| <img src="ShaolanPet/Assets/face.png" width="120" alt="邵艾伦头像"> | <img src="SunPet/Assets/face.png" width="120" alt="太对了头像"> | <img src="GoodThingPet/Assets/face.png" width="120" alt="峰哥头像"> |
| 37 句，默认启用 8 句 | 28 句原声 | 11 句原声 |

## 下载与运行

在 [最新 Release](https://github.com/wanzhijie1/Alan-Desktop-Pets-Windows/releases/latest) 下载 `Alan-Desktop-Pets-Windows-*.zip` 运行包（不带 `-source`），完整解压后双击 `启动三宠.cmd`。也可进入各角色文件夹单独启动 exe。更新前先退出旧版。

需要 Windows 10 / 11 和 .NET Framework 4.8 或更新版本。运行不需要联网，不会添加开机启动。

## 功能与接话

- 点击说话并显示字幕，拖动移动；右键调整音量、大小和动画，也可从托盘退出。
- 邵艾伦和太对了支持随机、固定播放和轮播筛选；邵艾伦默认启用 8 句，可在筛选窗口“全部恢复”开启 37 句。
- 点击邵艾伦：邵艾伦 → 太对了 → 峰哥 → 太对了，第四句后结束。
- 单点峰哥：太对了接一句；单点太对了：只播放自己。
- 预加载 PCM 音频、处理首尾多余静音，让接话更顺滑。忙碌时可能跳过接话；整包更新可保持版本一致。

位置和偏好会自动保存。

## 从源码构建

Windows PowerShell 中执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-all.ps1
.\start-trio.cmd
```

使用系统自带 .NET Framework 编译器，无需安装 Visual Studio 或额外 SDK。各程序输出到对应项目的 `build/windows`。

分项目说明：[邵艾伦](ShaolanPet/Windows/README-Windows.txt) · [太对了](SunPet/Windows/README-Windows.txt) · [峰哥](GoodThingPet/Windows/README-Windows.txt)。

基于 Ganzhe2028 的 [ShaolanPet](https://github.com/Ganzhe2028/ShaolanPet)、[SunPet](https://github.com/Ganzhe2028/SunPet) 和 [FengGe-Desktop-Pet](https://github.com/Ganzhe2028/FengGe-Desktop-Pet)，由 [wanzhijie1](https://github.com/wanzhijie1) 整合维护。原许可证保留在各子目录。
