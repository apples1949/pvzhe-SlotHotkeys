using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「卡槽/铲子快捷键」Mod v3 —— v2 基础上新增：游戏内按键录制改键面板。
/// （原生热键屏蔽已拆分为独立 Mod：NativeKeyBlocker）
///
/// ── v3 新增（2026-10-11 用户需求）────────────────────────────────
///   「点配置 → 按键录制」：宿主没有按键控件（XWModSettingsService 仅
///   bool/enum/text/color/float/int/string/multienum/richtext），
///   但 XWModSettings.TrySave(changes) 是 public（Service.Save 落盘+立即 Apply+Changed）。
///   ⇒ 做**游戏内面板**：F8 呼出 → 点一行 → 按新键（WindowInput 捕获）→
///     TrySave 写回 slotKeyN/shovelKey → 立即生效。冲突键自动对调。
///
/// ── v2 继承（全部 unpack 源码实锤）────────────────────────────────
///   · 卡槽 = SeedBank.packetList（Array&lt;TowerDefenseInGamePacketShow&gt;），顺序=槽位；
///     CD 好了 = !coldDownOpen；触发 = card.Pressed()（原生按压链）。
///   · 铲子 = ShovelManager（在树里）→ shovelButton；照抄游戏 _Input：
///     翻转 ButtonPressed + ShovelButtonPressed()。
///   · 空槽 PacketSlot 纯视觉占位，无选卡交互（非 bug）。
///
/// ── 铁律 ──────────────────────────────────────────────────────────
/// · 手写 csproj ⇒ 无源生成器 ⇒ 不用 _Input/_Process；轮询走 process_frame 信号。
///   （WindowInput 是 Godot.Window 上的**既有事件**，直接订阅合法——源生成器
///     只影响自定义 Node 子类的虚方法，不影响订阅现成类型的事件。）
/// · 入口三回调一律 try/catch 吞异常（抛出 = 整包回滚）。
/// · 能编译期直调就别反射。
/// </summary>
public sealed class SlotHotkeysEntry : IXWModRuntimeEntry
{
	private const string P = "[SlotHotkeys] ";

	// ── 配置键 ────────────────────────────────────────────────────────
	private const string KEnabled = "enabled";
	private const string KShowOnlyReady = "showOnlyWhenReady";
	private const string KKeysData = "keysData";   // ★ v4：全部键位存一个字符串（面板维护）
	private const string KLog = "enableLog";
	// v3：改键面板
	private const string KPanelKey = "panelKey";

	private XWModRuntimeContext _context;
	private XWModSettings _settings;
	private bool _subscribed;
	private SceneTree _tree;
	private Callable _tick;
	private bool _started;
	private int _diag;

	/// <summary>每张卡片一个标签：卡片 instanceId → Label。</summary>
	private readonly Dictionary<ulong, Label> _labels = new Dictionary<ulong, Label>();

	/// <summary>上一帧按键状态（按下沿检测）。</summary>
	private readonly HashSet<ulong> _prevDown = new HashSet<ulong>();
	private bool _shovelPrevDown;
	private bool _panelPrevDown;

	/// <summary>进战斗提示只打一次。</summary>
	private bool _battleLogged;

	// 铲子引用缓存
	private ShovelManager _shovelManager;
	private Label _shovelLabel;

	// ── v3：改键面板状态 ─────────────────────────────────────────────
	private CanvasLayer _panelLayer;
	private Control _panelDim;
	private Label _captureHint;
	private readonly List<Button> _rowButtons = new List<Button>();
	private readonly List<Button> _clearButtons = new List<Button>();
	/// <summary>赞助弹窗（收款码）。</summary>
	private CanvasLayer _sponsorLayer;
	/// <summary>收款码纹理缓存（配置页关于栏 + 弹窗共用）。</summary>
	private Texture2D _qrTexture;
	private const int RowCount = 17;   // 0..15 = 卡槽1..16，16 = 铲子
	/// <summary>录制态专用「取消录制」按钮（BuildPanel 创建）。</summary>
	private Button _cancelCaptureButton;
	/// <summary>★ v4.1：「保存」按钮（有暂存改动时显示）。</summary>
	private Button _saveButton;
	/// <summary>正在录制改键的行（-1 = 未在录制）。</summary>
	private int _captureRow = -1;
	private bool _windowInputHooked;

	// ============================================================ 生命周期

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			_context = context;
			try { _settings = context.Settings; }
			catch (Exception ex) { Warn("原生 Mod 配置不可用，用内置默认值：" + ex.GetBaseException().Message); }
			Log("初始化完成（v3：改键面板版；原生热键屏蔽在 NativeKeyBlocker）。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started) { return; }
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null) { Warn("拿不到 SceneTree，本 Mod 不生效。"); return; }
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			Subscribe();
			HookWindowInput();
			_started = true;
			Log("已挂载 process_frame。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void Shutdown()
	{
		try
		{
			Unsubscribe();
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
			}
			ClosePanel();
			ClearAll();
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Shutdown 异常（已吞）：" + ex.Message); } catch { }
		}
		finally { _started = false; }
	}

	// ============================================================ 配置读取

	private void Subscribe()
	{
		try
		{
			if (_settings == null || _subscribed) { return; }
			_settings.Changed += OnSettingsChanged;
			_subscribed = true;
		}
		catch (Exception ex) { Warn("订阅配置失败：" + ex.GetBaseException().Message); }
	}

	private void Unsubscribe()
	{
		try
		{
			if (_settings != null && _subscribed)
			{
				_settings.Changed -= OnSettingsChanged;
				_subscribed = false;
			}
		}
		catch { }
	}

	private void OnSettingsChanged(IReadOnlyList<string> keys)
	{
		try
		{
			InvalidateKeysCache();   // ★ v4：keysData 可能被外部改动，失效缓存
			ClearLabels();
			RefreshPanelRows();          // 面板开着的话同步显示
			Log("配置已更新：" + string.Join("、", keys));
		}
		catch { }
	}

	private object ReadRaw(string key)
	{
		if (_settings == null) { return null; }
		try { return _settings.Get<object>(key); }
		catch { return null; }
	}

	private bool ReadBool(string key, bool fallback)
	{
		object v = ReadRaw(key);
		if (v is bool b) { return b; }
		if (v is string s && bool.TryParse(s, out bool sb)) { return sb; }
		try
		{
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
		}
		catch { }
		return fallback;
	}

	private string ReadString(string key, string fallback)
	{
		object v = ReadRaw(key);
		if (v is string s && !string.IsNullOrWhiteSpace(s)) { return s; }
		try
		{
			if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				string r = je.GetString();
				if (!string.IsNullOrWhiteSpace(r)) { return r; }
			}
		}
		catch { }
		return fallback;
	}

	// ============================================================ 键位解析（v3：支持特殊键）

	/// <summary>配置字符串 → Key。空 = Key.None（该槽不启用）；单字符按 ASCII 映射（数字/字母正好同码）；多字符按枚举名（如 F8、Space）。</summary>
	private static Key ParseKeySetting(string s)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(s)) { return Key.None; }
			s = s.Trim();
			if (s.Length == 1)
			{
				int code = s[0];
				if (code >= 32 && code <= 126) { return (Key)code; }
				return Key.None;
			}
			if (Enum.TryParse(s, true, out Key k)) { return k; }
			return Key.None;
		}
		catch { return Key.None; }
	}

	/// <summary>
	/// Key → 配置字符串（可被 ParseKeySetting 无损还原）。
	/// ★ v4.1.4：None → 空串（不再是 "None" 字面量）。
	/// ★ v4.1.5（2026-10-12）：改为**复用共享面板的 `KeybindPanelManager.KeyToString`** ——
	///   两边必须同口径，否则按键录进 keysData 的拼写与下拉/标签（"VolumeUp"→手机音量+）
	///   对不上。原因见 `KeybindPanelManager.KeyToString` 注释：Godot 枚举名是
	///   `Volumeup/Volumedown/Volumemute`（小写），而各处的选项/标签拼写是
	///   `VolumeUp/VolumeDown/VolumeMute`；另外 Space/-/=/./, 也会被旧逻辑转成字符而失配。
	/// </summary>
	private static string KeyToSetting(Key k)
	{
		try { return KeybindPanelManager.KeyToString(k) ?? ""; }
		catch { return ""; }
	}

	/// <summary>Key → 面板/标签显示文本。</summary>
	private static string KeyToDisplay(Key k)
	{
		if (k == Key.None) { return "（无）"; }
		return KeyToSetting(k);
	}

	/// <summary>
	/// ★ v4：keysData 解析缓存。"卡槽1=键,卡槽2=键,...,铲子=键"（键=KeyToSetting 字符串，空=无绑定）。
	/// 失效条件：配置 Changed 事件 / 本 Mod 写入后手动刷新。
	/// </summary>
	private Dictionary<int, Key> _keysCache;
	private Key _shovelKeyCache = Key.None;

	private void InvalidateKeysCache()
	{
		_keysCache = null;
		_shovelKeyCache = Key.None;
	}

	/// <summary>解析 keysData → 每行键位（缓存）。</summary>
	private void EnsureKeysCache()
	{
		if (_keysCache != null) { return; }
		_keysCache = new Dictionary<int, Key>();
		string raw = ReadString(KKeysData, "");
		if (string.IsNullOrWhiteSpace(raw)) { return; }
		foreach (string part in raw.Split(','))
		{
			int eq = part.IndexOf('=');
			if (eq <= 0) { continue; }
			if (!int.TryParse(part.Substring(0, eq).Trim(), out int idx)) { continue; }
			string keyStr = part.Substring(eq + 1).Trim();
			Key k = ParseKeySetting(keyStr);
			if (idx == 16) { _shovelKeyCache = k; }
			else if (idx >= 0 && idx < 16) { _keysCache[idx] = k; }
		}
	}

	/// <summary>keysData → 写回字符串（保存前调用）。</summary>
	private string BuildKeysData()
	{
		EnsureKeysCache();
		var parts = new List<string>();
		for (int i = 0; i < 16; i++)
		{
			Key k = _keysCache.TryGetValue(i, out Key kk) ? kk : Key.None;
			parts.Add(i + "=" + (KeyToSetting(k) ?? ""));
		}
		parts.Add("16=" + (KeyToSetting(_shovelKeyCache) ?? ""));
		return string.Join(",", parts);
	}

	/// <summary>第 idx 行（0..15=槽1..16，16=铲子）当前键位。</summary>
	private Key KeyOfRow(int row)
	{
		if (row < 0 || row >= RowCount) { return Key.None; }
		EnsureKeysCache();
		if (row == 16) { return _shovelKeyCache; }
		return _keysCache.TryGetValue(row, out Key k) ? k : Key.None;
	}

	/// <summary>设置某行键位并写回配置（面板录制/清空统一入口）。</summary>
	private void SetRowKey(int row, Key k, string oldForKeySwap)
	{
		EnsureKeysCache();
		if (row == 16) { _shovelKeyCache = k; }
		else { _keysCache[row] = k; }
		var changes = new Dictionary<string, object>();
		changes[KKeysData] = BuildKeysData();
		XWModSettingsSaveResult result = null;
		if (_settings != null && _settings.TrySave(changes, out result))
		{
			Log("键位已保存：" + result.Message);
		}
		else
		{
			Warn("键位保存失败：" + (result != null ? result.Message : "配置不可用"));
		}
	}

	private string TitleOfRow(int row)
	{
		return row == 16 ? "铲子" : "卡槽 " + (row + 1);
	}

	// ============================================================ 每帧主逻辑

	private void OnFrame()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree)) { return; }

			if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KEnabled, true)） */
			{
				ClearAll();
				return;
			}

			// ── 改键面板：呼出键 + 录制态 ─────────────────────────────
			if (_captureRow >= 0)
			{
				// 录制中：所有轮询暂停（按键交给 WindowInput 捕获，避免同时触发卡槽）
				return;
			}
			TickPanelToggle();

			// ── 配置页「关于」栏注入收款码图（低频扫描即可）────────────
			if (++_qrTick % 30 == 0)
			{
				TickAboutQrImage();
				TickConfigPagePanelButton();
				TickMobileHidePanelKeyDropdown();   // ★ v4.1.5：手机端隐藏宿主那个 73 项下拉
			}

			// ── 找战斗选卡栏（不在战斗 ⇒ 清理后直接返回）──────────────
			TowerDefenseInGameSeedBank bank = FindByTypeName<TowerDefenseInGameSeedBank>(_tree.Root);
			if (bank == null || !GodotObject.IsInstanceValid(bank) || !bank.IsInsideTree())
			{
				ClearLabels();
				_shovelManager = null;
				_shovelLabel = null;
				return;
			}

			if (!_battleLogged)
			{
				_battleLogged = true;
				try
				{
					string keys = "";
					for (int i = 0; i < bank.packetList.Count && i < 16; i++) { keys += KeyToDisplay(KeyOfRow(i)); }
					Log("战斗就绪：卡片 " + bank.packetList.Count + " 张，键位 [" + keys + "]，铲子=" + KeyToDisplay(KeyOfRow(16)));
				}
				catch { }
			}

			bool onlyReady = ReadBool(KShowOnlyReady, true);

			// ── 卡槽：packetList 顺序即槽位顺序 ────────────────────────
			var packets = bank.packetList;
			var seenIds = new HashSet<ulong>();
			for (int i = 0; i < packets.Count; i++)
			{
				TowerDefenseInGamePacketShow card = packets[i];
				if (card == null || !GodotObject.IsInstanceValid(card)) { continue; }
				Key key = KeyOfRow(i);
				if (key == Key.None) { continue; }
				UpdateCard(card, key, onlyReady);
				seenIds.Add(card.GetInstanceId());
			}
			PrunePrevDown(seenIds);

			// ── 铲子 ─────────────────────────────────────────────────
			UpdateShovel(bank);
		}
		catch (Exception ex)
		{
			if (_diag < 100)
			{
				_diag = 100;
				Warn("每帧异常（只报一次）：" + ex.GetBaseException().Message);
			}
		}
	}

	// ============================================================ 改键面板

	// ── v3.3：战斗设置页入口已按用户要求移除（恢复游戏原生单列布局）。
	//    改键面板入口保留两处：① 本 Mod 配置页「改键面板」按钮；② 可选的呼出键。

	private void HookWindowInput()
	{
		try
		{
			if (_windowInputHooked || _tree == null || _tree.Root == null) { return; }
			_tree.Root.WindowInput += OnWindowInput;
			_windowInputHooked = true;
		}
		catch (Exception ex) { WarnOnce("wininput", "订阅 WindowInput 失败：" + ex.Message); }
	}

	/// <summary>呼出/关闭面板（面板打开但未在录制时才允许关闭）。</summary>
	private void TickPanelToggle()
	{
		try
		{
			Key pk = ParseKeySetting(ReadString(KPanelKey, ""));
			if (pk == Key.None) { return; }
			bool down = false;
			try { down = Input.IsKeyPressed(pk); } catch { }
			if (down && !_panelPrevDown)
			{
				TogglePanel();
			}
			_panelPrevDown = down;
		}
		catch { }
	}

	/// <summary>呼出/关闭面板（供呼出键与「战斗设置页入口按钮」共用）。</summary>
	public void TogglePanel()
	{
		try
		{
			if (_panelLayer != null && GodotObject.IsInstanceValid(_panelLayer) && _panelLayer.Visible)
			{
				ClosePanel();
				Log("已关闭键位设置面板。");
			}
			else
			{
				OpenPanel();
			}
		}
		catch { }
	}

	private void OpenPanel()
	{
		try
		{
			if (_panelLayer == null || !GodotObject.IsInstanceValid(_panelLayer))
			{
				BuildPanel();
			}
			RefreshPanelRows();
			_captureRow = -1;
			UpdateCaptureHint();
			_panelLayer.Visible = true;
			Log("已打开键位设置面板（点一行 → 按新键；Esc 取消）。");
		}
		catch (Exception ex) { Warn("打开改键面板失败：" + ex.GetBaseException().Message); }
	}

	private void ClosePanel()
	{
		try
		{
			_captureRow = -1;
			if (_tree?.Root != null && _tree.Root.HasMeta("__mod_keybind_capturing")) { _tree.Root.RemoveMeta("__mod_keybind_capturing"); }
			if (_pendingChanges != null)
			{
				_pendingChanges = null;   // ★ v4.1：关闭=放弃未保存改动（面板里有明确提示）
				Log("已关闭面板，未保存的改动已放弃。");
			}
			if (_panelLayer != null && GodotObject.IsInstanceValid(_panelLayer))
			{
				_panelLayer.Visible = false;
			}
			if (_sponsorLayer != null && GodotObject.IsInstanceValid(_sponsorLayer))
			{
				_sponsorLayer.Visible = false;   // 主面板关了，赞助弹窗也一起收
			}
		}
		catch { }
	}

	private void BuildPanel()
	{
		// 层：高于游戏对话框（layer=100）
		CanvasLayer layer = new CanvasLayer();
		layer.Name = "ModSlotHotkeysPanelLayer";
		layer.Layer = 200;
		// ★ 关键：面板必须 Always——从「战斗设置页」打开时整棵树是 Paused 的，
		//   Pausable 的按钮收不到点击（血泪：游戏对话框 pasue=true 挂起全树）
		layer.ProcessMode = Node.ProcessModeEnum.Always;

		// 暗幕：挡住游戏点击
		ColorRect dim = new ColorRect();
		dim.Name = "Dim";
		dim.Color = new Color(0f, 0f, 0f, 0.45f);
		dim.MouseFilter = Control.MouseFilterEnum.Stop;
		dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(dim, false, Node.InternalMode.Disabled);

		// 居中容器
		CenterContainer center = new CenterContainer();
		center.Name = "Center";
		center.MouseFilter = Control.MouseFilterEnum.Ignore;
		center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		dim.AddChild(center, false, Node.InternalMode.Disabled);

		PanelContainer panel = new PanelContainer();
		panel.Name = "Panel";
		center.AddChild(panel, false, Node.InternalMode.Disabled);

		VBoxContainer box = new VBoxContainer();
		box.Name = "Box";
		box.AddThemeConstantOverride("separation", 6);
		panel.AddChild(box, false, Node.InternalMode.Disabled);

		Label title = new Label();
		title.Name = "Title";
		title.Text = "键位设置 · 卡槽与铲子快捷键";
		title.AddThemeFontSizeOverride("font_size", 20);
		title.HorizontalAlignment = HorizontalAlignment.Center;
		box.AddChild(title, false, Node.InternalMode.Disabled);

		_captureHint = new Label();
		_captureHint.Name = "Hint";
		_captureHint.Text = "点击一行 → 按下新按键（Esc 取消）；冲突键自动对调；保存后立即生效。";
		_captureHint.AddThemeFontSizeOverride("font_size", 14);
		_captureHint.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.9f));
		_captureHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_captureHint.CustomMinimumSize = new Vector2(440f, 0f);
		box.AddChild(_captureHint, false, Node.InternalMode.Disabled);

		// 滚动区（窗口太小时可滚）；v3.2 加宽（用户反馈：滑动栏宽度加长）
		ScrollContainer scroll = new ScrollContainer();
		scroll.Name = "Scroll";
		scroll.CustomMinimumSize = new Vector2(620f, 480f);
		scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
		box.AddChild(scroll, false, Node.InternalMode.Disabled);

		VBoxContainer rows = new VBoxContainer();
		rows.Name = "Rows";
		rows.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		rows.AddThemeConstantOverride("separation", 4);
		scroll.AddChild(rows, false, Node.InternalMode.Disabled);

		for (int i = 0; i < RowCount; i++)
		{
			int row = i;   // 闭包捕获
			HBoxContainer line = new HBoxContainer();
			line.Name = "RowLine" + row;
			line.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			line.AddThemeConstantOverride("separation", 8);
			rows.AddChild(line, false, Node.InternalMode.Disabled);

			Button b = new Button();
			b.Name = "Row" + row;
			b.AddThemeFontSizeOverride("font_size", 16);
			b.Alignment = HorizontalAlignment.Left;
			b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			b.SizeFlagsStretchRatio = 0.78f;
			b.Pressed += () => OnRowClicked(row);
			line.AddChild(b, false, Node.InternalMode.Disabled);
			_rowButtons.Add(b);

			// ★ v3.2：每行「清空」按钮 —— 一键解绑该槽/铲子键位
			Button clear = new Button();
			clear.Name = "Clear" + row;
			clear.Text = "清空";
			clear.TooltipText = "解绑" + TitleOfRow(row) + "的键位（恢复为无绑定）";
			clear.AddThemeFontSizeOverride("font_size", 14);
			clear.CustomMinimumSize = new Vector2(72f, 0f);
			clear.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
			clear.SizeFlagsStretchRatio = 0.22f;
			clear.Pressed += () => OnRowClear(row);
			line.AddChild(clear, false, Node.InternalMode.Disabled);
			_clearButtons.Add(clear);
		}

		// 底部按钮行：取消录制（仅录制态可见）+ 保存（有改动时可见）+ 关闭
		HBoxContainer footer = new HBoxContainer();
		footer.Name = "Footer";
		footer.AddThemeConstantOverride("separation", 10);
		box.AddChild(footer, false, Node.InternalMode.Disabled);

		// ★ v3.4：录制态专用「取消录制」按钮（= Esc 的按钮版；非录制态隐藏）
		Button cancel = new Button();
		cancel.Name = "CancelCapture";
		cancel.Text = "取消录制";
		cancel.TooltipText = "放弃本次录制，不修改任何键位（等同按 Esc）";
		cancel.AddThemeFontSizeOverride("font_size", 16);
		cancel.CustomMinimumSize = new Vector2(130f, 0f);
		cancel.Visible = false;
		cancel.Pressed += () =>
		{
			try
			{
				if (_captureRow >= 0)
				{
					_captureRow = -1;
					if (_tree?.Root != null && _tree.Root.HasMeta("__mod_keybind_capturing")) { _tree.Root.RemoveMeta("__mod_keybind_capturing"); }
					UpdateCaptureHint();
					RefreshPanelRows();
					Log("已取消录制。");
				}
			}
			catch { }
		};
		footer.AddChild(cancel, false, Node.InternalMode.Disabled);
		_cancelCaptureButton = cancel;

		// ★ v4.1：「保存」按钮（有暂存改动时显示；点击统一写回 keysData）
		Button save = new Button();
		save.Name = "Save";
		save.Text = "保存";
		save.TooltipText = "把面板里的键位改动写回配置（立即生效）";
		save.AddThemeFontSizeOverride("font_size", 16);
		save.CustomMinimumSize = new Vector2(130f, 0f);
		save.Visible = false;
		save.Pressed += () =>
		{
			try
			{
				SavePendingChanges();
				// 保存后面板行立即显示新键位（RefreshPanelRows 在 SavePendingChanges 里已调）
				// ★ v4.1.1：同步配置页「键位数据」的 LineEdit + 触发宿主保存链（对齐基线，免误弹）
				SyncKeysDataLineEdit(_tree.Root, ReadString(KKeysData, ""));
				PressHostSaveButton(_tree.Root);
				Log("保存完成，面板与配置页已同步。");
			}
			catch { }
		};
		footer.AddChild(save, false, Node.InternalMode.Disabled);
		_saveButton = save;

		// ★ v4.1：保存状态行（未保存改动 / 空闲说明）
		_pendingHint = new Label();
		_pendingHint.Name = "PendingHint";
		_pendingHint.Text = "点击一行 → 按下新按键（Esc 取消）；冲突键自动对调；改完点「保存」生效。";
		_pendingHint.AddThemeFontSizeOverride("font_size", 14);
		_pendingHint.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.9f));
		_pendingHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_pendingHint.CustomMinimumSize = new Vector2(440f, 0f);
		box.AddChild(_pendingHint, false, Node.InternalMode.Disabled);

		Button close = new Button();
		close.Name = "Close";
		close.Text = "关闭";
		close.AddThemeFontSizeOverride("font_size", 16);
		close.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		close.Pressed += () => { ClosePanel(); };

		// ★ v4.1：「改键面板呼出键」设置区（下拉 + 录制按钮，独立立即保存）
		HSeparator sep = new HSeparator();
		box.AddChild(sep, false, Node.InternalMode.Disabled);

		HBoxContainer pkRow = new HBoxContainer();
		pkRow.AddThemeConstantOverride("separation", 8);
		box.AddChild(pkRow, false, Node.InternalMode.Disabled);

		Label pkLabel = new Label();
		pkLabel.Text = "面板呼出键：";
		pkLabel.AddThemeFontSizeOverride("font_size", 15);
		pkRow.AddChild(pkLabel, false, Node.InternalMode.Disabled);

		OptionButton pkChoice = new OptionButton();
		pkChoice.Name = "PanelKeyChoice";
		pkChoice.AddThemeFontSizeOverride("font_size", 14);
		pkChoice.CustomMinimumSize = new Vector2(130f, 32f);
		// 选项与 mod.json panelKey 一致（无绑定 + 字母数字F键 + 常用键）
		// ★ v4.1.5：手机端没有物理键盘 ⇒ 只留音量键（用户要求；PC 端保持全量）
		string currentPk = ReadString(KPanelKey, "");
		int selectIdx = 0;
		var pkOptions = new List<string> { "" };
		if (KeybindPanelManager.IsMobileInput())
		{
			pkOptions.AddRange(new[] { "VolumeUp", "VolumeDown", "VolumeMute" });
		}
		else
		{
			for (char c = 'A'; c <= 'Z'; c++) { pkOptions.Add(c.ToString()); }
			foreach (string c in new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" }) { pkOptions.Add(c); }
			for (int i = 1; i <= 12; i++) { pkOptions.Add("F" + i); }
			pkOptions.AddRange(new[] { "Space", "Enter", "Tab", "Insert", "Delete", "Home", "End", "Pageup", "Pagedown", "Up", "Down", "Left", "Right", "Minus", "Equal", "Comma", "Period" });
			pkOptions.AddRange(new[] { "VolumeUp", "VolumeDown", "VolumeMute" });
		}
		for (int i = 0; i < pkOptions.Count; i++)
		{
			string v = pkOptions[i];
			string label = (v == "") ? "（无绑定）" : (v.StartsWith("Volume") ? v.Replace("VolumeUp", "手机音量+").Replace("VolumeDown", "手机音量-").Replace("VolumeMute", "手机静音") : v);
			pkChoice.AddItem(label);
			if (v == currentPk) { selectIdx = i; }
		}
		pkChoice.Select(selectIdx);
		pkChoice.ItemSelected += (long idxL) =>
		{
			try
			{
				string v = pkOptions[(int)idxL];
				var ch = new Dictionary<string, object>();
				ch[KPanelKey] = v;
				if (_settings != null && _settings.TrySave(ch, out XWModSettingsSaveResult r))
				{
					Log("面板呼出键已保存：" + (string.IsNullOrEmpty(v) ? "（无绑定）" : v));
				}
				// ★ v4.1.5：同步宿主配置页那一行的 enum 下拉 + 触发宿主保存链。
				//   否则用户之后点一次宿主「保存」，宿主按**索引**读下拉里的旧值 ⇒ 把刚存的覆盖回去。
				KeybindPanelManager.SyncHostRowAndSave(_tree?.Root, KPanelKey, v);
			}
			catch { }
		};
		pkRow.AddChild(pkChoice, false, Node.InternalMode.Disabled);

		Button pkRecord = new Button();
		pkRecord.Name = "RecordPanelKey";
		pkRecord.Text = "录制呼出键";
		pkRecord.TooltipText = "按下新按键即设为面板呼出键（Esc 取消）";
		pkRecord.AddThemeFontSizeOverride("font_size", 14);
		pkRecord.CustomMinimumSize = new Vector2(110f, 32f);
		pkRecord.Pressed += () =>
		{
			try
			{
				// 借用 KeybindPanelManager 的通用录制会话（目标是 panelKey）
				KeybindPanelManager.OpenFor(_tree, "卡槽与铲子快捷键", "面板呼出键",
					_context?.PackageRoot ?? "", KPanelKey, ParseKeySetting(ReadString(KPanelKey, "")),
					string.IsNullOrWhiteSpace(ReadString(KPanelKey, "")) ? "（无绑定）" : ReadString(KPanelKey, ""),
					key =>
					{
						var ch = new Dictionary<string, object>();
						ch[KPanelKey] = key ?? "";
						if (_settings != null && _settings.TrySave(ch, out XWModSettingsSaveResult r))
						{
							Log("面板呼出键已保存：" + (string.IsNullOrEmpty(key) ? "（无绑定）" : key));
						}
					});
			}
			catch { }
		};
		pkRow.AddChild(pkRecord, false, Node.InternalMode.Disabled);

		footer.AddChild(close, false, Node.InternalMode.Disabled);

		_tree.Root.AddChild(layer, false, Node.InternalMode.Disabled);
		_panelLayer = layer;
		_panelDim = dim;
		Log("已创建键位设置面板。");
	}

	private void OnRowClicked(int row)
	{
		try
		{
			if (_captureRow == row)
			{
				_captureRow = -1;   // 再点一次取消
			}
			else
			{
				_captureRow = row;
			}
			// ★ v4.0.1：录制期间挂全局元标记，其它轮询型快捷键（时停/三倍加速/CD）跳过响应
			try
			{
				if (_captureRow >= 0) { if (_tree?.Root != null) { _tree.Root.SetMeta("__mod_keybind_capturing", true); }; }
				else if (_tree?.Root != null && _tree.Root.HasMeta("__mod_keybind_capturing")) { _tree.Root.RemoveMeta("__mod_keybind_capturing"); }
			}
			catch { }
			UpdateCaptureHint();
			Log("改键录制：" + (_captureRow >= 0 ? TitleOfRow(row) + "，请按新按键（Esc 取消）" : "已取消"));
		}
		catch { }
	}

	/// <summary>★ v4.1.1：同步配置页「键位数据」的 LineEdit（宿主 string 控件显示）。v4.1.4 诊断版。</summary>
	private void SyncKeysDataLineEdit(Node root, string newValue)
	{
		try
		{
			Node reset = FindNodeByName(root, "Reset_" + KKeysData);
			GD.PrintErr("[SlotHotkeys][DIAG] SyncKeysData: reset=" + (reset != null));
			if (reset == null || !GodotObject.IsInstanceValid(reset)) { return; }
			Node row = reset.GetParent();
			GD.PrintErr("[SlotHotkeys][DIAG] row=" + (row != null ? row.Name : "null") + " type=" + (row != null ? row.GetType().Name : "?"));
			if (row == null || !GodotObject.IsInstanceValid(row)) { return; }
			LineEdit le = FindLineEdit(row);
			GD.PrintErr("[SlotHotkeys][DIAG] LineEdit found=" + (le != null) + (le != null ? " oldText=" + le.Text.Substring(0, System.Math.Min(30, le.Text.Length)) : ""));
			if (le != null && le.Text != newValue)
			{
				le.Text = newValue;
				GD.PrintErr("[SlotHotkeys][DIAG] LineEdit.Text 已更新为新值");
			}
		}
		catch (Exception ex) { GD.PrintErr("[SlotHotkeys][DIAG] SyncKeysData EX: " + ex.Message); }
	}

	private LineEdit FindLineEdit(Node node)
	{
		try
		{
			if (node is LineEdit le) { return le; }
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				LineEdit r = FindLineEdit(node.GetChild(i));
				if (r != null) { return r; }
			}
		}
		catch { }
		return null;
	}

	/// <summary>★ v4.1.1：模拟按下宿主配置页「保存」按钮（Name=SaveSettings），
	/// 走完整 reader→Draft→Save 链对齐基线（关闭配置页不误弹「放弃修改」）。</summary>
	private void PressHostSaveButton(Node root)
	{
		try
		{
			Node saveBtn = FindNodeByName(root, "SaveSettings");
			GD.PrintErr("[SlotHotkeys][DIAG] SaveBtn found=" + (saveBtn != null) + (saveBtn is Button b2 ? " disabled=" + b2.Disabled : ""));
			if (saveBtn is Button b && GodotObject.IsInstanceValid(b))
			{
				b.EmitSignal(BaseButton.SignalName.Pressed);
				GD.PrintErr("[SlotHotkeys][DIAG] 宿主保存按钮已按下（EmitSignal Pressed）");
			}
		}
		catch { }
	}

	/// <summary>★ v4.1：一键解绑某行键位（入暂存，点「保存」统一写回）。</summary>
	private void OnRowClear(int row)
	{
		try
		{
			Key old = KeyOfRow(row);
			if (old == Key.None)
			{
				Log(TitleOfRow(row) + " 本来就是无绑定。");
				return;
			}
			_captureRow = -1;   // 若正在录制此行，一并取消
			if (_tree?.Root != null && _tree.Root.HasMeta("__mod_keybind_capturing")) { _tree.Root.RemoveMeta("__mod_keybind_capturing"); }
			UpdateCaptureHint();
			MarkPending(row, Key.None);
			Log("待保存：" + TitleOfRow(row) + " → 无绑定（原 " + KeyToDisplay(old) + "）");
			RefreshPanelRows();
		}
		catch (Exception ex) { Warn("清空键位失败：" + ex.GetBaseException().Message); }
	}

	// ── v4.1：暂存-保存模式 ──────────────────────────────────────────
	/// <summary>录制到的待保存改动：row → 新键（null = 无待保存改动）。点「保存」统一写回。</summary>
	private Dictionary<int, Key> _pendingChanges;
	private Label _pendingHint;   // 底部保存状态行

	private bool HasPending
	{
		get { return _pendingChanges != null && _pendingChanges.Count > 0; }
	}

	private void MarkPending(int row, Key k)
	{
		try
		{
			if (_pendingChanges == null) { _pendingChanges = new Dictionary<int, Key>(); }
			_pendingChanges[row] = k;
			UpdatePendingHint();
		}
		catch { }
	}

	private void UpdatePendingHint()
	{
		try
		{
			if (_pendingHint == null || !GodotObject.IsInstanceValid(_pendingHint)) { return; }
			if (HasPending)
			{
				var parts = new List<string>();
				foreach (KeyValuePair<int, Key> kv in _pendingChanges)
				{
					parts.Add(TitleOfRow(kv.Key) + "=" + KeyToDisplay(kv.Value));
				}
				_pendingHint.Text = "● 未保存改动：" + string.Join("、", parts) + " —— 点「保存」写回配置（关闭=放弃）";
				_pendingHint.AddThemeColorOverride("font_color", new Color(0.5f, 0.9f, 1f));
			}
			else
			{
				_pendingHint.Text = "点击一行 → 按下新按键（Esc 取消）；冲突键自动对调；改完点「保存」生效。";
				_pendingHint.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.9f));
			}
		}
		catch { }
	}

	/// <summary>把暂存改动统一写回 keysData（保存按钮）。冲突对调在此统一处理。</summary>
	private void SavePendingChanges()
	{
		try
		{
			if (!HasPending) { return; }
			EnsureKeysCache();
			// ★ v4.1.4：**清空操作不做冲突对调**（用户反馈：清空就是清空，键不应挪到别的槽）。
			//   只有「录制新键」与其他行冲突时才对调。
			foreach (KeyValuePair<int, Key> kv in _pendingChanges)
			{
				if (kv.Value != Key.None)
				{
					Key oldOfRow = KeyOfRow(kv.Key);
					for (int other = 0; other < RowCount; other++)
					{
						if (other == kv.Key) { continue; }
						if (_pendingChanges.ContainsKey(other)) { continue; }   // 也被改了，让它自己生效
						if (KeyOfRow(other) != kv.Value) { continue; }
						if (other == 16) { _shovelKeyCache = oldOfRow; }
						else { _keysCache[other] = oldOfRow; }
						Log("冲突对调：" + TitleOfRow(other) + " ← " + KeyToDisplay(oldOfRow));
						break;
					}
				}
			}
			foreach (KeyValuePair<int, Key> kv in _pendingChanges)
			{
				if (kv.Key == 16) { _shovelKeyCache = kv.Value; }
				else { _keysCache[kv.Key] = kv.Value; }
			}
			_pendingChanges = null;
			UpdatePendingHint();
			RefreshPanelRows();
			// ★ v4.1.3 终版：按上下文分两条保存路径——
			//   A) 配置页开着（能找到 Reset_keysData）⇒ 走宿主管道：新 keysData 写进
			//      LineEdit（宿主 reader 读 Text）→ 触发宿主「保存」按钮 → 完整保存链
			//      落盘+基线对齐（直接 TrySave 会改 fingerprint ⇒ 宿主保存被拒，弹红字）。
			//   B) 配置页没开（战斗中呼出面板）⇒ 无 UI 冲突 ⇒ 直接 TrySave 落盘。
			Node resetNode = FindNodeByName(_tree.Root, "Reset_" + KKeysData);
			bool cfgPageOpen = resetNode != null && GodotObject.IsInstanceValid(resetNode);
			string newData = BuildKeysData();
			if (cfgPageOpen)
			{
				SyncKeysDataLineEdit(_tree.Root, newData);
				PressHostSaveButton(_tree.Root);
				Log("键位已通过配置页保存链写回。");
			}
			else
			{
				var ch = new Dictionary<string, object>();
				ch[KKeysData] = newData;
				XWModSettingsSaveResult r = null;
				if (_settings != null && _settings.TrySave(ch, out r))
				{
					Log("键位已保存：" + r.Message);
				}
				else
				{
					Warn("键位保存失败：" + (r != null ? r.Message : "配置不可用"));
				}
			}
		}
		catch (Exception ex) { Warn("保存键位失败：" + ex.GetBaseException().Message); }
	}

	/// <summary>★ v3.2：赞助弹窗 —— 从包 Resources/ 加载收款码大图显示。</summary>
	private void OpenSponsorPopup()
	{
		try
		{
			if (_sponsorLayer != null && GodotObject.IsInstanceValid(_sponsorLayer))
			{
				_sponsorLayer.Visible = true;
				return;
			}

			CanvasLayer layer = new CanvasLayer();
			layer.Name = "ModSponsorLayer";
			layer.Layer = 210;
			layer.ProcessMode = Node.ProcessModeEnum.Always;   // 设置页暂停树下也能显示/关闭

			ColorRect dim = new ColorRect();
			dim.Color = new Color(0f, 0f, 0f, 0.6f);
			dim.MouseFilter = Control.MouseFilterEnum.Stop;
			dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			layer.AddChild(dim, false, Node.InternalMode.Disabled);

			CenterContainer center = new CenterContainer();
			center.MouseFilter = Control.MouseFilterEnum.Ignore;
			center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			dim.AddChild(center, false, Node.InternalMode.Disabled);

			VBoxContainer box = new VBoxContainer();
			box.AddThemeConstantOverride("separation", 10);
			center.AddChild(box, false, Node.InternalMode.Disabled);

			Label title = new Label();
			title.Text = "微信扫码 · 赞助作者";
			title.AddThemeFontSizeOverride("font_size", 22);
			title.AddThemeColorOverride("font_color", new Color(0.3f, 0.9f, 0.4f));
			title.HorizontalAlignment = HorizontalAlignment.Center;
			box.AddChild(title, false, Node.InternalMode.Disabled);

			TextureRect qr = new TextureRect();
			Texture2D tex = LoadPackageTexture("Resources/SponsorQR.png");
			if (tex != null)
			{
				qr.Texture = tex;
				// 原图 1267×1723 ⇒ 显示成 380×517 左右（ExpandMode 忽略原尺寸）
				qr.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
				qr.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
				qr.CustomMinimumSize = new Vector2(380f, 517f);
			}
			else
			{
				Label miss = new Label();
				miss.Text = "（收款码图片缺失：Resources/SponsorQR.png）";
				miss.AddThemeFontSizeOverride("font_size", 16);
				box.AddChild(miss, false, Node.InternalMode.Disabled);
			}
			box.AddChild(qr, false, Node.InternalMode.Disabled);

			Button close = new Button();
			close.Text = "关闭";
			close.CustomMinimumSize = new Vector2(140f, 44f);
			close.AddThemeFontSizeOverride("font_size", 18);
			close.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
			close.Pressed += () =>
			{
				try { if (_sponsorLayer != null && GodotObject.IsInstanceValid(_sponsorLayer)) { _sponsorLayer.Visible = false; } } catch { }
			};
			box.AddChild(close, false, Node.InternalMode.Disabled);

			_tree.Root.AddChild(layer, false, Node.InternalMode.Disabled);
			_sponsorLayer = layer;
			Log("已打开赞助弹窗。");
		}
		catch (Exception ex) { Warn("打开赞助弹窗失败：" + ex.GetBaseException().Message); }
	}

	/// <summary>从 Mod 包 Resources/ 目录加载贴图（运行时直读文件，不走 res://；带缓存）。</summary>
	private Texture2D LoadPackageTexture(string relativePath)
	{
		try
		{
			if (_qrTexture != null && GodotObject.IsInstanceValid(_qrTexture)) { return _qrTexture; }
			string root = null;
			try { root = _context.PackageRoot; } catch { }
			if (string.IsNullOrEmpty(root)) { return null; }
			string path = System.IO.Path.Combine(root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
			if (!System.IO.File.Exists(path)) { WarnOnce("tex_" + relativePath, "贴图不存在：" + path); return null; }
			Image img = Image.LoadFromFile(path);   // Godot 4 直读 PNG/JPG/WebP
			if (img == null) { WarnOnce("tex_load", "贴图解码失败：" + path); return null; }
			_qrTexture = ImageTexture.CreateFromImage(img);
			return _qrTexture;
		}
		catch (Exception ex) { WarnOnce("tex_load", "加载贴图失败：" + ex.Message); return null; }
	}

	// ============================================================ 配置页「关于」栏收款码（v3.4 改走共享组件）

	// ── 配置页「打开改键面板」按钮（挂在 Reset_keysData 同一行，v4.1 按用户要求）──
	private const string PanelKeyResetName = "Reset_keysData";   // ★ v4.1：锚改到「键位数据」行
	private const string ConfigBtnName = "ModOpenKeyPanelButton";
	private int _qrTick;
	private int _cfgBtnTick;

	/// <summary>★ v3.4：收款码显示已切换到共享组件 SponsorQrInjector（带归属判定），本文件不再内置注入逻辑。</summary>
	private void TickAboutQrImage()
	{
		try
		{
			SponsorQrInjector.Tick(_tree, "卡槽与铲子快捷键", _context?.PackageRoot ?? "", "Resources/SponsorQR.png");
		}
		catch (Exception ex) { WarnOnce("aboutqr", "关于栏注入收款码失败：" + ex.GetBaseException().Message); }
	}

	// ============================================================ 配置页「打开改键面板」按钮

	/// <summary>
	/// ★ v4.1：在**本 Mod 的配置页**里加「改键面板」按钮——锚改到「键位数据」行
	/// （Reset_keysData，用户要求）；按钮插到重置图标前（同一行内）。
	/// 配置页每次打开重建 ⇒ 低频扫描 + 幂等。
	/// </summary>
	private void TickConfigPagePanelButton()
	{
		try
		{
			Node reset = FindNodeByName(_tree.Root, PanelKeyResetName);
			if (reset == null || !GodotObject.IsInstanceValid(reset)) { return; }   // 配置页没开/不是本 Mod 页
			Node row = reset.GetParent();
			if (row == null || !GodotObject.IsInstanceValid(row) || row.GetNodeOrNull(ConfigBtnName) != null) { return; }

			Button b = new Button();
			b.Name = ConfigBtnName;
			b.Text = "改键面板";
			b.TooltipText = "打开「卡槽与铲子快捷键」的键位设置面板（点一行 → 按新键）";
			b.AddThemeFontSizeOverride("font_size", 14);
			b.CustomMinimumSize = new Vector2(88f, 36f);
			b.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
			b.Pressed += () =>
			{
				try { TogglePanel(); } catch { }
			};
			row.AddChild(b, false, Node.InternalMode.Disabled);
			row.MoveChild(b, reset.GetIndex());   // 放在重置按钮前面
			Log("已在配置页加入「改键面板」按钮。");
		}
		catch (Exception ex) { WarnOnce("cfgbtn", "配置页按钮注入失败：" + ex.GetBaseException().Message); }
	}

	/// <summary>
	/// ★ v4.1.5（用户要求）：手机端没有物理键盘 ⇒ 把**宿主配置页**里 panelKey 那个
	/// 70+ 项的下拉隐藏掉（本 Mod 自己的改键面板里自带"只含音量键"的下拉 + 录制按钮）。
	///
	/// ⚠️ 只能"隐藏"，**不能重建/删项**：宿主 enum 读取器是
	/// `_playerSettingsReaders[key] = () => definition.Options[choice.Selected].Value;`
	/// （XWModToolsPanel.cs）—— 按**索引**映射回宿主自己的选项数组，
	/// 动了下拉项就会写进别的值。
	/// </summary>
	private void TickMobileHidePanelKeyDropdown()
	{
		try
		{
			if (!KeybindPanelManager.IsMobileInput()) { return; }
			Node reset = FindNodeByName(_tree?.Root, "Reset_" + KPanelKey);
			if (reset == null || !GodotObject.IsInstanceValid(reset)) { return; }
			Node row = reset.GetParent();
			if (row == null || !GodotObject.IsInstanceValid(row)) { return; }
			OptionButton ob = FindOptionButtonIn(row);
			if (ob != null && ob.Visible) { ob.Visible = false; }
		}
		catch { }
	}

	/// <summary>递归找第一个 OptionButton（宿主 enum 项就在这一行里）。</summary>
	private static OptionButton FindOptionButtonIn(Node node)
	{
		try
		{
			if (node is OptionButton ob) { return ob; }
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				OptionButton r = FindOptionButtonIn(node.GetChild(i));
				if (r != null) { return r; }
			}
		}
		catch { }
		return null;
	}

	/// <summary>按节点名递归找节点。</summary>
	private static Node FindNodeByName(Node root, string name)
	{
		try
		{
			if (root == null || !GodotObject.IsInstanceValid(root)) { return null; }
			if (root.Name == name) { return root; }
			int n = root.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				Node r = FindNodeByName(root.GetChild(i), name);
				if (r != null) { return r; }
			}
		}
		catch { }
		return null;
	}
	private void UpdateCaptureHint()
	{
		try
		{
			if (_captureHint == null || !GodotObject.IsInstanceValid(_captureHint)) { return; }
			if (_captureRow >= 0)
			{
				_captureHint.Text = "▶ 正在录制【" + TitleOfRow(_captureRow) + "】：按下任意按键（Esc 取消）…";
				_captureHint.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f));
			}
			else
			{
				_captureHint.Text = "点击一行 → 按下新按键（Esc 取消）；冲突键自动对调；保存后立即生效。";
				_captureHint.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.9f));
			}
			// 「取消录制」按钮跟随录制态显隐
			if (_cancelCaptureButton != null && GodotObject.IsInstanceValid(_cancelCaptureButton))
			{
				bool vis = _captureRow >= 0;
				if (_cancelCaptureButton.Visible != vis) { _cancelCaptureButton.Visible = vis; }
			}
		}
		catch { }
	}

	private void RefreshPanelRows()
	{
		try
		{
			if (_panelLayer == null || !GodotObject.IsInstanceValid(_panelLayer)) { return; }
			for (int row = 0; row < RowCount && row < _rowButtons.Count; row++)
			{
				// ★ v4.1：有暂存改动时显示**暂存值**（预览），并加 ● 标记
				Key showKey = KeyOfRow(row);
				bool pending = false;
				Key pk = Key.None;
				if (_pendingChanges != null && _pendingChanges.TryGetValue(row, out pk))
				{
					pending = true;
					showKey = pk;
				}
				string txt = TitleOfRow(row) + "  ▸  " + KeyToDisplay(showKey);
				if (_captureRow == row) { txt += "   （按任意键…）"; }
				else if (pending) { txt += "   ●"; }
				_rowButtons[row].Text = txt;
				// 清空按钮：无绑定时禁用，避免误点刷日志
				if (row < _clearButtons.Count && _clearButtons[row] != null && GodotObject.IsInstanceValid(_clearButtons[row]))
				{
					bool bound = showKey != Key.None;
					if (_clearButtons[row].Disabled == bound) { _clearButtons[row].Disabled = !bound; }
				}
			}
			// 保存按钮：有暂存改动时显示
			if (_saveButton != null && GodotObject.IsInstanceValid(_saveButton))
			{
				bool vis = HasPending;
				if (_saveButton.Visible != vis) { _saveButton.Visible = vis; }
			}
			UpdatePendingHint();
		}
		catch { }
	}

	/// <summary>WindowInput：只在录制态消费按键。</summary>
	private void OnWindowInput(InputEvent ev)
	{
		try
		{
			if (_captureRow < 0) { return; }
			if (ev is not InputEventKey ke) { return; }
			if (ke.Echo || !ke.Pressed) { return; }
			Key k = ke.PhysicalKeycode != Key.None ? ke.PhysicalKeycode : ke.Keycode;

			// 纯修饰键：忽略，等真正的键
			if (k == Key.Shift || k == Key.Ctrl || k == Key.Alt || k == Key.Meta)
			{
				_tree.Root.SetInputAsHandled();
				return;
			}

			int target = _captureRow;
			_captureRow = -1;
			if (_tree?.Root != null && _tree.Root.HasMeta("__mod_keybind_capturing")) { _tree.Root.RemoveMeta("__mod_keybind_capturing"); }
			_tree.Root.SetInputAsHandled();   // 吞掉这次按键，游戏/其它 Mod 不响应
			UpdateCaptureHint();

			if (k == Key.Escape)
			{
				Log("改键已取消（" + TitleOfRow(target) + "）。");
				RefreshPanelRows();
				return;
			}

			ApplyCapturedKey(target, k);
			RefreshPanelRows();
		}
		catch (Exception ex) { WarnOnce("capture", "录制按键失败：" + ex.GetBaseException().Message); }
	}

	/// <summary>★ v4.1：把录到的键**入暂存**（点「保存」统一写回）；冲突对调也在保存时一并生效。</summary>
	private void ApplyCapturedKey(int row, Key k)
	{
		try
		{
			// 冲突对调信息在保存时计算（RefreshPanelRows 里能看到预览提示）
			MarkPending(row, k);
			Log("待保存：" + TitleOfRow(row) + " = " + KeyToDisplay(k));
		}
		catch (Exception ex) { Warn("应用录制键失败：" + ex.GetBaseException().Message); }
	}

	// ============================================================ 卡槽

	/// <summary>键位是否可用于触发：CD 完成（!coldDownOpen）且可用（alive）。</summary>
	private static bool CardReady(TowerDefenseInGamePacketShow card)
	{
		try
		{
			if (card.coldDownOpen) { return false; }
			if (!card.alive) { return false; }
			return true;
		}
		catch { return true; }
	}

	private void UpdateCard(TowerDefenseInGamePacketShow card, Key key, bool onlyReady)
	{
		try
		{
			ulong id = card.GetInstanceId();
			if (!_labels.TryGetValue(id, out Label label) || label == null || !GodotObject.IsInstanceValid(label) || label.GetParent() != card)
			{
				label = MakeLabel(card, "ModHotkeyLabel");
				if (label == null) { return; }
				_labels[id] = label;
			}

			string txt = KeyToDisplay(key);
			if (label.Text != txt) { label.Text = txt; }

			// 跟随卡片尺寸（PC 50×70 / 手机 96×60，切换模式时 Size 会变）
			Vector2 sz = card.Size;
			label.Position = new Vector2(Mathf.Max(sz.X - 16f, 2f), Mathf.Max(sz.Y - 20f, 2f));

			bool show = onlyReady ? CardReady(card) : true;
			if (label.Visible != show) { label.Visible = show; }

			// 按下沿检测 → 走游戏原生 Pressed()（内部自带 CD/lock/onlyDraw 门槛与音效）
			bool down = IsKeyDown(key);
			bool was = _prevDown.Contains(id);
			if (down && !was && CardReady(card))
			{
				Log("快捷键 " + txt + " → 卡槽" + (card.config != null ? card.config.saveKey : "?"));
				card.Pressed();
			}
			if (down) { _prevDown.Add(id); } else { _prevDown.Remove(id); }
		}
		catch (Exception ex)
		{
			WarnOnce("card" + key, "处理卡槽失败：" + ex.GetBaseException().Message);
		}
	}

	private Label MakeLabel(Node host, string name)
	{
		try
		{
			Label l = new Label();
			l.Name = name;
			l.MouseFilter = Control.MouseFilterEnum.Ignore;   // 不吃点击
			l.AddThemeFontSizeOverride("font_size", 15);
			l.AddThemeColorOverride("font_color", new Color(1f, 0.92f, 0.25f));
			l.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
			l.AddThemeConstantOverride("outline_size", 4);
			host.AddChild(l, false, Node.InternalMode.Disabled);
			return l;
		}
		catch (Exception ex)
		{
			WarnOnce("label" + name, "建标签失败：" + ex.GetBaseException().Message);
			return null;
		}
	}

	private static bool IsKeyDown(Key key)
	{
		try { return key != Key.None && Input.IsKeyPressed(key); }
		catch { return false; }
	}

	/// <summary>清掉本帧没见到的按下沿记录（卡片被回收/换卡时防脏数据）。</summary>
	private void PrunePrevDown(HashSet<ulong> aliveIds)
	{
		try
		{
			if (_prevDown.Count == aliveIds.Count) { return; }
			_prevDown.RemoveWhere(id => !aliveIds.Contains(id));
		}
		catch { }
	}

	// ============================================================ 铲子

	/// <summary>铲子：找树里的 ShovelManager（Control），标签挂 %ShovelButton 上，一直显示。</summary>
	private void UpdateShovel(TowerDefenseInGameSeedBank bank)
	{
		try
		{
			Key key = KeyOfRow(16);
			if (key == Key.None) { return; }

			if (_shovelManager == null || !GodotObject.IsInstanceValid(_shovelManager) || !_shovelManager.IsInsideTree())
			{
				_shovelManager = FindByTypeName<ShovelManager>(_tree.Root);
				_shovelLabel = null;
				if (_shovelManager == null) { return; }
			}

			TextureButton btn = _shovelManager.shovelButton;
			if (btn == null || !GodotObject.IsInstanceValid(btn)) { return; }

			if (_shovelLabel == null || !GodotObject.IsInstanceValid(_shovelLabel) || _shovelLabel.GetParent() != btn)
			{
				_shovelLabel = MakeLabel(btn, "ModShovelKeyLabel");
				if (_shovelLabel == null) { return; }
				Log("已给铲子挂上键位标签：" + KeyToDisplay(key));
			}

			string txt = KeyToDisplay(key);
			if (_shovelLabel.Text != txt) { _shovelLabel.Text = txt; }
			Vector2 sz = btn.Size;
			_shovelLabel.Position = new Vector2(Mathf.Max(sz.X - 16f, 2f), Mathf.Max(sz.Y - 20f, 2f));
			if (!_shovelLabel.Visible) { _shovelLabel.Visible = true; }

			bool down = IsKeyDown(key);
			if (down && !_shovelPrevDown)
			{
				try
				{
					btn.ButtonPressed = !btn.ButtonPressed;   // ★ 照抄游戏 _Input 原生路径
					_shovelManager.ShovelButtonPressed();
					Log("快捷键 " + txt + " → 铲子");
				}
				catch (Exception ex) { WarnOnce("shovelpress", "触发铲子失败：" + ex.GetBaseException().Message); }
			}
			_shovelPrevDown = down;
		}
		catch { }
	}

	// ============================================================ 工具

	/// <summary>在子树里按类型找节点（泛型直调版）。</summary>
	private static T FindByTypeName<T>(Node root) where T : Node
	{
		try
		{
			if (root == null || !GodotObject.IsInstanceValid(root)) { return null; }
			if (root is T hit) { return hit; }
			int n = root.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				T r = FindByTypeName<T>(root.GetChild(i));
				if (r != null) { return r; }
			}
		}
		catch { }
		return null;
	}

	private void ClearLabels()
	{
		try
		{
			foreach (KeyValuePair<ulong, Label> kv in _labels)
			{
				Label l = kv.Value;
				if (l != null && GodotObject.IsInstanceValid(l)) { l.QueueFree(); }
			}
		}
		catch { }
		_labels.Clear();
		_prevDown.Clear();
		_shovelManager = null;
		_shovelLabel = null;
		_shovelPrevDown = false;
		_battleLogged = false;
	}

	private void ClearAll()
	{
		ClearLabels();
		_panelPrevDown = false;
	}

	private readonly HashSet<string> _warnedOnce = new HashSet<string>();

	private void WarnOnce(string tag, string msg)
	{
		// ★ 2026-10-14：原先绕过日志开关，现统一受 enableLog 控制
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KLog, false)） */
		{
			return;
		}
		if (_warnedOnce.Add(tag)) { try { GD.PrintErr(P + msg); } catch { } }
	}

	private void Log(string msg)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KLog, false)） */ { return; }
		try { GD.Print(P + msg); } catch { }
	}

	private void Warn(string msg)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KLog, false)） */ { return; }
		// ★ 2026-10-14：该输出原先绕过日志开关，现统一受 enableLog 控制
		try { GD.PrintErr(P + msg); } catch { }
	}
}
