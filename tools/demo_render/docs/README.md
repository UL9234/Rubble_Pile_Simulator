# demo_render —— RubbleSim 无头渲染工具

**职责边界**：本目录只负责「把模拟器渲染成视频」。
坍塌场景本身（碎片数量、层数、生成区、取向、受害者摆放）属于业务逻辑，实现在
`Assets/MITLL/Scripts/Debris/DebrisSpawner.cs`，通过命令行参数控制；本目录不复制、不重写任何生成逻辑。

```
tools/demo_render/
├── runtime/          # 运行时注入的渲染代码（编译进 Assembly-CSharp）
│   ├── DemoBootstrap.cs    # 仅当命令行含 -demoshot 时注入 DemoDirector
│   ├── DemoDirector.cs     # 机位（静态/环绕/推镜/跟随）、按帧调度、写元数据
│   ├── FrameRecorder.cs    # 相机 → RenderTexture → PNG 序列
│   └── DemoArgs.cs         # 独立的 -demo* 参数解析（与项目 CustomArgs 不冲突）
├── Editor/           # 仅编辑器（文件夹名必须是 Editor，Unity 的约定）
│   └── DemoBuild.cs        # -executeMethod DemoBuild.PerformBuild 构建 Linux 播放器
├── scripts/
│   ├── sync_scripts.sh     # 把本目录暴露给 Unity（Assets/DemoRender 软链接）
│   ├── build_player.sh     # 构建 Builds/Linux/RubbleSim.x86_64
│   └── render_shots.sh     # 渲染 5 seeds × 2 机位 = 10 段视频
├── docs/             # 本文档
├── .logs/            # 构建/渲染日志（点号开头 → Unity 忽略，见 §5）
└── .work/            # 逐帧 PNG 与 shot.json/player.log 中间产物
```

资产预处理不在这里，而在 **`tools/model_prep/`**（`prepare_human_mesh.py`：把人形网格裁成 body 组并把 A 姿势手臂改成贴身姿态），
与"渲染"职责分开。

> Unity 只编译 `Assets/` 下的 C#，所以 `sync_scripts.sh` 建立软链接
> `Assets/DemoRender -> ../tools/demo_render`（实测 Unity 2022 能正常跟随并写入 `.meta`）。
> C# 源码只存在于本目录，`Assets/` 下只有一个链接和它的 `.meta`。

---

## 1. 工作流

```bash
cd tools/demo_render/scripts

./sync_scripts.sh symlink     # 首次，或改动了脚本文件名后
./build_player.sh             # 构建 Linux 播放器（Xvfb + Vulkan）

./render_shots.sh                        # 全部 10 段（会自动清空输出目录里的旧视频）
./render_shots.sh seed101_high           # 只渲染其中某几段
SEEDS="7 8 9" ./render_shots.sh          # 换种子
HIGH_DUR=16 ORBIT_DUR=24 ./render_shots.sh
KEEP_FRAMES=1 ./render_shots.sh          # 保留逐帧 PNG
```

输出：`/data1/chh/dataset/rubble_dataset/demo/seed<NN>_{high,orbit}.mp4`（1280×720、30 fps、H.264 CRF 16）。
**不生成画廊页与 README**，直接看视频即可；每次渲染会先清空输出目录，避免历次视频堆积占用空间。

首次在别的机器/新克隆上使用，还需要一次性把受害者模型接到生成器上（会改 `Managers.prefab` 的序列化引用，属于项目自身的接线）：

```bash
unity2022 -batchmode -nographics -quit -projectPath <项目根> -executeMethod VictimSetupEditor.Wire
# 或在 Unity 菜单：RubbleSim ▸ Wire Victim Into Debris Spawner
```

---

## 2. 无头渲染是怎么做到的

| 问题 | 方案 |
|---|---|
| 没有显示器 | `Xvfb :99` 虚拟 X server（脚本自动拉起） |
| 没有 GPU 窗口 | 装 `libvulkan1` 后 `-force-vulkan`，实测选中 **NVIDIA A800**；否则退回 Mesa llvmpipe 软件渲染 |
| 带窗口会卡死 | 裸 Xvfb（无窗口管理器）下 Vulkan present 会在第一帧后死锁 → **必须 `-batchmode`**，GPU 设备仍正常创建、离屏渲染正常 |
| 渲染慢于实时 | **帧级离线渲染**：`Time.captureFramerate=fps` 固定每帧虚拟时间，逐帧 `Camera.Render()` 到 RenderTexture 并写 PNG；渲染再慢导出的 30fps 视频也完全平滑且可复现 |
| 场景 UI / ROS | Screen-Space-Overlay UI 不进 RenderTexture；ROS 组件（`RosSensorOutput`/`RosPs5`）录制时自动禁用，无需 roscore |

---

## 3. 本工具自己的参数（`-demo*`）

| 参数 | 默认 | 说明 |
|---|---|---|
| `-demoshot <name>` | — | **必需**，不传则工具完全不介入 |
| `-demopreset <p>` | 由镜头名推断 | `overview` `aerial` `low` `closeup` `orbit` `approach` `fpv` `sensor` `follow` |
| `-demoout/-demowork <dir>` | — | 成片目录 / 中间产物目录 |
| `-demofps <n>` `-demowidth/-demoheight` | 30 / 1280×720 | 录制帧率与分辨率（与窗口无关） |
| `-demostart <sec>` `-demoduration <sec>` | 0 / 20 | 从第几秒虚拟时间开始录、录多久 |
| `-demotimescale <f>` | 不干预 | 每帧强制 `Time.timeScale` |
| `-demolook x,y,z` `-demoradius <r>` | 自动取景 | 手动指定取景中心/半径（本渲染用它保证可复现） |
| `-demoazimuth/-demoelevation/-demodist/-demofov` | 预设 | 方位角/仰角/距离倍数/垂直 FOV |
| `-demoorbitspeed <deg/s>` | 15 | `orbit` 的角速度（12°/s × 30 s = 整圈） |
| `-demodollyfrom/-demodollyto` | 2.0 / 1.1 | `approach` 推镜起止距离倍数 |
| `-demoride main\|rgb\|depth\|robot` `-demoridefovscale <s>` | main / 1 | 跟随哪个相机，以及 FOV 缩放 |
| `-demoteleport 0\|1` `-demodrive ...` | 0 / none | 把机器人投放到堆积体顶部并程序化驱动（第一视角镜头） |
| `-demorosoff 0\|1` `-demomaincamoff 0\|1` `-demosensorcamsoff 0\|1` | 1 | 运行时禁用 ROS 与多余相机 |
| `-demortssrgb 0\|1` | 1 | PNG 用 sRGB RT 读取（画面偏暗/偏灰时切 0 对比） |
| `-demoquit 0\|1` `-demoprog <n>` `-demodiag 0\|1` | 1 / 30 / 0 | 录完退出 / 进度日志间隔 / 场景诊断 |

---

## 4. 坍塌场景参数（属于模拟器，见 `DebrisSpawner.cs`）

`-pancake 1` 打开煎饼式坍塌：板状碎块近水平下落、受害者平躺在生成区边界上。

| 参数 | 默认 | 说明 |
|---|---|---|
| `-pancake 0\|1` | 0 | 打开该场景 |
| `-numlayers <n>` | 3 | 层数（复用模拟器原有参数） |
| `-numobjs <n>` | 300 | 每层块数（复用） |
| `-spawnboundx/-spawnboundz <m>` | 10 | 生成区尺寸（复用；本渲染用 3.0×3.0） |
| `-spawnposy <m>` / `-spawnboundy <m>` | 15 / 10 | 下落高度与生成高度带（复用；本渲染用 3.0 / 1.6） |
| `-pancaketilt <deg>` | 20 | 水平取向的最大随机扰动角（pitch/roll），yaw 自由 |
| `-pancakescalemin/-max` | 0.8 / 2.2 | 单块等比缩放范围（本轮渲染用 1.6 / 4.4，见 §4 末） |
| `-pancakelayergap <s>` | 4 | 层间隔 |
| `-pancakespawndelay <s>` | 0.06 | 层内逐块间隔（避免同帧重叠爆飞） |
| `-pancakecatchfloor 0\|1` | 1 | 加一层 y=0 的隐形物理地板（见 §5） |
| `-pancakevictim 0\|1` | 1 | 是否放受害者 |
| `-pancakevictimedge 0\|1\|2\|3` | 0 | 边界：0=+x, 1=−x, 2=+z, 3=−z（脚本参数只支持数值） |
| `-pancakevictimheight <m>` | 1.7 | 身高，模型按包围盒自动缩放 |
| `-pancakevictimyawspread <deg>` | 30 | 朝向相对「边界外法线」的随机范围：**头朝外、脚朝内 ± 该角度** |
| `-pancakevictimoffset <m>` | 0 | 沿外法线平移（默认身体中心正好压在边界线上） |
| `-randomseed <int>` | 0(随机) | 5 个种子 → 5 个不同场景，堆积体完全可复现 |

受害者模型：`Assets/MITLL/Models/Human/human-neutral.obj` —— **MakeHuman 基础网格**（写实、标准比例、
无性别特征），资产部分 **CC0 1.0**，许可证与出处（`LICENSE-CC0-MakeHuman.txt`、`SOURCE.md`）与该资产同目录。
上游 `base.obj` 的编辑辅助/关节标记几何（含 `helper-genital`）已剔除，只保留 `body` 组并重映射顶点
（19158→13380 顶点）；**并且把 A 姿势的手臂改成了贴身姿态**——上游手臂既外张又前伸（手在 Z=+2.7 dm），
仰卧时手会举在空中、被物理当成支撑把废墟顶起来，现在按测得的肩→手方向整条手臂刚性旋转到沿身体向下、
略外张 8°、与背平面齐平（手从 (±4.96,1.24,+2.68) 变为 (±2.80,0.54,−0.82)，手臂最低点 −1.03 与躯干背侧 −1.02 齐平）。
这些处理都在 `tools/model_prep/prepare_human_mesh.py` 里可复现。换模型：把文件放进同一目录，改 `VictimSetupEditor.ModelPath` 后重跑 `VictimSetupEditor.Wire`
（该脚本可反复执行，会就地更新材质与 prefab 引用）。

> 尺寸演进：初版 0.8/2.2 → 翻倍 1.6/4.4 → **本轮为翻倍值的 80%，即 `PANC_SCALE_MIN/MAX` = 1.28/3.52**，
> 生成区曾试过 2.6×2.6、4.0×4.0，**当前取 3.0×3.0**。
> 想对比其它档位：`PANC_SCALE_MIN=1.6 PANC_SCALE_MAX=4.4 ./render_shots.sh`。

---

## 5. 两个踩过的坑

* **日志目录必须点号开头**：本目录经软链接暴露给 Unity，若日志放在 `logs/`，构建过程写 `build.log` 会被资源数据库
  反复重新导入 → `An infinite import loop has been detected`，**真实资产会静默导入失败**（曾导致构建出的播放器里没有模型）。
  现在日志固定在 `.logs/`、中间产物在 `.work/`（Unity 忽略点号开头的路径）。
* **必须加物理地板 + CCD**：项目 `GroundPlane` 的碰撞体厚度≈0，快速板子会直接穿地，随后被 `FreezeDebris` 的
  `y < -0.5` 判据剔除（实测 60 块丢 23 块）。现在 `-pancake 1` 时会在 y=0 加一层 60×60×2 m 隐形地板，并把每块碎片的
  `collisionDetectionMode` 设为 `ContinuousDynamic`，60 块全部保住（`player.log` 里可见 `frozen 60 pieces (0 discarded...)`）。

---

## 6. 其它

* 未接 ROS：`-demorosoff 0` 时会尝试连 `127.0.0.1:10000`，没有 roscore 只报错刷日志，不影响画面。
* 取景不理想时用 `-demolook/-demoradius/-demodist/-demofov` 微调即可，无需改代码。
* 每段镜头会在 `.work/<shot>/shot.json` 留下该次的取景/参数快照，`player.log` 里留有 `[DebrisSpawner]`、`[DemoDirector]` 的运行记录。
