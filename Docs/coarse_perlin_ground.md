# 稀疏柏林噪声地面

## 工作区结构

- `Assets/MITLL/Scenes/MainScene_OS.unity`：主场景，原来的 `GroundPlane` 是 2500×2500 m 平面，使用近乎零厚度的 BoxCollider。
- `Assets/MITLL/Scripts/Managers`：命令行参数、随机种子、重置与初始化事件。
- `Assets/MITLL/Scripts/Debris`：碎片生成、楼板切割、钢筋、假人摆放及地形。
- `Assets/MITLL/Scripts/Vine`：机器人与 ROS 接口。
- `Assets/MITLL/Models`、`Prefabs`、`Materials`：模型、预制体与材质。
- `tools/demo_render`：构建 Linux 播放器、控制相机、逐帧录制并编码 MP4；通过 `Assets/DemoRender` 软链接被 Unity 编译。
- `Builds`、`Library`：构建产物与 Unity 缓存。

## 地形生成

`CoarsePerlinGround.cs` 在 `DebrisSpawner.Start` 中创建，先于碎片生成和假人摆放执行。
默认在以废墟生成区为中心的 80×80 m 范围内，每 2 m 采样一次二维 Perlin 噪声（41×41 个采样点）。
每个四边形单元拆成两个三角形，总计 3200 个斜面；三角形内部线性连接采样点，不进行细分或曲面平滑。
各三角形使用独立顶点和平面法线，斜面之间的折角保持可见。MeshCollider 使用同一网格。

高度为 `terrainbase + terrainamplitude × (Perlin(x,z) - Perlin(中心))`；振幅是噪声的缩放系数，
不是保证达到的峰谷差。种子沿用 `randomseed`，但使用独立随机数发生器派生噪声偏移，不消耗碎片随机序列。
地形在重置时保留；同一运行中的地面保持一致。

原地面的显示与碰撞同时停用。地形启用时不再创建 y=0 的隐形接地板，避免架空谷地内的碎片。
程序化楼板的生成高度在原参数基础上增加地形最大高度（仅正向增加），确保从地形上方释放。
假人沿所在三角面的法线倾斜，再按身体网格顶点与实际地形的高度差接地。
碎片剔除阈值改为地形最低点以下 0.5 m，避免删掉正常处于负高度谷地中的碎片。

地形是有限面积静态网格；范围外无地面。假人是刚性模型，跨多个斜面时不做身体形变。
采样单元最多 256×256，超过后会扩大实际采样间距以限制网格大小。

| 参数 | 默认值 | 含义 |
|---|---|---|
| `-terrainground` | 1 | 1 启用，0 恢复原平面与接地板 |
| `-terrainsize` | 80 | 地形边长（m），至少覆盖生成区并留 20 m 余量 |
| `-terrainstep` | 2 | 采样间距（m）；增大可降低采样率 |
| `-terrainamplitude` | 2.4 | 高度缩放系数（m） |
| `-terrainfrequency` | 0.12 | 每米的噪声坐标增量，与采样率独立 |
| `-terrainbase` | 0 | 高度基准（m） |

## 外围演示

录制脚本默认外围环绕：距离目标 12 m、仰角 22°，30 秒转一周。默认保留已存在视频。

```bash
bash tools/demo_render/scripts/build_player.sh
SEEDS="101 505 909" NAME_PREFIX="terrain_outer_" NO_CLEAN=1 \
  bash tools/demo_render/scripts/render_shots.sh
```

输出 `/data1/chh/dataset/rubble_dataset/demo/terrain_outer_seed*_orbit.mp4`。
`TERRAIN_STEP`、`TERRAIN_AMPLITUDE`、`TERRAIN_FREQUENCY`、`TERRAIN_SIZE` 可覆盖地形参数；
`ORBIT_RADIUS`、`ORBIT_DISTANCE`（两者乘积为相机距离）、`ORBIT_ELEVATION` 可调整外围取景。
每段的参数、录制信息和运行日志保存在 `tools/demo_render/.work/<片名>/`。

## 本次验证（2026-09-29）

Unity 2022.3.62f2 Linux 播放器构建成功，0 编译错误。三个种子均使用上述默认地形参数；
运行日志确认 1681 个采样点、3200 个三角斜面。

| 种子 | 地形高度范围（m） | 落定碎片数 | 地下剔除数 |
|---|---|---|---|
| 101 | -0.445～1.650 | 21 | 0 |
| 505 | -1.067～1.104 | 22 | 0 |
| 909 | -1.059～1.331 | 19 | 0 |

相机水平环绕半径约 11.13 m，位置高度约 5.30 m，高于上述地形最高点，并处于废墟包围范围之外。
录制包含碎片落下、落定和完整外围环绕。视觉检查使用各段中间帧及不同朝向的画面。

三个成品视频均通过逐帧解码检查：H.264、1280×720、30 fps、900 帧、30 秒。
`-terrainground 0` 的回退路径另以 22 秒低分辨率录制验证，21 块碎片落定、0 块剔除。
