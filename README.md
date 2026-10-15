# 卡槽与铲子快捷键（SlotHotkeys）

> 给战斗选卡栏的卡槽与铲子加键盘快捷键。

| 项目 | 内容 |
| --- | --- |
| Mod ID | `slothotkeys` |
| 程序集 | `JTYSlotHotkeys` → 包内 `Runtime/ModAssembly.dll` |
| 入口类 | `SlotHotkeysEntry` |
| 当前版本 | 1.0.0 |
| 分类 | 工具与前置 |

## 版本历史


## 这个模组能做什么

- **卡槽**：冷却（CD）**恢复完成之后**，在卡槽内显示它的键位；按一下即选中该卡
- **铲子**：键位**一直显示**；按一下切换铲子
- 键位均可在「Mod 配置」面板里改，**改动立即生效**

## 配置项

| 配置项 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `enabled` | bool | 开 | 总开关 |
| `slotKeys` | string | `1234567890` | 键位表：从左到右每个卡槽一个字符（数字或字母） |
| `showOnlyWhenReady` | bool | 开 | 开=CD 好了才显示键位；关=一直显示 |
| `shovelKey` | string | `Q` | 铲子键位 |
| `enableLog` | bool | 关 | 输出诊断日志 |

> 面板**没有"按键输入"控件**（类型只有 bool/int/float/string/enum/multienum/text/richtext/color），
> 所以键位用 `string` 让玩家填字符。

## 实现要点（全部来自 0.30.0 游戏文件实地读取）

选卡栏本体：`Registry/Battle/Feature/PacketBank/PacketBank/TowerDefenseInGamePacketBank.cs`

| 节点 | 用途 |
| --- | --- |
| `%PacketContainer` | ★ **卡槽容器**（卡槽是它的子节点） |
| `%PacketBankScroll` / `%PacketBankMargin` / `%PacketBankPanelTexture` | 滚动 / 边距 / 面板底图 |
| `%Translate` | 进出场位移节点 |
| `%CardSort` | 右侧分类面板 |

卡槽类型 **`TowerDefenseInGamePacketShow`** 的关键成员：

| 成员 | 用途 |
| --- | --- |
| `button` | ★ 触发选卡（发 `pressed`，与玩家点击**同一条路径**） |
| `coldDown` | ★ 冷却剩余（`<= 0` 即 CD 好了） |
| `coldDownProgressBar` | 冷却进度条 |
| `itemCostLabel` | 阳光费用标签（键位标签贴右下角，避开它） |

铲子 UI = `TowerDefenseBattleFeatureShovel.shovelPickTool`（类型名 **`ShovelPickTool`**）。

### ⚠️ 两个关键技术约束

1. **卡槽是虚拟化复用的**（该类有 `SetVirtualizedPackets` / `RefreshVirtualizedPacketBindings`）
   ⇒ 一个节点会先后绑定不同的卡 ⇒ 键位标签**必须每帧按当前状态刷新**，不能"建一次就完事"。
2. **无源生成器 ⇒ 不能用 `_Input`** ⇒ 按键轮询放在 `SceneTree.process_frame` 回调里，
   用 `Input.IsKeyPressed` + 自己记「上一帧」实现"刚按下"。
3. **跨程序集引用游戏类型有编不过风险** ⇒ 卡槽/铲子类型**按类型名匹配**，
   `coldDown` / `button` 用**反射**取（零编译期依赖）。

## 硬护栏（违反会整包被拒）

1. `mod.json` 必须在**根**且**唯一**
2. `Runtime/` 下**只允许** `ModAssembly.dll`
3. 包内**绝不允许**出现 `.cs`

## 构建

```powershell
python mods\SlotHotkeys\build_and_install.py --install
python pvz-hybrid-mod-skills\tools\verify_build.py SlotHotkeys   # 必须跑
```

## 未实测说明

按项目约定，开发期不启动游戏 ⇒ 以下**属待实测**：
- 卡槽键位是否真的在 CD 好后出现、按下是否真的选卡
- 铲子键位是否常驻、按 Q 是否真的切换铲子

## 更新记录

| 版本 | 变更 |
| --- | --- |
| **4.2.1** | ① **修复「启用总开关」被误关**导致整个 Mod 停止工作（配置页按钮不注入、卡槽/铲子快捷键失效）；② **战斗设置页新增「改键面板」入口按钮**，与时停、CD 面板**竖排在同一列**；③ 配置页「改键面板」按钮恢复可见。 |
