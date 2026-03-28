# JRETS-Go 关键代码位置快速参考

## 快速索引

### 1️⃣ 下一站游戏位移的计算

| 内容 | 位置 | 关键代码 |
|------|------|--------|
| **当前站位移** | DebugRealtimeDataSource.cs:173 | `_stationStopDisplacements[_currentStationIndex]` |
| **目标站位移** | DebugRealtimeDataSource.cs:177 | `_stationStopDisplacements[_currentStationIndex + 1]` |
| **接近状态位移** | DebugRealtimeDataSource.cs:181 | 目标位移 - 100米 |
| **发车时位移** | DebugRealtimeDataSource.cs:192 | 当前位移 + 200米 |
| **位移数组初始化** | DebugRealtimeDataSource.cs:150 | `BuildStationStopDisplacements()` |

**访问位移时的三个关键变量：**
```csharp
private double _currentDistance;          // snapshot.CurrentDistanceMeters
private double _targetStopDistance;       // snapshot.TargetStopDistanceMeters
private int _currentStationIndex;         // 当前站索引
```

---

### 2️⃣ DoorOpen 相关的事件判断

| 判断点 | 位置 | 条件 | 结果 |
|-------|------|------|------|
| **状态转换检测** | MainWindow.cs:783 | `_previousDoorOpen` 变化 | 进入CaptureStopScore() |
| **停靠站识别** | DisplayStateResolver.cs:18 | `snapshot.DoorOpen ? currentStation : null` | CurrentStopStation |
| **显示状态判断** | MainWindow.cs:978,984 | DoorOpen + distance | "ただいま"或"まもなく"或"次は" |
| **评分捕获时机** | MainWindow.cs:1143 | `doorOpenTransition = DoorOpen && !_previousDoorOpen` | 锁定TargetStopDistance |
| **超车检测** | MainWindow.cs:1151 | `!DoorOpen && distance超限` | OvershootFaultTriggered |

**关键变量：**
```csharp
private bool _previousDoorOpen;                        // 前一帧DoorOpen状态
private bool? _mapLastDoorOpen;                        // 地图更新用
bool doorOpenTransition = snapshot.DoorOpen && !_previousDoorOpen;
```

---

### 3️⃣ StopScoringService 中的位置逻辑

| 功能 | 位置 | 计算公式 | 备注 |
|------|------|--------|------|
| **位置误差** | StopScoringService.cs:56 | `CurrentDistance - TargetStopDistance` | 可正可负 |
| **位置评分** | StopScoringService.cs:62 | 通过表查询（厘米单位） | 见下表 |
| **时间误差** | StopScoringService.cs:58 | `MainClockSeconds - ScheduledSeconds` | 秒单位 |
| **时间评分** | StopScoringService.cs:63 | 通过表查询（秒单位） | 见下表 |
| **完美奖励** | StopScoringService.cs:67-72 | +10位置 +10时间 | 各最多+10 |
| **最终评分** | StopScoringService.cs:75 | `PositionScore + TimeScore + 奖励` | 四舍五入到0.1 |

**评分表：**
- **位置表**：误差5cm(50分) → 480cm(0分)
- **时间表**：误差3秒(50分) → 180秒(0分)
- **完美停靠**：位置误差≤0.0001cm && 时间误差=0

---

### 4️⃣ DisplayStateResolver 关于下一站位置的逻辑

| 功能 | 代码 | 说明 |
|------|------|------|
| **查找当前站** | 第9行 | `Array.FindIndex(..., x => x.Id == snapshot.NextStationId)` |
| **识别停靠站** | 第18行 | `if (snapshot.DoorOpen) currentStation else null` |
| **查找物理下一站** | 第19行 | `currentIndex < count - 1 ? stations[currentIndex + 1] : null` |
| **计算进度比** | 第25-26行 | `currentIndex / (stations.Count - 1)` |
| **返回显示状态** | 第28-34行 | TrainDisplayState 对象 |

**核心逻辑：**
```csharp
NextStationId 
  ↓ (查表)
currentStation (当前物理站)
  ↓ (判断DoorOpen)
CurrentStopStation (仅DoorOpen=true时) 或 null
  ↓
nextStation = currentStation.Next
```

---

### 5️⃣ 接近状态触发逻辑（MainWindow.UpdateDisplay）

**位置：** MainWindow.xaml.cs:982-1010

**三层判断：**

#### 第1层：门打开检查
```csharp
if (snapshot.DoorOpen && state.CurrentStopStation is not null)
{
    statusText = "ただいま";        // 停靠状态
    displayStation = state.CurrentStopStation;
}
```

#### 第2层：门关闭 + 距离检查
```csharp
else if (!snapshot.DoorOpen && nextStoppingStation is not null)
{
    var remainingDistance = snapshot.TargetStopDistanceMeters 
                          - snapshot.CurrentDistanceMeters;
    
    if (remainingDistance < 100)
    {
        statusText = "まもなく";     // 接近状态 < 100m
    }
    else
    {
        statusText = "次は";         // 正常行驶 >= 100m
    }
    displayStation = nextStoppingStation;
}
```

#### 第3层：降级处理
```csharp
else
{
    displayStation = state.NextStation;  // 使用物理下一站
    statusText = "次は";
}
```

**关键阈值：**
| 距离范围 | 状态 | 日文 |
|---------|------|------|
| DoorOpen=true | 停靠 | ただいま |
| 0 ~ 100m | 接近 | まもなく |
| >100m | 正常 | 次は |

---

## 核心数据流

```
游戏进程内存
    ↓
ProcessMemoryRealtimeDataSource.GetSnapshot()
    ↓
RealtimeSnapshot {
    NextStationId,
    DoorOpen,
    CurrentDistanceMeters,
    TargetStopDistanceMeters,
    MainClockSeconds,
    TimetableHour/Minute/Second
}
    ↓
    ├─→ DisplayStateResolver.Resolve()
    │       ↓
    │   TrainDisplayState {
    │       CurrentStopStation,  // DoorOpen时有值
    │       NextStation,
    │       DoorOpen,
    │       ProgressRatio
    │   }
    │
    └─→ StopScoringService.ScoreStop()
            ↓
        StationStopScore {
            PositionErrorMeters,
            TimeErrorSeconds,
            PositionScore,
            TimeScore,
            FinalScore
        }
            ↓
        MainWindow.CaptureStopScore()
            ↓
        UI + 音声播放 + 评分结算
    
    ↓
    → MainWindow.UpdateDisplay()
        ↓
    remainingDistance = TargetStop - CurrentDistance
        ↓
    判断"ただいま"/"まもなく"/"次は"
        ↓
    UI更新 + 号码牌更新 + 音声
```

---

## 常用方法速查

### 距离相关
```csharp
// 获取剩余距离
double remainingDistance = snapshot.TargetStopDistanceMeters - snapshot.CurrentDistanceMeters;

// 获取位置误差（用于评分）
double positionErrorSigned = snapshot.CurrentDistanceMeters - snapshot.TargetStopDistanceMeters;
double positionErrorAbsolute = Math.Abs(positionErrorSigned);

// 判断接近状态
bool isApproaching = remainingDistance < 100 && !snapshot.DoorOpen;
```

### 时间相关
```csharp
// 计算时刻表秒数
int scheduledSeconds = snapshot.TimetableHour * 3600 
                     + snapshot.TimetableMinute * 60 
                     + snapshot.TimetableSecond;

// 计算时间误差
int timeErrorSigned = snapshot.MainClockSeconds - scheduledSeconds;
```

### 站点相关
```csharp
// 查找当前站
var currentStation = stations.FirstOrDefault(s => s.Id == snapshot.NextStationId);

// 查找停靠站（仅当车门打开）
var stopStation = snapshot.DoorOpen ? currentStation : null;

// 查找下一站
var nextStation = currentIndex < stations.Count - 1 
                ? stations[currentIndex + 1] 
                : null;
```

---

## 调试技巧

### 查看实时值
**MainWindow.xaml.cs:1062-1067行** - UpdateMemoryDebugText()
```csharp
MemNextStationText.Text = $"Current Station Id: {snapshot.NextStationId}";
MemDoorText.Text = $"Door Open: {(snapshot.DoorOpen ? "True" : "False")}";
MemCurrentDistanceText.Text = $"Current Distance (m): {snapshot.CurrentDistanceMeters:F2}";
MemTargetDistanceText.Text = $"Target Stop Distance (m): {snapshot.TargetStopDistanceMeters:F2}";
```

### Debug模式三态循环
**DebugRealtimeDataSource.cs** - 按钮DebugAdvance()触发循环：
1. **Stopped** → **Departed** (关门，移动200m)
2. **Departed** → **Approaching** (接近，移动target-100m)
3. **Approaching** → **Stopped** (开门，到达下一站)

---

## 文件导航速记

| 功能 | 文件 |
|------|------|
| 核心数据结构 | Core/Runtime/*.cs |
| 状态解析 | Core/Services/DisplayStateResolver.cs |
| 评分计算 | Core/Services/StopScoringService.cs |
| Debug数据源 | Core/Services/DebugRealtimeDataSource.cs |
| 内存读取 | Core/Services/ProcessMemoryRealtimeDataSource.cs |
| UI更新逻辑 | App/MainWindow.xaml.cs |
| 配置加载 | Core/Services/Yaml*Loader.cs |
| 内存偏移 | Core/Configuration/MemoryOffsetsConfiguration.cs |
