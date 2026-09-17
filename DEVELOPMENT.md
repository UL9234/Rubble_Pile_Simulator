# RubbleSim 二次开发指南

> 本文档基于对 `main` 分支（commit `83e8a0b`）的源码通读与资源文件核对，用于指导在 RubbleSim 上做二次开发。
> 所有结论都标注了对应的源码/资源路径，未验证的内容会明确写"未验证"。

---

## 1. 项目本质与技术栈

**这是"随机废墟堆生成 + 机器人在废墟中导航"的仿真器**，不是一个纯算法库。随机性只体现在**废墟堆的摆放/选型**上，机器人侧是连续物理仿真。

| 项 | 值 | 来源 |
|---|---|---|
| Unity | **2022.3.62f2** | `ProjectSettings/ProjectVersion.txt` |
| 渲染管线 | URP 14.0.12，当前 Quality = **High Fidelity**（index 2） | `ProjectSettings/QualitySettings.asset`、`Assets/Settings/URP-HighFidelity.asset` |
| 物理 | fixed timestep 0.02、solver 6/1、gravity -9.81、AutoSyncTransforms=0 | `ProjectSettings/TimeManager.asset`、`DynamicsManager.asset` |
| 输入 | Input System 1.14（`SproutControls.inputactions` → 生成 `SproutControls.cs`） | `Assets/MITLL/Scripts/Vine/` |
| 通信 | ROS-TCP-Connector（Unity 侧）+ ROS Melodic + `ds5_ros` fork（PS5 手柄） | `Packages/manifest.json`、README |
| 代码量 | 20 个 `.cs`，约 2500 行，**没有 asmdef**（全部编译进 `Assembly-CSharp`） | `find Assets -name "*.cs"` |

包依赖里有 3 个 Git URL 包（ROS-TCP-Connector、URDF-Importer、`meryuhi/URPFog#urp14`），
**首次打开工程必须联网**，否则 URP Fog 相关的 `VolumeParams.cs` 会编译失败。
本机目前**没有安装 Unity**（`which unity` 为空），要动手改必须自己装 Hub + 2022.3.62f2。

---

## 2. 代码地图

```
Assets/MITLL/Scripts/
├── Managers/
│   ├── GameManager.cs      48 行  事件中枢：static事件 doReset / doInit
│   ├── CustomArgs.cs      199 行  命令行参数解析（static Dictionary）
│   └── RandomManager.cs    81 行  单例随机源，static/dynamic 两条流
├── Debris/
│   ├── DebrisSpawner.cs   263 行  ★核心：生成废墟堆
│   ├── WeightedItemSO.cs   21 行  单个"预制体+权重"
│   ├── WeightedItemCollectionSO.cs 20 行  加权集合
│   ├── DebrisLibraryScriptableObject.cs 20 行  ← 死代码，无任何引用
│   ├── ItemBuilder.cs     108 行  仅编辑器：CSV → SO 生成器
│   └── Editor/ItemBuilderEditor.cs 31 行  给 ItemBuilder 加一个按钮
├── Vine/                  机器人侧（"Vine"是机器人名）
│   ├── VineController.cs  138 行  力驱动的移动、灯、重置
│   ├── JoyInput.cs        130 行  键鼠/手柄 → VineController
│   ├── RosPs5.cs          120 行  ROS joy 话题 → VineController
│   ├── RosSensorOutput.cs 135 行  ★发布 pos_rot / rgb_cam / depth_cam
│   ├── SproutControls.cs  716 行  自动生成，勿手改
│   ├── LightControls.cs   140 行  场景灯的类型/位置/旋转（CLI 参数）
│   └── UIController.cs     84 行  加载进度条 + 说明面板
├── Camera/AutoFocus.cs     54 行  射线测距 → DoF 对焦
├── Utils/
│   ├── SceneToSTLExporter.cs 112 行  合并废墟 mesh → 导出 STL（真值）
│   └── CSVReader.cs         47 行  ← 死代码，解析完就丢掉
└── Volume/VolumeParams.cs   39 行  URP Fog 密度/强度（CLI 参数）
```

**关键 GameObject（都在 Prefab 里，不在 Scene 内联）**

- `Assets/MITLL/Prefabs/Managers.prefab` — 挂 `GameManager` + `CustomArgs` + `RandomManager` + `DebrisSpawner`
- `Assets/MITLL/Prefabs/VineRobot.prefab` — 挂 `VineController` + `JoyInput` + `RosPs5` + `RosSensorOutput`，
  含 `Rgb Camera`、`Depth Camera`（都在 `rgb.renderTexture` / `depth.renderTexture` 上出图）、`Spot Light`、`Main Camera`
- `Assets/MITLL/Prefabs/ItemBuilder.prefab` — 场景里也放了一份，**仅编辑器用途**

**Scene 根对象**（`Assets/MITLL/Scenes/MainScene_OS.unity`，唯一在 Build Settings 里的场景）
`GroundPlane`、`Directional Light`(+LightControls)、`Global Volume`(+VolumeParams)、`Canvas`(+UIController)、
`Background`、`EventSystem`、`DustMotes`、7×`DustCloudBurst`，以及 4 个 PrefabInstance：
`Managers`、`VineRobot`(位于 0,1,-10)、`ItemBuilder`、`DustCloudBurst`。

---

## 3. 运行流程（这是理解一切的主线）

```
Scene 加载
  └─ CustomArgs.Awake()      解析参数（编辑器下读 DebugArgs.txt，播放器下读命令行）
  └─ RandomManager.Awake/OnEnable()   建 seed、建两条 Random 流
  └─ GameManager.Start() → LateStart(0.1s) → Reset()
        └─ GameManager.doReset 事件
             ├─ DebrisSpawner.Reset()  → StartCoroutine(SetUpScene())
             ├─ VineController.Reset() （回到初始位姿、kinematic）
             └─ UIController.Reset()   （进度条动画）
  └─ DebrisSpawner.SetUpScene():
        Time.timeScale = 10                        ← 加速沉降
        GeneratePile(smallDebrisCollection)        ← 第 1 层：小碎块
        WaitForSeconds(spawnDelay)                 ← 默认 5（缩放时间）= 0.5s 真实 ≈ 250 物理步
        重复 numPiles 次 GeneratePile(debrisCollection)
        FreezeDebris()                             ← 删 Rigidbody、置 static、合并批处理、(可选)导出 STL
        Time.timeScale = 1
        GameManager.Initialize() → doInit 事件
             ├─ VineController.Initialize()  （Rigidbody 变为非 kinematic，机器人开始受物理）
             └─ UIController.Initialize()    （隐藏加载界面）
```

数量关系（默认值）：`numToSpawn=300`、`numPiles=3`，加上第 1 层小碎块，**总共产 4 × 300 = 1200 个 debris**。
位置在 `spawnpos(0,15,0)` 为中心、`spawnbound(10,10,10)`（即 **±5**）的立方体内均匀随机采样，
缩放为 `U(1,5)` 的各向同性缩放，欧拉角各轴 `U(-180,180)`。

`FreezeDebris()` 之后废墟堆是**静态烘焙**的：Rigidbody 被 `Destroy`、`isStatic=true`、`StaticBatchingUtility.Combine`。
y < -0.5 的物体直接删除。所以"堆完再改"必须重跑整个流程。

---

## 4. 数据链：如何控制"生成什么"（最重要的扩展面）

```
Prefabs/Debris/*.prefab (17 个)
        ▲ gameObject 引用
ScriptableObjects/Items/{MainDebris,SmallDebris}/*.asset  (WeightedItemSO: prefab + weight)
        ▲ 数组引用
ScriptableObjects/Collections/{MainDebrisPrototype,SmallDebrisPrototype}.asset  (WeightedItemCollectionSO)
        ▲ Inspector 字段
DebrisSpawner.debrisCollection / smallDebrisCollection
```

生成器工具链：`ItemBuilder.cs` + `ItemBuilderEditor.cs`

- 读取 `TextAsset` CSV（`object,weight` 两列），逐行 `AssetDatabase.LoadAssetAtPath<GameObject>($"{prefabPath}/{name}.prefab")`
- 为每个 prefab 生成一个 `WeightedItemSO` 到 `itemsPath`
- 把累积列表写成一个 `WeightedItemCollectionSO` 到 **硬编码路径** `Assets/MITLL/ScriptableObjects/Collections/{collectionName}.asset`

**已知的数据问题（务必先看这一条）**

`MainDebrisPrototype.asset` 里实际有 **34 个条目**，而不是 17 个：

| 位置 | 来源目录 | 权重 |
|---|---|---|
| 1–17 | `Items/SmallDebris` | 全 1（= `SmallDebrisCollection.csv`） |
| 18–34 | `Items/MainDebris` | 1/0.5/2/0.3/1/1/1/1/1/1/10/10/10/10/10（= `WeightedCollection.csv`） |

原因：`ItemBuilder.GenerateFromCSV()` 往 **实例字段** `weightedItems` 里累加且从不清空
（`ItemBuilder.cs:88-94`），在同一个 ItemBuilder 上先跑 SmallDebris、再跑 MainDebris，就得到"小碎块列表 + 主列表"的并集。
配合下面第 5.1 节的**有偏采样**，结果是：**排在前面、权重为 1 的小碎块被大量选中，Chunk_* 的 weight=10 完全没有体现出 10 倍频次。**

同时 `SmallDebrisPrototype` 只有 12 项（Bits/Bricks/ConcreteBlock），缺 `Chunk_01..05`，这个看起来是有意为之。

---

## 5. 已确认的坑与脆弱点（按影响排序）

### 5.1 `Spawn()` 的加权采样不是按权重比例（`DebrisSpawner.cs:187-218`）

```csharp
float weightMax = Σ item.weight;
while (curSpawned < 1) {
    foreach (var item in weightedList.weightedItems)
        if (item.weight > random.GetStaticFloat(0, weightMax))   // 逐个试，先通过者胜
            return Instantiate(item.gameObject, position, rotation);
}
return weightedList.weightedItems[0].gameObject;                  // 兜底
```

每个 item 的通过概率是 `w_i / weightMax`，但**第一个通过的就直接返回**，所以选中概率 ≠ `w_i / ΣW`。
多个 item 同时通过时，越靠前的越占优。另外每次失败尝试都会消耗一次随机数，
所以**调整 collection 里的条目顺序会改变整个堆的分布**。

要真正按权重采样，应改成累积分布（cumulative）或别名法，例如：

```csharp
private WeightedItemSO PickWeighted(WeightedItemCollectionSO list)
{
    float total = 0f;
    foreach (var it in list.weightedItems) total += Mathf.Max(0f, it.weight);
    float r = random.GetStaticFloat(0f, total);
    float acc = 0f;
    foreach (var it in list.weightedItems)
    {
        acc += Mathf.Max(0f, it.weight);
        if (r <= acc) return it;
    }
    return list.weightedItems[list.weightedItems.Length - 1];
}
```

注意：改成累积采样会消耗**不同数量**的随机数，因此同一 seed 生成的堆会与旧版本不同。

### 5.2 `spawnBounds` 只有在 `SpawnVolume == null` 时才被赋值（`DebrisSpawner.cs:56-67`）

```csharp
if (SpawnVolume == null) {
    SpawnVolume = GameObject.CreatePrimitive(PrimitiveType.Cube);
    ...
    spawnBounds = meshbounds;      // ← 只在这个分支里赋值
}
```

如果你在 Inspector 里给 `SpawnVolume` 指派了一个物体，`spawnBounds` 就保持 `default(Bounds)`
（中心在原点、size 为 0），**所有 debris 会全部生成在原点**，看起来像"堆生成坏了"。
目前 `Managers.prefab` 里 `SpawnVolume: {fileID: 0}` 且场景未覆盖，所以走的是自动创建分支，功能正常。
要支持自定义 spawn 体积，需把 `spawnBounds = SpawnVolume.GetComponent<Collider>().bounds;` 补进 else 分支。

### 5.3 编辑器里命令行参数完全不生效（`CustomArgs.cs:72-80`）

```csharp
#if UNITY_EDITOR
    ParseArguments(debugArguments.text.Split(...));   // 只读 Assets/MITLL/Debug/DebugArgs.txt
#else
    ParseArguments(Environment.GetCommandLineArgs()); // 只有打包后的播放器才读命令行
#endif
```

**这对自动化影响很大**：README 说明 `exportstl` 在官方 release 二进制里因授权被禁用，
所以你要导出 STL 就必须用编辑器跑；而编辑器里 `-exportstl 1` 传命令行**没用**，
必须改写 `Assets/MITLL/Debug/DebugArgs.txt`（当前内容已配置为 `-randomseed 1 ... -exportstl 1`）。

另外 `argDict` 是 `static`，`GetWithDefault()` 会把"默认值"也写进去。
若关掉 Editor 的 Domain Reload（Enter Play Mode Options），跨播放会话会残留旧参数。

### 5.4 `randomseed 0` 表示"随机"，不是"种子 0"（`RandomManager.cs:54-56`）

```csharp
seed = (int)CustomArgs.GetWithDefault("randomseed", 0);
if (seed == 0) seed = new Random().Next(148000);   // 时间种子
```

要可复现必须传 `1..147999`。`RandomManager` 是 `DontDestroyOnLoad` 单例，
`randomStatic` 供废墟堆使用（`GetStaticFloat`），`randomDynamic` 供灯/UI/机器人随机落点使用——
**只要在 static 流里插入任何一次额外取值，整堆布局就会全变**。

### 5.5 Prefab 上已有 Rigidbody 时，`ValidateDebrisColliders` 什么都不做（`DebrisSpawner.cs:163-186`）

```csharp
if (!debris.GetComponent<Rigidbody>()) {   // 只有完全没有 Rigidbody 才补 Collider 和 Rigidbody
    ... rb.mass = 20;
}
```

17 个 debris prefab **都自带 Rigidbody（mass=1）**，所以 `mass = 20` 这个分支实际上从不触发，
真实质量是 1；而"有 Rigidbody 但没 Collider"的 prefab 不会被修好，会直接穿模掉下去（然后被 y<-0.5 删掉）。

### 5.6 `RosSensorOutput` 的发布频率在 prefab 里被设到 50 Hz（`VineRobot.prefab`）

代码默认是 `posRotPublishFreq = 0.2f`、`camPublishFreq = 0.5f`，
但 prefab 覆盖成了 **`0.02` / `0.02`**。每次发布做的是：

```csharp
targetCamera.Render();                                   // 额外强制渲染一次
camTexture.ReadPixels(...);                              // GPU→CPU 回读
camTexture.ToImageMsg(new HeaderMsg());                  // PNG 编码
```

即 **50 Hz × 2 台 1024×1024 相机的 Render + 回读 + PNG 编码**，是非常重的负载。
做数据采集时建议先把它降到 1–5 Hz。另外 `GetRenderTarget()` 是 `SendImage()` 的未使用重复实现。

### 5.7 用 `SpawnVolume` 尺寸语义、"小碎块也吃 numToSpawn"

`spawnboundx=10` 表示**边长 10**，即 x ∈ [-5, +5]。小碎块层同样使用 `numToSpawn`
（`GeneratePile` 内部统一用 `numToSpawn`），只调小碎块数量需要自己加参数。

### 5.8 其他细节

- `ItemBuilder.prefab` 自身字段是**过期的**：`prefabPath: Assets/Resources/Prefabs`（该目录不存在）、
  `itemsPath: .../Items/SmallDebris`、`collectionName: SmallDebris`。
  场景里的 ItemBuilder 实例通过 override 修正成了 MainDebris（`prefabPath: Assets/MITLL/Prefabs/Debris`）。
  用 prefab 默认值直接点按钮会 `asset.gameObject` 为 null → `NullReferenceException`。
- 输入动作名拼写错误：`Instructons`（`SproutControls.inputactions` 与生成类都是这个拼写）。
  要改名必须同时改 `.inputactions` 并让它重新生成 `SproutControls.cs`。
- `DebrisLibraryScriptableObject.cs`、`CSVReader.cs` 是死代码，可以安全删除。
- `CustomArgs.ROSIP` 的 getter 判断 `if (ROSip != null || ROSip == "")` 逻辑别扭
  （空串会原样返回、只有 null 才回落到 `127.0.0.1`），功能上勉强可用，重构时注意。
- `numToSpawn` / `numPiles` 的 Inspector 值会被 CLI 默认值覆盖（`GetWithDefault("numobjs", 300)`），
  所以改 prefab 里的数字**不生效**，必须传参数。`spawnDelay` 没有 CLI 参数，只能改 prefab。

---

## 6. 二次开发扩展点（按代价从低到高）

### A. 只换废墟素材（零代码）
1. 把 fbx 拖进 `Assets/MITLL/Models/Debris/`，建 prefab 到 `Assets/MITLL/Prefabs/Debris/`，**确保有 Collider + Rigidbody**。
2. 编辑 `Assets/MITLL/Resources/WeightedCollection.csv`（`object,weight`，object 必须等于 prefab 文件名）。
3. 选中场景里的 `ItemBuilder`，确认 `prefabPath = Assets/MITLL/Prefabs/Debris`、`itemsPath = .../Items/MainDebris`、
   `collectionName = MainDebris`，点 **Generate Items**。
4. 注意 5.1/5.4 节的坑；生成后建议手工检查 `MainDebrisPrototype.asset` 的条目数是否为预期值（不要出现累加）。

### B. 只调规模/位置（零代码，用 CLI 或 DebugArgs.txt）
```
-randomseed 12345 -numobjs 400 -numlayers 5 -spawnposy 20 -spawnboundx 16 -spawnboundy 12 -spawnboundz 16
```
调试期写进 `Assets/MITLL/Debug/DebugArgs.txt`；发布后用命令行。

### C. 改"怎么摆"（改 `DebrisSpawner.GeneratePile`）——最常见的真实需求
现在的摆放是**立方体内均匀分布 + 均匀缩放 1~5 + 全向随机旋转**，靠物理自由落体堆成堆。想更像真实坍塌，可以：
- 换成分层/高斯撒点（例如按层高度收缩 xz 范围）；
- 尺寸分级（先大块后小块，或按 `Mathf.Pow` 偏置让小块更多）；
- 从单一"坍塌点"向上锥形撒点；
- 让缩放与质量联动（`rb.mass = k * scale^3`），现在所有物体质量都是 1，大块和小块惯性一样，堆的形状会偏"塑料感"。

因为缩放是在 `Instantiate` **之后**用 `localScale` 设置的（`DebrisSpawner.cs:155`），
而 Rigidbody 的惯性张量会自动随 transform 缩放重算，所以改质量是安全的一行改动。

### D. 加一个新的命令行参数
`CustomArgs` 是纯静态字典，任意位置在 `Awake` 之后调用即可：

```csharp
// 例：新增 -debrisgauss 1 控制分布形状
bool useGauss = CustomArgs.FloatToBool(CustomArgs.GetWithDefault("debrisgauss", 0f));
```
键名会 `ToLower()`，必须全小写；解析器按 `-key` 切分并把后续非 `-` 开头的 token 当值
（负号开头的 float 能正常解析，其它 `-xxx` 视为下一个 key）。**没有类型系统，全是 float。**

### E. 修加权采样（见 5.1）
替换 `Spawn()`，并在生成前调用一次 `weightedItems` 去重/清理。这是让"权重"真正有意义的前提。

### F. 增加新的传感器输出（`RosSensorOutput.cs`）
现成的模式可以直接抄：`RegisterPublisher<T>()` + 定时回调 + `ros.Publish(topic, msg)`。
当前发布 `pos_rot`(Pose)、`rgb_cam`/`depth_cam`(Image，PNG 编码)。

**深度图的语义要注意**：`Depth Camera` 的 `m_RendererIndex: 1` 指向
`Assets/Settings/URP-HighFidelity-Depth.asset`，它带一个 `FullScreenPassRendererFeature`
（`Assets/MITLL/Shaders/DepthCam.mat` → `DepthCam.shadergraph`）。
该 shadergraph 的链路是 `Scene Depth → Remap → Multiply → Combine(BaseColor)`，
输出的是**归一化后的可视化深度（RGB）**，不是米制深度。
所以 `depth_cam` 话题给的是"看起来像深度图"的图。如果要**米制深度 / 点云**，需要改 shadergraph
（把线性深度按 near/far 映射编码进多通道）或绕开渲染另做一次深度回读。
相机的 renderer index 分别是：RGB = `-1`（默认渲染器）、Depth = `1`（Depth 渲染器）、Main = `-1`；
clip 平面：RGB `0.05 / 1000`、Depth **`0.05 / 10`**、Main `0.05 / 1000`，FOV 都是 90°。
也就是说深度相机的有效量程只有 10 米——把废墟堆做大或抬高相机时，超出 10 米的部分会被裁掉。

### G. 加真值标注（对数据集生成最有价值）
`SceneToSTLExporter.ExportSceneToSTL()` 展示了如何拿到全部 debris：
`FindObjectOfType<DebrisSpawner>().GetDebrisObj()`，然后逐个取 `MeshFilter`/`transform.localToWorldMatrix`。
在此基础上很容易再加一个"逐物体位姿 + 类别 + 实例掩码"的导出（JSON/CSV），
用于 6D pose 或语义分割的监督。注意 `FreezeDebris()` 会先把物体 parent 到 `root` 并做静态合批，
真值导出要在这之前（或之后，因为 `objList` 仍持有引用）。

### H. 换机器人（URDF）
保留 `VineRobot.prefab` 的骨架即可：`Rigidbody` + `SphereCollider` + 两台相机 + `VineController` + `RosSensorOutput`。
`VineController.manager` 字段在 prefab 里是空的，**由场景 override 指向 Managers 的 GameManager**
（`MainScene_OS.unity` 中 `propertyPath: manager`）；自己搭机器人时别忘了接这个引用，否则 `EmergencyStop()` 会 NPE。
`uiController` 不用管，`Start()` 里用 `FindObjectOfType` 自动找。

---

## 7. 环境搭建与构建

```bash
# 1. 装 Unity Hub + Unity 2022.3.62f2（必须带 Windows/Linux 构建支持）
#    Linux 下需要 Unity Hub 的 Linux Editor；本仓库无 Library/，首次打开会重新 import 116MB 资源
# 2. 打开工程，等待 Package Manager 从 GitHub 拉 3 个 Git 包（需要网络）
# 3. 打开 Assets/MITLL/Scenes/MainScene_OS.unity（Build Settings 里唯一的场景）
# 4. 直接 Play（参数从 Assets/MITLL/Debug/DebugArgs.txt 读）
```

打包与自动化：

```bash
Unity -quit -batchmode -projectPath <repo> \
      -executeMethod <YourEditorClass.YourMethod> \
      -logFile build.log
# 注意：-randomseed 等自定义参数在编辑器模式下不会被解析（见 5.3）
```

关于 `exportstl`：README 明确说明官方 release 二进制**禁用了 STL 导出（授权原因）**，
必须自己用 Unity 构建才能用。STL 落在 `Application.persistentDataPath/SceneData/GroundTruth_yy-MM-dd-HH-mm.stl`
（Windows 即 `AppData\LocalLow\MIT Lincoln Laboratory\RubbleSim\SceneData`）。
注意文件名只精确到**分钟**，批量生成时同分钟内会互相覆盖。

### 批量生成数据集的建议路线
由于生成是"协程 + 物理沉降"驱动的，最省事的做法是写一个 Editor 脚本（`-executeMethod` 入口）：

1. 进入 Play 模式前，把本轮参数写入 `Assets/MITLL/Debug/DebugArgs.txt`（含 `-randomseed <i>`）；
2. 加载 `MainScene_OS`，等待 `GameManager.doInit` 触发（表示 `FreezeDebris()` 已完成）；
3. 此时抓取相机 / 导出 STL / 导出真值标注；
4. 退出 Play，进入下一轮 `i`。

或者干脆绕开 `CustomArgs`，在自己的 Editor 脚本里直接给 `RandomManager.seed`、`DebrisSpawner.numToSpawn` 等字段赋值，
这样比反复重写 `DebugArgs.txt` 更干净（但要注意 `Start()` 里 `numToSpawn` 会被 `GetWithDefault` 覆盖，需要在 `Start` 之后再写）。

---

## 8. 一分钟速查

| 我想…… | 改哪里 |
|---|---|
| 换废墟模型/权重 | `Prefabs/Debris/` + `Resources/WeightedCollection.csv` + ItemBuilder 按钮 |
| 调数量/范围/种子 | CLI 参数（编辑器下写 `Debug/MainScene`→`Debug/DebugArgs.txt`） |
| 改摆放分布、尺寸、质量 | `DebrisSpawner.GeneratePile()` |
| 修加权采样 | `DebrisSpawner.Spawn()`（见 5.1） |
| 加参数 | `CustomArgs.GetWithDefault("mykey", default)` |
| 改沉降时间/分层数 | `Managers.prefab` 的 `spawnDelay`（无 CLI）/ `-numlayers` |
| 改机器人运动 | `VineController`（moveSpeed=2, rotSpeed=0.05, mass=5, drag=2, useGravity=0） |
| 加 ROS 话题 | `RosSensorOutput`（注意 5.6 的频率与 6.F 的深度语义） |
| 导出几何真值 | `SceneToSTLExporter`（注意分钟级文件名冲突） |
| 加真值标注 | 基于 `DebrisSpawner.GetDebrisObj()` 自写 |
