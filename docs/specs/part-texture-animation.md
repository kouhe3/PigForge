# Spec: 部件贴图动画（本机）

> 状态：待确认（2026-09-09）。消费 `docs/intent/part-texture-animation.md`。
> 调研依据：BPLE 原作 `Assets/Scripts/Assembly-CSharp/{SpriteAnimation,FanPropeller,CartWheel,Pig}.cs` + `Assets/GameObject/Part_*.prefab`；`c:/tmp/badpiggies-editor` 的 wgpu 渲染器（`crates/renderer/src/renderer/particles/{fan,mod}.rs`、`compounds.rs`）。
> 权威契约：ADR-003（贴图资产不入库、清单可选、缺失回退形状渲染）、ADR-005（形状来自 BPLE 碰撞体）。本文件只补「贴图动画」缺口。
> 零协议改动：PGFS v3 / PGFC v2 不变；动画不进回放、不进状态哈希、不上行。

## Capability Map

| Module id | Responsibility | Depends on |
|---|---|---|
| anim-manifest | `tools/bple-textures/extract.mjs` 从 BPLE prefab 提取动画描述符 → `part-textures.json` v3；`renderer/atlas.ts` 解析/校验 | — |
| anim-clock | 动画时钟与运行态门控（`renderer/animation/clock.ts` + `App.vue` 接线） | — |
| anim-spin | 旋转运行时：风扇/螺旋桨/旋翼（开关状态机 + 透视压缩）。轮子不需要动画代码——物理铰链让刚体真旋转，快照 `rotation` 直接驱动贴图（ADR-008） | anim-manifest, anim-clock |
| anim-frames | 逐帧运行时：帧表播放器 + 猪/猪王表情触发规则 | anim-manifest, anim-clock |

Build order: `anim-manifest` → `anim-clock` → (`anim-spin` ∥ `anim-frames`)。

并行边界：`animation/spin.ts` 与 `animation/frames.ts` 各自独立（互不 import）；共享的只有 `animation/index.ts`、`renderer/draw.ts`、`App.vue` 三处接线，由同一个 owner 收口。

## Assumptions（写进规格，实现不得另猜）

1. **动画是纯客户端表现**：服务端只发状态（位姿、速度、`flags` 开关位）。不新增快照字段、不改 PGFC、不写回放、不进 `ComputeStateHash`。
2. **动画数据进贴图清单，不进内容文档**：`content/parts.json`（`part-content-v1`）保持不含贴图/动画字段（ADR-003 决策 6）。动画描述符是渲染层可选叠加，干净检出没有贴图时行为与本切片前逐字一致。
3. **原作语义是唯一权威**：转速、衰减、旋转轴、表情阈值全部照抄 BPLE prefab/脚本常量；badpiggies-editor 只作为交叉验证（它的风扇 600°/s + 2s 惯性停转与原作者约 2.5s 停转同量级；它的 `|cos|` 透视压缩与原作者一致）。
4. **原作没有的动画不做**：TNT 闪烁、气球帧动画、引擎火焰帧序列在原作中**不存在**（`TNT.cs` 无 Update；`Balloon.cs` 只有物理；`JetEngine` 是程序化缩放抖动）。PigForge 不发明。
5. **时间源**：`requestAnimationFrame` 墙钟差分，单帧 `dt` 上限 0.1s；只有「运行中」推进，建造/暂停/预览件一律冻结（原作 `Time.timeScale = 0` 语义）。
6. **硬切、无插值**：原作帧动画是 mesh 指针替换（无交叉淡化），旋转是每帧硬写 `localRotation`。PigForge 沿用硬切。
7. **回退**：清单缺失、`schemaVersion` 不支持、单个部件没有动画描述符 → 该部件静态渲染；解析抛错 → 整个贴图层回退形状渲染（ADR-003 既有路径）。

## Objective

`clients/web` 在保持服务端权威与零预测的前提下，把原作的部件动画搬回来：

- 玩家打开风扇/螺旋桨/旋翼开关 → 叶片立刻满速「转」；关掉 → 按原作指数衰减约 2.5s 停住。
- 轮子真滚动：车轮是自己的刚体、用铰链挂在车体上（ADR-008），渲染层直接按快照的 `yaw` 画精灵，无需本地推算转速。
- 猪在静止时随机眨眼；速度快时露齿笑/惊恐；速度突变时受击表情。
- 建造期、回放暂停、拖动预览件（`bodyId === 0`）不动画；Start/RESET 后动画相位复位。

## 原作语义（照抄清单）

| 机制 | 原作实现 | 关键常量 | PigForge 对应 |
|---|---|---|---|
| 风扇/螺旋桨/旋翼 | `FanPropeller.cs:120-142` FixedUpdate 累加角度，`:319-324` LateUpdate 写 `m_fanVisualization.localRotation = m_origRot * AngleAxis(m_angle, axis)` | 开：`speed = m_maximumRotationSpeed`（`= 1000 * powerFactor + 700`，`FanPropeller.cs:92,108-112`；powerFactor=1 → **1700 °/s**）；关：`speed < 450 ? speed *= 0.9 : speed *= 0.98`（每 FixedUpdate，默认 0.02s）；`SetEnabled(false)` 立即 `speed = 800, angle = 292.3`（`:274-275`）；`angle += speed * dt`；`angle > 180 → angle -= 360`；轴：`m_isRotor ? Vector3.up : Vector3.right`（`Part_Rotor_01_SET.prefab:135` = 1，其余 = 0） | `animation/spin.ts` 状态机 + 透视压缩 |
| 轮子 | `CartWheel.cs:89-110` 视觉角由接地速度推算；物理上轮子是独立刚体 | `m_angle += -360 * m_spinSpeed / m_circumference * dt`；`m_circumference = 2πr` | **不做客户端动画**：PigForge 的轮子有独立刚体 + revolute 关节（ADR-008），快照 `rotation` 就是真实滚动角，`draw.ts` 已按 `yaw` 旋转精灵 |
| 逐帧（猪脸） | `SpriteAnimation.cs:196-215` 预生成每帧 mesh、`:229-260` 按累计时间切 `sharedMesh`；`:143` `Play(name)` 是排队语义（当前动画播完才切）；`:181` 子动画同步同名 clip | `FrameTiming.time` 是**本帧时长**（不是绝对时间）；非循环播完**停在最后一帧**；无插值、无事件帧 | `animation/frames.ts` 播放器 |
| 猪表情 | `Pig.cs:277-329` Update、`:409-438` SelectExpression、`:616-648` SetExpression、`:730-742` PlayAnimation、`:602-605` ReceiveObjectiveAchieved | 眨眼：`m_blinkTimer` 随机 **1.5–4.0s**，仅当前表情为 `Normal` 时触发；速度表情：`num = \|v\| + 0.3·\|vy\|`，`> 8` Grin、`> 0.5·(8+14)` FearfulGrin、`> 14` Fear（阈值来自 `Part_Pig_01_SET.prefab:443-445`）；切换后 **1s** 内不重选；**撞击**：`abs(\|v\| − 上一帧\|v\|) > 5` → `Hit` 1.0s（`:313-319`）；**坠落**：离地 > 0.25s 且 `−vy > 3` → `Fear_2`（`:428-431`）；**收集星星盒子 / 达成目标 → `Laugh` 3s**（`GoalBox.cs:104`、`OneTimeCollectable.cs:184` → `ObjectiveAchieved` → `Pig.cs:602-605`）。**注**：原作马达轮速度上限 `15·powerFactor`（`MotorWheel.cs:103`），8/14 是该上限的 53%/93% | `animation/frames.ts` 表情状态机 |
| 暂停 | `GameTime.Pause(true)` → `Time.timeScale = 0` | 帧动画/旋转/火焰全部用 `Time.deltaTime` → 暂停即冻结 | `anim-clock` 门控 |
| 参考实现交叉验证 | badpiggies-editor `particles/fan.rs:229` 状态机 + `mod.rs:84` `fan_propeller_foreshorten = |cos(angle)|` | 600 °/s、2s 惯性停转 | 与上表同量级，采用原作常量 |

### 与原作的已知偏差（有意，逐条记录）

1. **帧动画按时间轴推算**：原作 `Update` 每帧最多推进一帧（低帧率下动画变慢）；PigForge 按累计时间推进（低帧率下动画不减速）。
2. **轮子滚动由物理给出**：原作视觉角是从接地速度推算的；PigForge 的轮子是真刚体（铰链，ADR-008），滚动角直接来自快照，所以清单里不再有 `wheel` 动画描述符（与上一版 spec 相比是删减）。
3. **透视压缩以精灵自身中心为缩放中心**：原作绕 `FanVisualization` 节点原点旋转，节点原点与精灵中心存在 ≤0.22 世界单位的偏移（风扇/旋翼），近似误差在压缩到接近 0 时才可见。
4. **坠落表情用 `−vy` 近似、收集/目标类表情不做**：原作的 `Fear2` 还要求「离地 > 0.25s」，协议没有接地状态，改为 `−vy > fallFearThreshold` 直接判定；`Laugh` 只在收集星星盒子/达成目标时触发（沙盒 `ObjectivesEnabled = false`、内容里没有收集物），v1 不做。
5. **转速取 powerFactor = 1 的常量 1700 °/s**：原作随引擎功率因子浮动（`1000 * powerFactor + 700`），协议里没有该因子。
6. **建造期冻结**：原作的猪在建造期是活的（拖放零件影响 fun/fear、每 5–9s 随机 Laugh，`Pig.cs:440-504`）；本切片沿用「非运行中一律冻结」，建造期静止（见 Open Questions）。

### 阈值标定（实测）

标定方法（一次性实验，不留在测试里）：`PlayHost.CreateSandboxRoom()`（真实 Bepu + 真实 `content/parts.json` + terrain-v1）里放一个马达轮，`StartSimulation` → `SetPartTypeActive(17)`，逐 tick 读快照速度：

| 对象 | 0.05s | 0.10s | 0.20s | 0.37s |
|---|---|---|---|---|
| 单个马达轮（part 17，0.8 kg，thrustPerTick 2.2） | 5.17 m/s | 10.35 m/s | 20.69 m/s | 54.32 m/s |

每 tick Δv ≈ **2.59 m/s**（≈155 m/s²，= 推力/质量 − 摩擦），**无上限**。原作的 8 / 14 m/s 会在 4 / 6 tick（0.07 / 0.10 s）内被跨过——照抄绝对值，猪会在开动瞬间永久停在 Fear。多件复合体按 `Σ推力 / Σ质量` 同量级。

因此 v1 用**相对阈值**（本切片唯一的数值发明，写进清单可调）：

```text
vRef = 60 × Σ(该 body 上激活电机的 thrustPerTick) / Σ(该 body 上所有零件的 mass)   // 「满推 1 秒的速度」，m/s
Grin        ⇔ num > expression.speedFunRatio     × vRef   (默认 0.15)
FearfulGrin ⇔ num > expression.speedFearfulRatio × vRef   (默认 0.30)
Fear        ⇔ num > expression.speedFearRatio    × vRef   (默认 0.50)
无激活电机（滑翔/火箭/自由落体）⇒ vRef = expression.speedReference（绝对兜底，默认 20 m/s）
```

默认比例等价于「满推约 0.15 / 0.30 / 0.50 s 后依次换表情」，与载具轻重无关。原作绝对阈值 8/14 只有在补上原作限速（见 Open Questions）后才有意义。

## 内容契约：`part-textures.json` v3

`tools/bple-textures/extract.mjs` 生成，格式 `pigforge.part-textures`、`schemaVersion: 3`（当前 v2）。文件仍是本机生成物、进 `.gitignore`（ADR-003 决策 1）。

新增的都是**可选**字段；v2 清单仍可解析（按无动画处理）：

```jsonc
{
  "format": "pigforge.part-textures",
  "schemaVersion": 3,
  "source": "BPLE_Unity6",
  "unitsPerPixel": 0.026041666666666668,
  "atlases": { "IngameAtlas.png": { "width": 2048, "height": 2048 } },
  "parts": {
    "11": {
      "bbox": [0.4479, 1.1146],
      "sprites": [
        {
          "atlas": "IngameAtlas.png", "x": 2003, "y": 1351, "w": 38, "h": 107,
          "cx": 0, "cy": 0, "sx": 0.3958, "sy": 1.1146, "rot": 0,
          // 风扇叶片（FanVisualization 子树）：开关驱动
          "spin": { "axis": "x", "maxDegreesPerSecond": 1700 }
        },
        { "atlas": "IngameAtlas.png", "x": 760, "y": 1773, "w": 43, "h": 91,
          "cx": 0, "cy": 0, "sx": 0.4479, "sy": 0.9479, "rot": 0 }
      ]
    },
    "7": {
      "bbox": [0.6875, 0.8125],
      "sprites": [
        { "atlas": "IngameAtlas.png", "x": 1127, "y": 844, "w": 66, "h": 78,
          "cx": 0, "cy": 0, "sx": 0.6875, "sy": 0.8125, "rot": 0 },
        // 轮毂（WheelPivot 子树）：无动画描述符——滚动角来自轮子自己的刚体旋转
        { "atlas": "IngameAtlas.png", "x": 1127, "y": 551, "w": 63, "h": 64,
          "cx": 0.0029, "cy": -0.0317, "sx": 0.6563, "sy": 0.6667, "rot": 0 }
      ]
    },
    "4": {
      "bbox": [0.9688, 0.9165],
      // 有 expression 的部件才跑表情状态机（猪/猪王）
      "expression": { "speedFunRatio": 0.15, "speedFearfulRatio": 0.3, "speedFearRatio": 0.5, "speedReference": 20, "hitDeltaV": 5, "fallFearThreshold": 3 },
      "sprites": [
        // 省略猪的身体/耳朵/眼睛精灵（结构与上面的静态精灵相同）；下面是脸（Face 节点）
        { "atlas": "IngameAtlas.png", "x": 587, "y": 289, "w": 85, "h": 48,
          "cx": 0, "cy": -0.1468, "sx": 0.8854, "sy": 0.5, "rot": 0,
          "clips": {
            "Normal": { "loop": false, "frames": [
              { "atlas": "IngameAtlas.png", "x": 587, "y": 289, "w": 85, "h": 48,
                "cx": 0, "cy": -0.1468, "sx": 0.8854, "sy": 0.5, "rot": 0, "seconds": 0.1 } ] },
            // 其余 clip 的每帧都是同结构的完整描述符 + seconds
            "Blink":  { "loop": false, "frames": [ /* 2 帧 */ ] },
            "Laugh":  { "loop": true,  "frames": [ /* 2 帧 */ ] },
            "Hit":    { "loop": false, "frames": [ /* 1 帧 */ ] },
            "Grin":   { "loop": false, "frames": [ /* 1 帧 */ ] },
            "Fear_1": { "loop": true,  "frames": [ /* 2 帧 */ ] },
            "Fear_2": { "loop": true,  "frames": [ /* 2 帧 */ ] },
            "FearfulGrin": { "loop": false, "frames": [ /* 1 帧 */ ] }
          } }
      ]
    }
  }
}
```

字段语义：

- `spin`：每帧 `scale` 沿压缩轴乘 `|cos(angle)|`，精灵本身不旋转。`axis: "x"` → 压缩 **y**（风扇/螺旋桨）；`axis: "y"` → 压缩 **x**（旋翼）。
- `clips`：命名帧表。每帧是**完整精灵描述符**（`atlas/x/y/w/h/cx/cy/sx/sy/rot`）+ `seconds`（本帧时长）。`loop: false` 播完停在最后一帧。
- `expression`：只有带此块的部件才跑表情状态机；缺省 = 不跑。`*Ratio` 是相对 `vRef` 的比例，`speedReference` 是载具无激活电机时的绝对兜底速度（m/s），`hitDeltaV` 是撞击判定阈值（m/s，默认 5），`fallFearThreshold` 是坠落判定阈值（m/s，默认 3）。

校验（`renderer/atlas.ts`，沿用严格风格：坏数据抛错 → 加载器回退 `null`）：

- `schemaVersion` 接受 `2` 与 `3`；其他值抛错。v2 视为「无任何动画描述符」。
- `spin.axis` ∈ `{"x","y"}`；`spin.maxDegreesPerSecond > 0`。
- 帧表非空；每帧 `seconds > 0`；`loop` 为布尔；clip 名非空字符串。
- `expression`：三个比例、`speedReference`、`hitDeltaV`、`fallFearThreshold` 均为有限正数。

## 运行时契约

```text
clients/web/src/renderer/animation/clock.ts    # 墙钟差分 + 运行态门控
clients/web/src/renderer/animation/spin.ts     # 旋转状态机（风扇/螺旋桨/旋翼）
clients/web/src/renderer/animation/frames.ts   # 帧表播放器 + 表情状态机
clients/web/src/renderer/animation/index.ts    # 组合入口（updateAnimations / poseFor / reset）
clients/web/src/renderer/atlas.ts              # v3 解析
clients/web/src/renderer/draw.ts               # 应用 pose（唯一 canvas 写入点不变）
clients/web/src/App.vue                        # 时钟接线与 running 判定
```

### 时钟与门控（`animation/clock.ts`）

```ts
export interface AnimationClock {
  /** 推进时钟并返回本帧动画步长（秒）；未运行返回 0。 */
  step(now: number, running: boolean): number;
  reset(): void;
}
export function createAnimationClock(): AnimationClock;
```

- `dt = min((now - last) / 1000, 0.1)`，`last` 每帧更新（切后台回来不补帧）。
- `running === false` → 返回 0（冻结），状态不动。
- `running` 由 `false → true` 边沿 → 调用方 `resetAnimations(state)`：清空所有实体动画状态（角度归零、clip 回 `Normal`、眨眼计时重掷）。
- `running` 判定（`App.vue`）：
  - live 页：`playerPhase === "materialized"`（建造期与预览件不动画）；
  - 回放页：`clock.playing === true`（暂停/逐帧不动画）。

### 旋转运行时（`animation/spin.ts`）

每个「实体 × 精灵」一条状态：`{ angle: number; speed: number }`（角度制）。

风扇/螺旋桨/旋翼（`spin`）：

```text
if (entity.active) speed = maxDegreesPerSecond
else               speed *= exp(-λ · dt)          // λ = 1.010135 (speed ≥ 450) | 5.268026 (speed < 450)
angle += speed · dt
scaleY = |cos(angle)|        // axis "x"
scaleX = |cos(angle)|        // axis "y"
```

- `active` 取快照 `flags` bit0（`DrawEntity.active`），只在运行中出现边沿。
- 关闭沿（`active: true → false`）：立刻 `speed = 800`、`angle = 292.3`（原作的「抖一下」），之后按上式衰减。
- λ 由原作的每 FixedUpdate 因子连续化：`λ = -ln(factor) / 0.02`（`0.98 → 1.010135`、`0.9 → 5.268026`）。停止时间 ≈ `ln(1700/450)/1.010 + ln(450)/5.268 ≈ 2.5s`，与参考实现 2s 惯性同量级。
- 未激活且 `speed < 0.5` → 归零（避免长尾浮点抖动）。

轮子：**没有运行时动画**。轮子有自己的刚体 + revolute 关节（ADR-008），快照里的 `rotation` 就是真实滚动角；`draw.ts` 已按 `-entity.yaw` 旋转精灵，所以轮子贴图自动跟着转。`FakeWheelPivot` 的 ±8° 摆动属于原作视觉技巧，PigForge 不需要。

### 逐帧运行时（`animation/frames.ts`）

播放器（每个「实体 × 精灵」一条状态：`{ clip, frame, elapsed }`）：

```text
play(name): 有该 clip 就 { clip = name; frame = 0; elapsed = 0 }，没有就忽略
step(dt):   elapsed += dt
            while (elapsed >= frames[frame].seconds) { elapsed -= frames[frame].seconds;
              frame < 末帧 ? frame++ : (loop ? frame = 0 : 停在末帧并 break) }
pose.sprite = frames[frame]      // 帧是完整精灵描述符，替换源矩形与放置尺寸
```

- 子动画同步沿用原作语义：同一个部件内**所有**带同名 clip 的精灵一起切（猪的 Face 与 Eyes）。
- 部件没有 `clips` → 返回清单里的原始精灵。

表情状态机（仅 `expression` 存在的部件；全部本地触发，不依赖任何服务端事件）：

```text
每步（dt 来自动画时钟）：
  if (oneShotUntil > now) → 保持一次性 clip（Hit）
  else:
    blinkTimer -= dt
    if (blinkTimer <= 0 && current === "Normal") { play("Blink"); blinkTimer = rand(1.5, 4.0) }
    if (|Δ|v|| > expression.hitDeltaV) { play("Hit"); oneShotUntil = now + 1.0 }      // 撞击优先
    else if (now > expressionSetAt + 1.0) {
      if (−vy > expression.fallFearThreshold) { play("Fear_2"); expressionSetAt = now }
      else {
        num = |v| + 0.3·|vy|
        vRef = 该 body 有激活电机 ? 60 · Σthrust / Σmass : expression.speedReference
        next = num > expression.speedFearRatio    · vRef ? "Fear_1"
             : num > expression.speedFearfulRatio · vRef ? "FearfulGrin"
             : num > expression.speedFunRatio     · vRef ? "Grin" : "Normal"
        if (next !== current) { play(next); expressionSetAt = now }
      }
    }
```

- `blinkTimer` 初值 `rand(1.5, 4.0)`；随机数用 `Math.random`（纯表现，不入哈希）。
- **撞击（`Hit`）**：`|Δ|v|| = ||v_t| − |v_{t−1}|| > expression.hitDeltaV`（默认 5 m/s，原作 `Pig.cs:313-319`）→ 播 `Hit` **1.0s**，期间不重选表情。Δv 按渲染帧差分：快照 60 Hz、渲染 144 Hz，重复帧 Δv = 0，只有快照更新的那一帧会变（等价于相邻快照的速度差）。
  - 原作同样没有碰撞事件驱动表情：`OnCollisionEnter`（`relativeVelocity > 2`）只放星星粒子（`Pig.cs:607-614`）。
  - 实时视图的 PGFS 只有快照 + `flags`，**没有接触事件**；回放的 `CONTACT_STARTED` 不带撞击速度——两种视图都只能用 Δv 定强度。
  - 实测正常加速 ≈ 2.6 m/s/tick < 5，不误触发；TNT 冲击（impulse 25）、硬着陆、撞墙都远超阈值。`hitDeltaV` 进清单，火箭载具（≈ 4 m/s/tick）可调高。
- **坠落（`Fear_2`）**：`−vy > expression.fallFearThreshold`（默认 3 m/s，原作值）→ `Fear_2`；原作附加的「离地 > 0.25s」需要接地状态（协议没有），省略。
- `Laugh`（收集/目标）与建造期随机表情见下表。

#### 表情触发清单（原作 vs PigForge 沙盒）

| 触发 | 原作 | PigForge v1 |
|---|---|---|
| 速度分档（Grin / FearfulGrin / Fear） | `SelectExpression` | ✅ 快照 `vx/vy` + 相对阈值 |
| 眨眼 | `m_blinkTimer` | ✅ |
| 撞击（Hit） | 单帧 `Δ\|v\| > 5` | ✅ 同一规则（Δv 推断） |
| 坠落（Fear_2） | 离地 > 0.25s + `−vy > 3` | ⚠️ 只用 `−vy > 3` 近似 |
| 收集星星盒子 / 达成目标（Laugh 3s） | `GoalBox.cs:104`、`OneTimeCollectable.cs:184` → `ObjectiveAchieved` → `Pig.cs:602-605` | ❌ 沙盒 `ObjectivesEnabled = false`、内容里没有收集物、协议没有目标事件 |
| 建造期随机表情（拖放零件 fun/fear、每 5–9s 随机 Laugh） | `UpdateBuildModeAnimations`（`Pig.cs:440-504`） | ❌ 建造期整体冻结（偏差 6） |

### `draw.ts` 接入

```ts
export interface SpritePose {
  /** 实际绘制的精灵：帧动画可能替换整条描述符，否则就是清单里的原始精灵。 */
  sprite: PartSprite;
  /** 在精灵静态 rot 之上追加的旋转（弧度，+y 上坐标系，逆时针为正）。 */
  rot: number;
  /** 以精灵中心为原点的缩放（透视压缩用；1 = 不变）。 */
  scaleX: number;
  scaleY: number;
}
export function poseFor(state: AnimationState, entity: DrawEntity, spriteIndex: number, sprite: PartSprite): SpritePose;
```

`draw.ts` 的精灵分支改为：`pose = poseFor(state, entity, spriteIndex, sprite)` → 用 `pose.sprite` 的 `x/y/w/h` 作源矩形、`cx/cy/sx/sy` 作放置、`rot + pose.sprite.rot` 作旋转、`sx·scaleX / sy·scaleY` 作尺寸。无动画状态或该精灵没有动画描述符时返回 `{ sprite, rot: 0, scaleX: 1, scaleY: 1 }`，调用方零分支。缩略图（`renderer/thumbnails.ts`）**不**接动画：始终用清单里的静态精灵（第一帧）。

`animation/index.ts` 组合入口：

```ts
export function createAnimationState(): AnimationState;
export function updateAnimations(
  state: AnimationState,
  entities: readonly DrawEntity[],
  textures: PartTextureSet | null,
  dtSeconds: number,          // 0 = 冻结
): void;
export function poseFor(state: AnimationState, entity: DrawEntity, spriteIndex: number, sprite: PartSprite): SpritePose;
export function resetAnimations(state: AnimationState): void;
```

- 状态放普通对象（Vue 响应式系统之外，`web-client-spec.md` §3.1 约束 2）。
- 每个 `updateAnimations` 末做 mark-and-sweep：快照里已消失的 `entityId` 立即回收状态。
- 预览件（`bodyId === 0`）不建状态、不推进。

## 提取器（`tools/bple-textures/extract.mjs`）

在现有 prefab 解析上追加：

1. **FanPropeller**（`Part_Fan_*`、`Part_PlanePropeller_*`、`Part_Rotor_*`）：读 `m_fanVisualization` 指向的 Transform → 其 GameObject 上的 `Sprite` 打 `spin`；`axis = m_isRotor ? "y" : "x"`；`maxDegreesPerSecond = 1700`（`1000 · powerFactor + 700` 的 powerFactor = 1 情形，写死并加注释）。
2. **轮子**（`Part_{CartWheel,MotorWheel,OffRoadWheel,StickyWheel,SmallWheel,NormalWheel}_*`）：**不产动画描述符**——滚动角来自物理刚体（ADR-008）。
3. **SpriteAnimation**：遍历 prefab 内的 `SpriteAnimation` 组件（脚本 guid `b724b453dd61eb03a1d123fa87323918`），把 `m_animations` 的每个 `FrameTiming.id` 解析成精灵矩形（复用现有 `extractSprite` 的 id → 图集矩形逻辑），生成 `clips`；`m_childAnimations` 只用于确认同名 clip 的同步语义，不额外产出。
4. **Pig/KingPig**：读 `speedFunThreshold` / `speedFearThreshold` / `fallFearThreshold` 生成 `expression`。
5. 找不到任何动画组件 → 该部件不带动画字段（现状不变）；prefab 解析失败沿用现有 `warnings` 累加。

`tools/bple-textures/part-map.json` 不变（映射表只做 partTypeId → prefab 名）。

## Code Style

- TypeScript：纯函数 + 一个不透明状态对象；`animation/*.ts` 不 import Vue、不碰 DOM/canvas；`draw.ts` 是唯一 canvas 写入点。
- 每帧热路径不分配：状态在 Map 里复用，`poseFor` 返回可复用的对象或直接写回调用方传入的临时结构（与现有 `layoutSprites` 的每帧 `map` 保持同级别，必要时一并收敛）。
- 数值常量集中在 `animation/spin.ts` / `animation/frames.ts` 顶部并注明 BPLE 出处（文件:行）。
- 命名沿用仓库风格：英文、行为化、无下划线。

## Testing Strategy

### Web（vitest，`pnpm test`）

- `renderer/atlas.test.ts`：v3 清单解析（`spin`/`clips`/`expression` 合法）；`schemaVersion` 2 仍可解析且无动画；`axis` 非法、空帧表、`seconds ≤ 0`、阈值非有限数 → 抛错；v1/未知版本 → 抛错。
- `renderer/animation/spin.test.ts`：
  - 激活 → 满速；关闭沿 → `speed = 800`、`angle = 292.3`；
  - 衰减：从 1700 到 450 用时 ≈ `ln(1700/450)/1.010135`，全程到静止 ≈ 2.5s（容差 ±5%）；
  - 压缩：`axis "x"` 只改 `scaleY`、`axis "y"` 只改 `scaleX`，`|cos|` 在 90° 为 0；
- `renderer/animation/frames.test.ts`：帧时长推进、`loop` 回卷、非循环停在末帧、缺 clip 忽略、同名 clip 跨精灵同步；表情：眨眼计时落在 1.5–4.0s 且仅 `Normal` 时触发；`vRef` 计算（2 电机 × 2.2 thrust / 3.6 kg → 73.3 m/s）与 0.15 / 0.30 / 0.50 比例边界；无激活电机 → 用 `speedReference`；撞击：相邻帧 `Δ|v|` 跨 5 的边界、速度不变的重复帧不触发、触发后 1.0s 内不重选；坠落：`−vy` 跨过 `fallFearThreshold` 播 `Fear_2`。
- `renderer/animation/clock.test.ts`：`dt` 上限 0.1s；`running = false` 返回 0 且不推进；`false → true` 触发复位。
- `renderer/draw.test.ts`：带 `spin` 的精灵按 `scaleY` 缩放；带帧表的精灵绘制帧矩形；无动画描述符 → 与既有断言逐字一致（既有用例不改）。
- `pnpm build`（`vue-tsc --noEmit` + vite build）。

### .NET

轮子滚动依赖物理铰链（ADR-008），因此本切片**包含 .NET 改动**：`dotnet test PigForge.slnx` 必须全绿，并新增 `SandboxRoomTests.SandboxCartHingesWheelsAndDrives` / `SandboxWheelUnderFrameHingesToFrame` 与 Bepu 铰链用例。

### 手验（Success Criteria）

`--play` 房间 + 两个浏览器标签（或单标签自测）：放风扇/螺旋桨/轮子/猪 → Start → 观察；开关切换 → 停转；RESET → 再 Start → 相位复位。回放页加载一个回放，播放/暂停/逐帧各看一次。

## Boundaries

**Always**

- 动画只读快照与内容；服务端权威、客户端零预测不变。
- 缺贴图/缺动画描述符 → 回退静态渲染，不报错、不阻塞。
- 新行为有 vitest 覆盖（每个 module 至少一处可观察断言）。
- 常量注明 BPLE 出处；UTF-8 LF。

**Ask first**

- 改 `part-textures.json` 的版本或字段语义（本切片 v2 → v3，且必须向后兼容 v2 读取）。
- 给快照/PGFC 新增字段（本切片明确不加）。
- 引入第三方动画库或替换 Canvas 2D 渲染器。
- 把动画相位写进回放、快照或状态哈希。

**Never**

- 服务端参与动画、下发动画相位或帧号。
- 客户端预测位姿、用动画「掩盖」权威状态延迟。
- 把动画/贴图字段写进 `content/parts.json` 或任何 `schemas/*.schema.json`。
- 发明原作不存在的动画（TNT 闪烁、气球帧序列、引擎火焰帧）。
- 热路径 JSON、反射或每帧新建大量对象。

## Success Criteria

1. `pnpm test`、`pnpm build` 全绿；`dotnet test PigForge.slnx` 全绿（含 ADR-008 的铰链用例）。
2. 风扇/螺旋桨/旋翼：开关打开后立即满速压缩旋转；关闭后约 2.5s 内指数衰减停住（可见「抖一下」）。
3. 轮子：物理上真滚动（轮子刚体的 `rotation` 随行进变化，`ω_z ≠ 0`），贴图跟着刚体转——见 ADR-008 与 `SandboxRoomTests.SandboxCartHingesWheelsAndDrives`。
4. 猪：静止时每 1.5–4s 眨眼一次；速度按载具能力分档（满推 1 秒速度的 15% / 30% / 50%）依次露出 Grin / FearfulGrin / Fear；单帧速度突变 > 5 显示受击 Hit 1s；下落速度 > 3 m/s 显示 Fear_2。
5. 建造期、回放暂停、拖动预览件（`bodyId === 0`）完全不动画；Start/RESET 后动画相位复位。
6. 干净检出（无 `clients/web/public/assets/original/`）：渲染与行为与本切片前逐字一致，测试不依赖任何原版资产。
7. 抓包：客户端 → 服务器仍只有 PGFC 命令，无动画相关上行。

## Open Questions

- **透视压缩中心**：当前以精灵中心近似（原作绕节点原点）。若视觉可见偏差，改为清单里带节点原点。
- **BlasterTNT 一次性放大淡出**（`BlasterTNT.cs:222-228`）：需要「激活沿」事件；`trigger` 件触发后 `Active` 可能只在一帧快照里为真。是否值得做取决于实测能否稳定观察到边沿。
- **猪 `Fear2` / `Laugh`**：需要接地时间与目标达成事件，v1 不做。
- **回放相位可复现性**：动画按墙钟推进，暂停/逐帧冻结，但同一回放两次播放的相位不同。若需要确定性，改为按 tick 派生时间——需要先确认这是不是需求。
- **是否要 PigForge 原创动画**（TNT 闪烁、气球呼吸、引擎火焰）：原作不存在，需要美术与新的内容字段，另开切片。
- **是否补原作马达限速**（`MotorWheel.cs:103` `m_maximumSpeed = 15·powerFactor` + `LimitForceForSpeed`）：补上后速度回到原作量级，表情可以回到绝对阈值 8/14，轮子也不会频闪——但这是玩法改动，另开切片。本切片用相对阈值规避。
- **轮子高速频闪**：50 m/s、r = 0.36 → 约 8000 °/s ≈ 133°/帧，真实旋转也会出现车轮倒转的频闪。若限速切片不做，需要视觉钳制（超过约 90°/帧时按低速/模糊处理）。
- **收集物/目标表情**（星星盒子、终点 → `Laugh` 3s）：需要内容侧有收集物与目标事件（沙盒现在是 `ObjectivesEnabled = false`，PGFS 也没有目标事件）——等关卡/收集物切片。
- **建造期要不要让猪动起来**：原作建造期有随机表情与「看向拖动的零件」（`Pig.cs:440-504`），本切片建造期冻结。若要，需要给建造期的自有零件（`bodyId === 0` 预览件）单独开门控。

## References

- BPLE 原作：`Assets/Scripts/Assembly-CSharp/{SpriteAnimation,FanPropeller,CartWheel,MotorWheel,OffRoadWheel,StickyWheel,Pig,KingPig,GameTime}.cs`；`Assets/GameObject/{Part_Fan_01_SET,Part_PlanePropeller_01_SET,Part_Rotor_01_SET,Part_CartWheel_01_SET,Part_Pig_01_SET}.prefab`
- 参考实现：`c:/tmp/badpiggies-editor/crates/renderer/src/renderer/particles/{fan.rs,mod.rs}`、`compounds.rs`（无部件帧动画；风扇 600 °/s + `|cos|` 压缩）
- 本仓库：`docs/decisions/ADR-003-original-texture-assets.md`、`docs/decisions/ADR-005-part-shapes-from-bple-colliders.md`、`docs/specs/web-client-spec.md`、`docs/specs/play-part-switches.md`
- 代码：`tools/bple-textures/extract.mjs`、`clients/web/src/renderer/{atlas,draw,thumbnails}.ts`、`clients/web/src/schema/{types,toDrawEntities}.ts`、`clients/web/src/App.vue`
