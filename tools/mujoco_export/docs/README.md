# Unity 废墟场景 → MuJoCo 导出器

本工具导出当前生成场景的物理快照，提供可直接加载的 MJCF、模型资源和状态恢复接口。
不包含奖励、策略、Gym 环境或训练代码。使用本机已有 `mujoco_sim` conda 环境，无需桌面或显示器。

## 精简训练场景与 MuJoCo 环绕录像

默认顺序生成 20 个通过沉降检查的场景，再从中等间隔选取 5 个，在 MuJoCo 中离屏录像：

```bash
bash tools/mujoco_export/scripts/run_training_batch.sh
```

两阶段也可独立运行，以便传入不同参数：

```bash
bash tools/mujoco_export/scripts/generate_training_scenes.sh \
  --output-root /data1/chh/dataset/rubble_dataset/demo/mujoco_train \
  --count 20 --seed-start 1000 --layers 2 --gpus 0 1 2 3 4 5 --workers 6 \
  --settle-timeout 15 --linear-threshold 0.02 --angular-threshold 0.05 --dwell 1
bash tools/mujoco_export/scripts/record_training_orbits.sh \
  --scene-root /data1/chh/dataset/rubble_dataset/demo/mujoco_train \
  --output-root /data1/chh/dataset/rubble_dataset/demo/mujoco_train_videos \
  --count 5 --gpus 0 1 2 3 4
```

可用 `--seeds 101 505 ...` 指定初始种子；该参数会以种子数确定场景数。
也可调 `--spawn-size X Z`、`--cells MIN MAX`、`--density`、`--layer-gap`、
`--terrain-step`、`--terrain-amplitude`、`--terrain-frequency`、`--padding`、
`--timestep` 和 `--validation-steps`。失败种子会记录在 `generation_report.json`，
并用新的唯一种子重试，默认每个目标场景最多 4 次。
录像可调 `--seeds`、`--fps`、`--duration`、`--width`、`--height`、
`--orbit-speed`、`--impulse`、`--effect-at`。

`mujoco_train/seed<seed>/` 只包含 `scene.xml`、所需 `assets/` 和 `metadata.json`。
模型直接以已沉降位姿和零速度起步，运行时用标准 `mujoco.mj_step`；
没有额外的阻尼回调、机器人、动作器或训练代码。质量、质心、惯量和碰撞几何在 XML 中；
生成密度、用于求质量的体积、Unity 专有的物理参数及验证摘要在元数据中。
不把 `scene.mjb`、原始大快照、录像帧或日志复制到训练场景目录。
批量索引和单场景平均耗时记录在 `mujoco_train/index.jsonl` 与 `generation_report.json`。
录像和元数据写到相邻的 `mujoco_train_videos/`。

最后一块板释放后，Unity 最多再模拟 15 秒（默认值），要求所有动态刚体的线速度
和角速度连续 1 秒小于所配阈值。超时或发现无效刚体则放弃该次种子；
完整 MuJoCo 物理步进与 EGL 离屏渲染验证通过后才发布场景包。
录像从静止场景起步，1 秒后只在录像进程中给两块高处板片初速度，让碰撞和坍落可见；
这不会改动训练场景。Unity/PhysX 的沉降运行在 CPU 上，六个并行 worker 分配 GPU 0–5
进行各自的 MuJoCo EGL 离屏验证；录像同样按 GPU 分配。

## 快速使用

在项目根目录运行：

```bash
bash tools/mujoco_export/scripts/export_scene.sh --seeds 101 505 909 --render
```

默认先构建包含导出模块的 Unity Linux 播放器，再依次生成、导出和验证。
`--render` 额外执行 EGL 离屏渲染验证，可以省略；Unity 生成始终使用 `-batchmode -nographics`。
模型验证不启动 GUI。首次 Unity 构建复用现有构建脚本的 Xvfb，但导出和 MuJoCo 验证不依赖 X 显示。

默认输出：

```
/data1/chh/dataset/rubble_dataset/demo/mujoco_seed101/
/data1/chh/dataset/rubble_dataset/demo/mujoco_seed505/
/data1/chh/dataset/rubble_dataset/demo/mujoco_seed909/
```

已有视频不会修改。若同名导出目录存在，工具拒绝覆盖；可指定新的 `--output-root`。
只有验证通过后才将临时目录原子重命名为目标目录；失败时保留隐藏临时目录供诊断。

| 参数 | 默认 | 用途 |
|---|---|---|
| `--seeds` | 101 | 一个或多个生成种子 |
| `--output-root` | 上述 demo 目录 | 每个种子一个自包含子目录 |
| `--padding` | 2 | 地形相对生成区的外扩量，单位 m |
| `--spawn-size X Z` | 3.5 3.5 | 废墟生成区尺寸 |
| `--layers` | 2 | 楼板层数 |
| `--timestep` | 0.002 | MuJoCo 积分步长，单位 s |
| `--steps` | 1000 | 验证中实际唤醒刚体并运行的步数 |
| `--render` | 关闭 | EGL 离屏渲染检查并输出 preview.ppm |
| `--no-build` | 关闭 | 复用已经包含本导出器的播放器 |
| `--unity-args ...` | 空 | 后续全部参数原样传给 Unity，可覆盖场景生成设置 |

Python 默认使用 `/data1/chh/dependency/miniconda3/envs/mujoco_sim/bin/python`。
可通过 `MUJOCO_PYTHON=/path/to/python` 更换。依赖只有 **MuJoCo >= 3.13 和 NumPy**；
3.13 用于验证并恢复原始休眠状态，不另装 conda 环境。默认 `MUJOCO_GL=egl`。

## 文件内容

| 文件 | 内容 |
|---|---|
| `scene.xml` | 可编辑 MJCF，使用相对资源路径，包含 `unity_snapshot` 初始关键帧 |
| `assets/*.obj` | 各碎片、钢筋、假人等显示与凸碰撞网格 |
| `scene.mjb` | 本机 MuJoCo 版本编译后的二进制模型；换版本时优先重新加载 XML |
| `unity_snapshot.json` | Unity 原始场景、刚体、碰撞体、几何、相机及组件清单 |
| `initial_state.npz` | 初始 qpos、qvel、time、mocap 位置与四元数 |
| `scene_loader.py` | 加载、重置、物理阻尼适配；无训练代码 |
| `manifest.json` | 原始元素 → MuJoCo 元素映射、实际地形范围、转换说明 |
| `terrain_reference.npz` | 裁剪前后地形网格及裁剪边界，用于逐面核对 |
| `validation.json` | 数值状态对照、碰撞、重置、1000 步运行与渲染验证结果 |
| `unity_player.log` / `unity_stdout.log` | 本次 Unity 生成和快照日志 |
| `preview.ppm` | 可选的 MuJoCo 离屏渲染图，未调用图形桌面 |

Unity 原始快照中保留质量、重心、主惯量、惯量坐标系、位姿、缩放、线速度、角速度、
线性/角阻尼、静态/运动学/动态属性、休眠状态与阈值、重力开关、碰撞开关、约束标志、
速度上限、动态刚体累计力/力矩（运动学刚体对此不适用）、求解器设置、碰撞层、碰撞形状、触发器、摩擦、弹性与组合模式等。
所有层级节点，包括非物理的 UI、粒子和组件类型，也保留在原始清单中。

## 快照时机与物理语义

原有 `DebrisSpawner.FreezeDebris()` 会删除 Rigidbody 并静态合批。
因此核心模拟器仅新增 `BeforeFreeze` 事件和只读生成边界属性，导出器在删除刚体前同步保存。
未传入 `-mjexport` 时工具不激活，不改变原有生成和录制流程。

- 碎片保留独立动态自由刚体，钢筋的复合碰撞体仍属于各自碎片，不会被合成一个场景大网格。
- 没有 Rigidbody 的假人与地形保持静态，不臆造质量或可变形身体。
- 原工程主场景挂有用于其他模式的 `VineRobot` 预制体。使用 `-mjexport` 时，
  导出器在废墟生成前从运行中的场景移除它；机器人刚体、碰撞体和相机均不进入环境快照或 MuJoCo 模型。
  源场景文件不因此改变。导出脚本会检查快照中没有机器人和运动学刚体。
- 坐标从 Unity `(x,y,z)` 转为 MuJoCo `(x,z,y)`；角速度作为轴向量转换为 `(-x,-z,-y)`。
- 自由关节的平移速度是物体原点速度；由 Unity 重心速度减去 `ω × 重心偏移` 得到。
  角速度转换到 MuJoCo 所要求的局部坐标系。不能直接复制 Unity 的六维速度数组。
- 惯量直接以原始主惯量及其坐标系表达，避免由网格体积重新估算，也避免重复对角化引入误差。
- 用初始关键帧恢复时间和速度；只调用 `MjData(model)` 不会恢复非零初始速度。
- 原始休眠的动态刚体使用 MuJoCo `sleep="init"`，开启休眠并显式使用稀疏 Jacobian。
  验证时先检查休眠恢复，再关闭休眠执行真实物理步进，防止“物体一直不动”掩盖碰撞错误。

## 地形和凹网格

地形默认裁到 **生成区边界外扩 2 m**。对于 3.5×3.5 m 的生成区，通常为 7.5×7.5 m。
直接裁剪原网格的三角面，边界新增顶点沿原三角平面插值，**不重新采样噪声**，也不改变坡面。

某些随机坍塌会将碎片抛到范围外。为保留全部碎片及其支撑地面，工具会将裁剪范围扩大到
动态碰撞体的完整水平包围范围，再留 0.25 m 余量。原始请求范围和实际范围均记录到 manifest；
不会为了满足固定尺寸而丢弃碎片或让其失去地面。实际范围超出源地形覆盖时，边界验证失败。

MuJoCo 普通 mesh geom 按凸包参与碰撞，直接放入完整地形会填平谷地。
因此地形和静态假人的非凸 MeshCollider 用 **所有顶点绑定到同一 body 的刚性 flex** 表示，
保留全部三角面和凹形轮廓；不会引入软体自由度。薄面接触壳半径默认 0.00001 m（10 μm）。
原来 `convex=true` 的碎片仍使用凸 mesh geom；钢筋胶囊、球和盒碰撞体直接转换。

## 加载、恢复与再次验证

在导出目录中：

```python
import scene_loader
model, data, drag = scene_loader.load()
scene_loader.step(model, data, drag)  # 物理步进，包含原始线性/角阻尼适配
scene_loader.reset(model, data)      # 恢复完整初始关键帧
```

也可直接加载 MJCF：

```python
import mujoco
model = mujoco.MjModel.from_xml_path("scene.xml")
data = mujoco.MjData(model)
mujoco.mj_resetDataKeyframe(model, data, model.key("unity_snapshot").id)
mujoco.mj_forward(model, data)
```

直接 `mj_step` 可用，但不会执行 `scene_loader.step` 的原始阻尼适配。
适配器在质心施加线性阻尼力、按世界惯量施加角阻尼力矩，保留调用方已有外力。
这属于物理加载支持，不是控制器或训练代码。

从项目根目录独立运行：

```bash
/data1/chh/dependency/miniconda3/envs/mujoco_sim/bin/python \
  -m unittest discover -s tools/mujoco_export/tests -v

MUJOCO_GL=egl /data1/chh/dependency/miniconda3/envs/mujoco_sim/bin/python \
  tools/mujoco_export/scripts/validate_scene.py \
  /data1/chh/dataset/rubble_dataset/demo/mujoco_seed101 --steps 1000 --render
```

验证包括：逐刚体质量/世界重心/完整惯量张量/世界位姿/重心速度/角速度/休眠状态，
所有活动碰撞体和可见网格的覆盖性，胶囊轴向与尺寸，网格世界顶点，全部地形裁剪三角面射线，
XML/二进制/NPZ/关键帧重置一致性，斜坡接触探针，数值稳定性、地下掉落及 EGL 渲染。
回归测试另覆盖非零速度和偏置重心、触发器、停用碰撞体、散落碎片地面扩展与不支持项拒绝。

## 跨引擎边界（不会隐藏丢失）

原始物理参数完整保存，不表示两个物理引擎能逐步产生相同轨迹：

- MuJoCo 的软接触求解器与 PhysX 不同。使用动态摩擦作为滑动摩擦，不能同时精确复现
  PhysX 独立的静/动摩擦和组合规则；弹性、接触偏移、CCD、求解器缓存、睡眠阈值和速度上限
  等在原始快照中保留，并在 manifest 中标明。未来外力/碰撞事件和 C# 控制逻辑不属于状态快照。
- 非零累计力/力矩、轴锁定、逐碰撞体忽略对、刚体或碰撞体层覆盖、Joint、ArticulationBody、
  未支持的碰撞体或活动蒙皮网格会明确报错，拒绝输出“验证通过”的有损模型。
- Unity 材质的基色被导出。复杂着色器、纹理、灯光效果、粒子、ROS 和 UI 不转成 MuJoCo 组件；
  原始场景清单保留它们的存在和层级。离屏预览用于检查模型和位置，不保证与 Unity 渲染相同。
- 刚性三角 flex 在原生 MuJoCo 中已验证，但不承诺所有 MJX/GPU 后端都支持相同模型元素。
  当前交付目标是本机原生 MuJoCo 强化学习仿真可加载的场景格式。

参考：[MuJoCo 刚性 flex](https://mujoco.readthedocs.io/en/stable/XMLreference.html#deformable-flex)、
[模型与非凸碰撞](https://mujoco.readthedocs.io/en/stable/modeling.html#deformable-objects)、
[MJCF 物理和状态字段](https://mujoco.readthedocs.io/en/stable/XMLreference.html)。

## 工具结构

```
tools/mujoco_export/
  runtime/MujocoSnapshot.cs       Unity 状态抓取（Assets/MujocoExport 软链接）
  scripts/export_scene.sh        本机 conda 环境入口
  scripts/export_scene.py        构建、生成、转换、验证、原子发布
  scripts/convert_scene.py       MJCF/网格/状态文件转换
  scripts/scene_loader.py        可随数据搬移的加载器
  scripts/validate_scene.py      无头源状态对照和物理验证
  tests/test_exporter.py         回归测试
  .work/                        中间结果（忽略）
  .logs/                        构建和导出日志（忽略）
```

## 本次完整验证（2026-09-29）

Unity 2022.3.62f2 构建成功。MuJoCo 3.13.0，10 项回归测试通过。

| 种子 | 动态碎片 | 活动碰撞体 | 地形尺寸（m） | 原三角面射线核对 | 物理步进 |
|---|---:|---:|---|---:|---|
| 101 | 21 | 280 | 7.5 × 7.5 | 44 | 1000 步通过 |
| 505 | 22 | 303 | 7.5 × 7.5 | 44 | 1000 步通过 |
| 909 | 19 | 276 | 7.807 × 7.5 | 53 | 1000 步通过 |

三场均通过位姿、重心速度、完整惯量、休眠、碰撞体及显示网格对照、关键帧重置、
XML/二进制/NPZ 加载、斜坡接触和 EGL 离屏渲染检查；求解器警告计数均为 0。
