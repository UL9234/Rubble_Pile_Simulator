# demo_render —— RubbleSim 无头渲染录制工具

本目录是**独立于模拟器业务逻辑**的 demo 录制工具。它不改动 `Assets/MITLL/**` 下的任何场景、预制体或脚本，
所有行为都在运行时通过 `[RuntimeInitializeOnLoadMethod]` 注入，因此可以随时整体删除（`./sync_scripts.sh clean`）而不影响项目本身。

目标：在无头服务器（无显示器）上把 RubbleSim 的 demo 效果录成 720p 视频，供人观看。

---

## 1. 无头渲染是怎么做到的

| 问题 | 方案 |
|---|---|
| 没有显示器 | `Xvfb :99` 虚拟 X server（脚本自动拉起） |
| 没有 GPU？ | 装了 `libvulkan1` 后 Unity 在 Xvfb 下可选用 **NVIDIA Vulkan**（`-force-vulkan`），实测选中 A800；否则会退回 Mesa llvmpipe 软件渲染（慢但能出图） |
| 需要人操控相机 | `DemoDirector` 用命令行参数自动布机位：静态、环绕、推镜、机器人第一视角 |
| 渲染速度 < 实时导致视频卡顿 | **帧级离线渲染**：`Time.captureFramerate = fps` 把每帧的虚拟时间固定为 `1/fps`，播放器以最快速度跑帧，每帧渲染进 RenderTexture 并写 PNG。渲染再慢，导出的 30fps 视频也是完全平滑且可复现的 |
| 场景里的 UI / ROS | Screen-Space-Overlay UI 不会进入 RenderTexture（所以视频干净无 UI）；ROS 脚本（`RosSensorOutput`/`RosPs5`）默认在运行时禁用，无需 roscore |

---

## 2. 目录内容

| 文件 | 作用 |
|---|---|
| `DemoBootstrap.cs` | 运行时注入入口；仅当命令行含 `-demoshot` 时才创建 `DemoDirector`（平时完全惰性） |
| `DemoDirector.cs` | 核心：解析参数、隐藏 ROS/传感器相机、构建录制相机、运镜、驱动机器人、按帧调度与写元数据 |
| `FrameRecorder.cs` | 相机 → RenderTexture → PNG 序列 |
| `DemoArgs.cs` | 独立命令行解析（不与项目自身的 `CustomArgs` 冲突） |
| `Editor/DemoBuild.cs` | 仅编辑器：`-executeMethod DemoBuild.PerformBuild` 构建 Linux 播放器 |
| `sync_scripts.sh` | 把本目录暴露给 Unity（默认 `Assets/DemoRender` 软链接，可 `copy`/`clean`） |
| `build_player.sh` | 构建 `Builds/Linux/RubbleSim.x86_64` |
| `render_shots.sh` | 批量渲染镜头 → PNG → mp4 + 封面/拼图，最后刷新画廊 |
| `make_gallery.py` | 生成 `index.html` 画廊（含每段的参数表与命令行） |
| `logs/` | 构建日志 |

> Unity 只编译 `Assets/` 下的 C#，所以 `sync_scripts.sh` 会建立
> `Assets/DemoRender -> ../tools/demo_render` 软链接（实测 Unity 2022 可正常跟随并写入 `.meta`）。
> 源码唯一存放在本目录，`Assets/` 下只有一个链接和它的 `.meta`。

---

## 3. 用法

```bash
cd tools/demo_render

./sync_scripts.sh symlink     # 首次，或改了脚本文件名后
./build_player.sh             # 构建 Linux 播放器（Xvfb + Vulkan）

./render_shots.sh                                  # 渲染全部镜头
./render_shots.sh 03_orbit_settled 05_fpv_robot    # 只渲染指定镜头
KEEP_FRAMES=1 ./render_shots.sh 01_overview_accum  # 保留 PNG 序列
SETTLE=22 ./render_shots.sh                        # 调整"堆积完成"时刻

python3 make_gallery.py --dir /data1/chh/dataset/rubble_dataset/demo
python3 -m http.server 8099 --directory /data1/chh/dataset/rubble_dataset/demo
```

输出：

```
/data1/chh/dataset/rubble_dataset/demo/
├── index.html                 # 浏览器画廊
├── 01_overview_accum.mp4      # 720p 成片
├── ...
├── thumbs/                    # 封面 + contact sheet
└── _frames/<shot>/            # 每帧 PNG（编码后默认删除）+ player.log + shot.json
```

---

## 4. 命令行参数

模拟器自身的参数（`numlayers`、`numobjs`、`spawnbound*`、`spawnpos*`、`randomseed`、`fogdensity`、
`fogintensity`、`lighttype`、`lightintensity`、`setlightrot`、`lightrot*`、`setlightpos`、`lightpos*`、
`exportstl`）与下面的录制参数写在同一行，各解析各的。

| 参数 | 默认 | 说明 |
|---|---|---|
| `-demoshot <name>` | — | **必需**，镜头名；不传则工具完全不介入 |
| `-demopreset <p>` | 由镜头名推断 | `overview` `aerial` `low` `closeup` `orbit` `approach` `fpv` `sensor` `follow` |
| `-demoout <dir>` | `<project>/demo_out` | 成片输出目录 |
| `-demowork <dir>` | `<out>/_frames/<shot>` | PNG/日志/元数据目录 |
| `-demofps <n>` | 30 | 录制帧率，同时写入 mp4 |
| `-demowidth/-demoheight` | 1280×720 | 录制分辨率（与窗口无关） |
| `-demostart <sec>` | 0 | 从第几秒虚拟时间开始录（0 = 从堆积开始录） |
| `-demoduration <sec>` | 20 | 录制时长（虚拟秒） |
| `-demotimescale <f>` | 不干预 | 每帧强制 `Time.timeScale`；默认保留项目自己的 10×（堆积加速） |
| `-demopos` | — | 预留的绝对机位覆盖 |
| `-demolook x,y,z` | 自动 | 手动指定看向点（给定时不做自动取景） |
| `-demoradius <r>` | 自动 | 手动指定取景半径 |
| `-demoazimuth/-demoelevation` | 预设 | 相机方位角/仰角（度） |
| `-demodist <m>` | 预设 | 距离 = 取景半径 × m |
| `-demofov <f>` | 预设 | 垂直 FOV（度） |
| `-demoorbitspeed <d>` | 15 | `orbit` 预设的角速度（度/秒） |
| `-demodollyfrom/-demodollyto` | 2.6 / 1.05 | `approach` 推镜的起止距离倍数 |
| `-demoride main\|rgb\|depth\|robot` | 由预设决定 | 跟随哪个物体/相机 |
| `-demoridefovscale <s>` | 1.0 | 跟随相机 FOV 缩放；1:1 传感器在 16:9 画布上取 `0.5625` 可匹配水平视角 |
| `-demoteleport 0\|1` | 0 | 开拍时把机器人投放到堆积体顶部（`VineController.RandomizePosition`） |
| `-demodrive none\|forward\|forwardleft\|forwardright` | none | 程序化驱动机器人，无需手柄 |
| `-demodrivespeed <f>` | 1 | 驱动力度 |
| `-demorosoff 0\|1` | 1 | 禁用 `RosSensorOutput`/`RosPs5` |
| `-demomaincamoff 0\|1` | 1 | 禁用场景主相机（省一次渲染） |
| `-demosensorcamsoff 0\|1` | 1 | 禁用 1024×1024 的 RGB/Depth 传感器相机 |
| `-demortssrgb 0\|1` | 1 | PNG 用 sRGB RT 读取（若成片偏暗/偏灰，切 0 对比） |
| `-demoquit 0\|1` | 1 | 录完自动退出播放器 |
| `-demodiag 0\|1` | 0 | 打印场景物体诊断信息 |

---

## 5. 取景与时间线约定

* **自动取景**：开拍瞬间遍历场景中的 `Rigidbody`（排除机器人）求渲染包围盒，取中心与 `extents.magnitude` 作为取景中心/半径；
  若此时碎块还没生成（宏观镜头从 t=0 开始），则改用生成体参数（`spawnpos*`/`spawnbound*`）推算。
* **时间线**：`DebrisSpawner` 的堆积过程 = `1 + numlayers` 波，每波间隔 `spawnDelay`（默认 5），
  默认 `numlayers=3` → 4 波 ≈ 20 虚拟秒。因此默认 `SETTLE=22`：`-demostart 0` 录堆积过程，
  `-demostart 22` 录堆积完成后的环绕/近景/机器人镜头。
* **机器人**：场景中 `VineRobot` 初始位于 `(0, 1, -10)`，面朝原点；堆积完成后 `VineController.Initialize()`
  解除 kinematic，机器人落到地面/堆积体上；`-demoteleport 1` 会先把它投放到堆积体顶部再向前推进。

---

## 6. 已知限制

* Screen-Space-Overlay UI（加载条、按键说明）不会出现在成片里——相机渲染到 RenderTexture 时不含 Overlay UI。
* 未接 ROS：`-demorosoff 0` 时会尝试连 `127.0.0.1:10000`，没有 roscore 就只是报错刷日志，不影响画面。
* 自动取景依赖碎块包围盒；若某镜头取景不理想，用 `-demolook/-demoradius/-demodist/-demofov` 手动微调即可，无需改代码。
