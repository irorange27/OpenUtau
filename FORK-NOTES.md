# FORK-NOTES.md — 本 fork 的基线与计划

上游：<https://github.com/openutau/OpenUtau>
本 fork：<https://github.com/irorange27/OpenUtau>
基点：`8879fbe`（master）

---

## 这个 fork 要做什么

给 OpenUtau 加**从 DAW 接收 MIDI 音符**的能力，实现「在 Logic 里画音符 → OpenUtau 唱出来」。

这是与 <https://github.com/irorange27/openutau-logic-au>（AU 侧）**配套**的改动。
两个仓库必须一起改，协议定义见该仓库的 `PROTOCOL.md`。

**当前状态：尚未开始改动。** 本文件先记录基线，便于日后对比。

---

## 上游把这条路封着

`OpenUtau.Core/DawIntegration/API.md` §6.3 原文：

> ### 6.3 MIDI Input (Reserved — Not in This Version)
>
> A future message family (e.g. `notification:midiNotes`, `request:recordMidi`) is reserved.
> Implementations **MUST NOT** invent wire shapes for this direction under the v1 namespace.

**所以本 fork 必须按上游的版本策略来做，不能私自扩展 v1：**

`API.md` §10 的兼容策略：

- **Append-only**：新的 minor 版本只能**增加**消息/字段，**不得**改变已有字段的含义
- **Kind 命名空间**：语义变化的消息要用新 kind 名（如 `updatePartLayoutV2`），不能原地改
- **未知字段容忍**：接收方必须忽略不认识的字段 —— 这正是 minor 差异安全的原因
- **版本标识**：在 discovery 文件和 `init` 响应里

版本兼容只看 **major**（`DawMessages.cs:60` `IsCompatibleWith` 只比 Major）。
所以加 MIDI 方向应该是 **1.2 → 1.3**（新增 kind），而不是塞进 v1 命名空间。

> ⚠️ 这条是有代价的：**改了 minor 之后，本 fork 的 AU 与上游 OpenUtau 之间会互相不认识这个新消息**，
> 但因为是"未知 kind 忽略"，降级是安全的 —— 老版本只是收不到音符，不会崩。

---

## 基线验证（改代码前）

环境：

| 项 | 值 |
|---|---|
| .NET SDK | **10.0.401**（Homebrew `dotnet` formula，非 `dotnet@8`） |
| 目标框架 | `net10.0` |
| 构建 | `dotnet build OpenUtau.Core/OpenUtau.Core.csproj` → **0 error**（1212 warning，全是上游既有的） |
| 测试 | **628 通过 / 0 失败 / 2 跳过**（49.8s） |

### ⚠️ 构建这个仓库需要 .NET 10，本机默认不是

本机原本只有 `dotnet@8`，而 OpenUtau 要求 `net10.0`，直接用会报：

```
error NETSDK1045: The current .NET SDK does not support targeting .NET 10.0.
```

已安装 Homebrew 的 `dotnet`（10.0.401）。但 `dotnet@8` 的 bin 在 PATH 里**遮蔽**了它，
所以每次构建都要显式指定：

```sh
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
export PATH="/opt/homebrew/opt/dotnet/bin:$PATH"
```

（或者 `brew link dotnet` 切换默认版本，但那会影响你其他用到 .NET 8 的项目 —— 不建议。）

### 怎么跑测试

**不要用 `dotnet test`** —— .NET 10 的 SDK 已经不支持 VSTest 目标，会报：

```
error : Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later.
```

按上游 CI（`.github/workflows/pr-test.yml:36`）的方式跑：

```sh
dotnet run --project OpenUtau.Test/OpenUtau.Test.csproj
```

输出末尾会给出 `TEST EXECUTION SUMMARY`。跳过 2 个测试是正常的 —— 它们需要外部资源
（`OPENUTAU_TEST_SINGERS` 声音库 / `OPENUTAU_BRIDGE_DISCOVERY` 运行中的桥接）。

---

## 相关代码位置（我读过并确认的）

| 文件 | 作用 | 行数 |
|---|---|---|
| `OpenUtau.Core/DawIntegration/DawManager.cs` | 主逻辑：连接、同步、收发分发 | 1176 |
| `OpenUtau.Core/DawIntegration/DawMessages.cs` | 消息 kind 常量 + payload 类 | 270 |
| `OpenUtau.Core/DawIntegration/DawTransport.cs` | TCP 传输、请求/响应 | 571 |
| `OpenUtau.Core/DawIntegration/DawServerFinder.cs` | 扫描 discovery 文件 | 185 |
| `OpenUtau.Core/DawIntegration/DawAudio.cs` | 音频缓存/哈希 | 199 |
| `OpenUtau.Core/DawIntegration/API.md` | 协议文档（与 AU 侧 PROTOCOL.md 对应） | — |

**关键扩展点**：

1. **消息 kind 常量** — `DawMessages.cs:11` `DawMessageKind` 类
2. **通知分发** — `DawManager.cs:925` `OnPluginNotification()` 的 switch，
   已有 `Ping / PlaybackStarted / Playhead / Bpm`，`default` 分支会记录 "ignoring unknown notification"
3. **写音符** — `OpenUtau.Core/Commands/NoteCommands.cs:25` `AddNoteCommand`
   （构造入参 `UVoicePart part, UNote note`，`Execute()` 里 `Part.notes.Add(note)`）
4. **在文档线程上改工程** — `DawManager.cs:962` 的 `OnDocumentThreadAsync()` 模式

**测试**：`OpenUtau.Test/Core/DawIntegration/` 有 8 个文件、1583 行，
含 `DawTestPlugin.cs`（模拟插件端）和 `DawConformanceTest.cs`（协议一致性）。
新功能应当照这个模式加测试。

---

## 两个已知难点（设计时必须处理）

### 1. 时序错位

Logic 送来的音符是**宿主时间轴上的位置**（小节/拍，或毫秒），
而 USTX 用**绝对 tick + 工程自己的 tempo**。

`API.md` §6.2 明确把 tempo-map 双向同步列为 **non-goal**，
所以两边速度不一致时，音符位置会**音乐性地错位**（不是崩溃，是跑偏）。

**必须决定**：按毫秒转换（`timeAxis.MsPosToTickPos`，已有此 API，见 `DawManager.cs:964`），
还是要求两边 tempo 一致（并在不一致时拒绝或警告）。

### 2. 去重

Logic 每次播放、每次循环都会**反复送**同一批音符。
不能每次都往 USTX 写，否则 OpenUtau 里会堆满重复音符。

**必须决定**：按什么维度去重（播放头位置？音符指纹？还是要求用户显式"录制"一段区间）。

---

## 歌词怎么处理

MIDI 音符**表达不了歌词**（MIDI 只有音高/时长/力度）。
已决定：DAW 侧默认用**占位音节**（如 "la"），用户回 OpenUtau 再填真词。

---

## 与 AU 侧的配合

| 仓库 | 角色 |
|---|---|
| `irorange27/openutau-logic-au` | AU 插件 + relay（C++ / CMake） |
| `irorange27/OpenUtau`（本仓库） | 编辑器侧（C# / .NET 10） |

协议文档在**两个仓库里各有一份**，必须同步修改：

- AU 侧：`PROTOCOL.md`（仓库根目录）
- 本仓库：`OpenUtau.Core/DawIntegration/API.md`

> AU 侧已完成的准备工作（在 `openutau-logic-au` 里）：
> ① 声明 note 端口让宿主送 MIDI ② 捕获音符事件（仅记录，尚未发送）。
> 实测证据：`auval -v aumu OuBg OuBr` 会打印
> `note: off C4 vel=0.000 offset=0 (2 since last report)`。

---

## 与上游同步

```sh
git fetch upstream
git rebase upstream/master
```

因为改动会集中在 `DawIntegration/` 目录，冲突面应该可控。
但注意本仓库是 **shallow clone**（`--depth 1`），rebase 前可能需要
`git fetch --unshallow` 才能拿到完整历史。
