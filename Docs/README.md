# Docs

项目级文档（与 `software/sim/Rubble_Pile_Simulator` 的模拟器本体、场景、算法、资产相关）。
`tools/` 目录可能不随发布一起提供，因此**工具自身的用法说明放在各自的 `tools/*/docs/` 下，这里只放项目本身的内容**。

| 文档 | 内容 |
|---|---|
| [procedural_debris_generation.md](procedural_debris_generation.md) | 程序化废墟碎片生成算法：沃罗诺伊切割、切缝、共享边界噪声、放样、崩角、质量与斥力；参数、已知限制与后续改进方向 |
| [coarse_perlin_ground.md](coarse_perlin_ground.md) | 工作区结构、稀疏柏林噪声斜面地形、物理接地与外围录制 |
| [MuJoCo 导出器](../tools/mujoco_export/docs/README.md) | 完整物理快照、MJCF/网格/初始状态、地形裁剪和无头验证 |
