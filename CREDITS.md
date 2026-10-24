# 来源与署名

本仓库整合三个桌宠的 Windows 移植版，保留原始图片、音频清单和原声。原项目及此次使用的上游提交如下：

| 桌宠 | 原项目 | 上游提交 | 保留的许可证 |
| --- | --- | --- | --- |
| 太对了 · SunPet | [Ganzhe2028/SunPet](https://github.com/Ganzhe2028/SunPet) | [a01d22c](https://github.com/Ganzhe2028/SunPet/commit/a01d22cca22a5362d26bd3c7cecdb88949245f85) | [SunPet/LICENSE](SunPet/LICENSE) |
| 好事桌宠 · 峰哥 | [Ganzhe2028/FengGe-Desktop-Pet](https://github.com/Ganzhe2028/FengGe-Desktop-Pet)，源自 [yuhanfu513-ops/FengGe-Desktop-Pet](https://github.com/yuhanfu513-ops/FengGe-Desktop-Pet) | [aad1a52](https://github.com/Ganzhe2028/FengGe-Desktop-Pet/commit/aad1a5253aa966e7e0be570605f1956a6b678c21) | [GoodThingPet/LICENSE](GoodThingPet/LICENSE)、[LICENSE-SunPet](GoodThingPet/LICENSE-SunPet) |
| 薄肌桌宠 · ShaolanPet | [Ganzhe2028/ShaolanPet](https://github.com/Ganzhe2028/ShaolanPet) | [7a5ce25](https://github.com/Ganzhe2028/ShaolanPet/commit/7a5ce25d6cfcff9d615f2dcd919fe26b3788f98e) | [ShaolanPet/LICENSE](ShaolanPet/LICENSE)、[LICENSE-SunPet](ShaolanPet/LICENSE-SunPet) |

SunPet 和 ShaolanPet 的 Windows 实现复用了好事桌宠 Windows 基础代码，相应原许可分别保留于 [SunPet/LICENSE-Windows-Base](SunPet/LICENSE-Windows-Base) 和 [ShaolanPet/LICENSE-Windows-Base](ShaolanPet/LICENSE-Windows-Base)。

Windows 新增部分包括 C# / WPF 桌面窗口、托盘菜单、设置持久化、本机 v2 联动、PCM 预加载与 WinMM 输出、构建和测试脚本。整合与公开发布维护者为 [wanzhijie1](https://github.com/wanzhijie1)。

## 素材

- SunPet 原声来自原项目注明的 [对话孙宇晨采访](https://www.bilibili.com/video/BV1fcHD6cEw3/)，每句时间码保留在 `SunPet/Assets/clips.json`。
- ShaolanPet 原声来自原项目注明的 [为什么我劝所有男人练薄肌？](https://www.bilibili.com/video/BV1xa4k6vEpp/) 和 [如何练出薄肌？](https://www.bilibili.com/video/BV1httT6LEfy/)，清单保留在 `ShaolanPet/Assets/clips.json`。
- 好事桌宠的人物图片和 11 段原声沿用原仓库，清单保留在 `GoodThingPet/Assets/clips.json`。
- 原始音频没有被覆盖；SunPet 的 PCM 文件是供 Windows 播放的格式转换产物，转换清单和哈希在 `SunPet/Assets/PCM/manifest.json`。

图片、头像和音频不属于各项目的 MIT 代码许可范围，权利仍归原权利人所有。此仓库的代码许可不授予额外的素材使用权。

## 诊断工具参考

可选音频输出诊断依据微软官方 [Application loopback audio capture](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/) 的进程树捕获 API。仅包含诊断源码，不包含本机录音、进程信息或原始运行日志。
