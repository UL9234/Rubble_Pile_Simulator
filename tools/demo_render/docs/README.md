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

现在默认走**纯程序化生成**（`-procdebris 1`，见 §4.1）：不再使用 prefab 碎片库，也不再对碎片做随机缩放，每块板按真实尺寸生成。`-pancake 1`（不带 procdebris）仍保留旧的"预fab 板 + 近水平取向"模式，用于复现早期版本。

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
| `-debrisdensity <kg/m3>` | 0（关） | **体积质量**：`mass = 密度 × 碰撞体局部体积 × scale³`；0 表示沿用 prefab 里的固定质量；本渲染用 2400（钢筋混凝土） |
| `-randomseed <int>` | 0(随机) | 5 个种子 → 5 个不同场景，堆积体完全可复现 |

### 4.1 程序化碎片生成（`-procdebris 1`）

本工具只负责把场景渲染成视频；**生成算法本身（沃罗诺伊切割、共享边界噪声、放样、崩角、质量与释放时序）
是实现细节，文档见项目级文档** [`Docs/procedural_debris_generation.md`](../../../Docs/procedural_debris_generation.md)，
那里有完整的步骤、参数表与已知限制。

渲染侧只需要知道：

* 场景由 `-procdebris 1` 打开，碎片数量来自生成器（每层 8–11 块，`-proccellsmin/max`），
  不再使用 `-numobjs`，也**没有随机缩放**；
* 层数与平面尺寸复用 `-numlayers` / `-spawnboundx|z`，层间距 `-proclayerspacing`（默认 3 m），
  第 0 层高度取 `-spawnposy`；
* **逐层生成**：下层落定（等 `-pancakelayergap`）后再建上层；层内按到中心距离**由内向外逐个释放**，
  间隔 = 一块板自由落体自身厚度的时间 `sqrt(2t/g)`（200 mm → 0.202 s）。这套策略已写死在
  `DebrisSpawner` 里（A/B 实测：铺开范围从 8–10 m 收敛到约 3.9–4.6 m，见 `Docs/` §5.1）；
* 场景里**没有斥力、没有切缝、没有随机朝向/缩放**：碎片间的分离完全来自裂缝几何；
* **暴露钢筋**（`-rebar 1`，默认开）是纯视觉对象：中面虚拟网格与碎片侧壁的交点按对记录，每对一根钢筋，
  两端埋入混凝土、按最小能量曲线弯曲、两端各自扫掠成短柱；没有刚体/碰撞体，不参与求解。
  相关参数 `-rebargrid`（网格间距）、`-rebarthickness`（半径倍数）；
* `player.log` 里可核对每层统计与冻结后的包围盒：
  `procedural layer 1: 10 slabs, bottom plane y=3.00, cover 12.3/12.3 m2 ... 2 corner cuts; release interval 0.168 s ...`、
  `edge noise 89 mm`（当前渲染的噪声振幅）、
  `debris world bounds after freeze: center=... size=...`。

受害者模型：`Assets/MITLL/Models/Human/human-neutral.obj`

## 5. 两个踩过的坑

* **日志目录必须点号开头**：本目录经软链接暴露给 Unity，若日志放在 `logs/`，构建过程写 `build.log` 会被资源数据库
  反复重新导入 → `An infinite import loop has been detected`，**真实资产会静默导入失败**（曾导致构建出的播放器里没有模型）。
  现在日志固定在 `.logs/`、中间产物在 `.work/`（Unity 忽略点号开头的路径）。
* **质量默认与尺寸无关**：Unity 的 `Rigidbody.mass` 是序列化值，运行时只改 `localScale` 不会重算质量，所以原库里每块都是 1 kg。`-debrisdensity` 打开后按碰撞体体积给质量（本渲染 2400 kg/m³ → 单块 0.1–935 kg，整堆约 15 t），堆积更"压得实"。日志里会打印 `volumetric mass: ... kg total` 便于核对。
* **必须加物理地板 + CCD**：项目 `GroundPlane` 的碰撞体厚度≈0，快速板子会直接穿地，随后被 `FreezeDebris` 的
  `y < -0.5` 判据剔除（实测 60 块丢 23 块）。现在 `-pancake 1` 时会在 y=0 加一层 60×60×2 m 隐形地板，并把每块碎片的
  `collisionDetectionMode` 设为 `ContinuousDynamic`，60 块全部保住（`player.log` 里可见 `frozen 60 pieces (0 discarded...)`）。

---

## 6. 其它

* 未接 ROS：`-demorosoff 0` 时会尝试连 `127.0.0.1:10000`，没有 roscore 只报错刷日志，不影响画面。
* 取景不理想时用 `-demolook/-demoradius/-demodist/-demofov` 微调即可，无需改代码。
* 每段镜头会在 `.work/<shot>/shot.json` 留下该次的取景/参数快照，`player.log` 里留有 `[DebrisSpawner]`、`[DemoDirector]` 的运行记录。
