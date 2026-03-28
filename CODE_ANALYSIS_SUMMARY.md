# JRETS-Go 评分和接近状态代码分析

## 1. 关键数据结构

### RealtimeSnapshot（[src/JRETS.Go.Core/Runtime/RealtimeSnapshot.cs](src/JRETS.Go.Core/Runtime/RealtimeSnapshot.cs)）
```csharp
public sealed class RealtimeSnapshot
{
    public required int NextStationId { get; init; }           // 下一站的ID
    public required bool DoorOpen { get; init; }               // 车门是否打开
    public required double CurrentDistanceMeters { get; init; } // 当前列车位置（米）
    public required double TargetStopDistanceMeters { get; init; } // 目标停止位置（米）
    public required int MainClockSeconds { get; init; }         // 主时钟秒数
    public required int TimetableHour { get; init; }            // 时刻表小时
    public required int TimetableMinute { get; init; }          // 时刻表分钟
    public required int TimetableSecond { get; init; }          // 时刻表秒数
}
```

### TrainDisplayState（[src/JRETS.Go.Core/Runtime/TrainDisplayState.cs](src/JRETS.Go.Core/Runtime/TrainDisplayState.cs)）
```csharp
public sealed class TrainDisplayState
{
    public required bool DoorOpen { get; init; }
    public required StationInfo? CurrentStopStation { get; init; }  // 当前停靠站（仅DoorOpen=true时）
    public required StationInfo? NextStation { get; init; }        // 下一个物理站
    public required string DisplayText { get; init; }
    public required double ProgressRatio { get; init; }           // 进度比例 0.0-1.0
}
```

---

## 2. 下一站位移的计算

### 2.1 DebugRealtimeDataSource 中的实现
位置：[src/JRETS.Go.Core/Services/DebugRealtimeDataSource.cs](src/JRETS.Go.Core/Services/DebugRealtimeDataSource.cs)

**关键方法：**

#### ResolveCurrentStopDistance()（第173行）
```csharp
private double ResolveCurrentStopDistance()
{
    return _stationStopDisplacements[Math.Clamp(_currentStationIndex, 0, _stationStopDisplacements.Length - 1)];
}
```
- 返回当前停靠站的距离位移值

#### ResolveTargetStopDistance()（第177行）
```csharp
private double ResolveTargetStopDistance()
{
    var targetIndex = Math.Min(_currentStationIndex + 1, _stationStopDisplacements.Length - 1);
    return _stationStopDisplacements[targetIndex];
}
```
- 返回下一站的目标停止位移

#### ResolveApproachingDistance()（第181行）
```csharp
private double ResolveApproachingDistance()
{
    var current = ResolveCurrentStopDistance();
    var target = ResolveTargetStopDistance();
    var approach = target - DebugApproachRemainingMeters;  // DebugApproachRemainingMeters = 100米
    
    if (approach <= current)
    {
        approach = (current + target) / 2;
    }
    
    return approach;
}
```
- 计算"接近"状态时的位移：距离目标还有100米时触发

#### ResolveDepartedDistance()（第192行）
```csharp
private double ResolveDepartedDistance()
{
    var current = ResolveCurrentStopDistance();
    var target = ResolveTargetStopDistance();
    var departed = current + DebugDepartureAdvanceMeters;  // DebugDepartureAdvanceMeters = 200米
    return Math.Min(departed, target);
}
```
- 发车时的位移：从当前站往前推进200米

#### 三态阶段循环（第74-100行）
```csharp
public void DebugAdvance()
{
    switch (_currentPhase)
    {
        case DebugPhase.Stopped:
            // ただいま → 次は
            _currentPhase = DebugPhase.Departed;
            _doorOpen = false;
            _currentDistance = ResolveDepartedDistance();
            break;
        
        case DebugPhase.Departed:
            // 次は → まもなく
            _currentPhase = DebugPhase.Approaching;
            _currentDistance = ResolveApproachingDistance();
            break;
        
        case DebugPhase.Approaching:
            // まもなく → ただいま(Next Station)
            if (_currentStationIndex < _stations.Count - 1)
            {
                _currentStationIndex++;
            }
            _currentPhase = DebugPhase.Stopped;
            _doorOpen = true;
            _currentDistance = ResolveCurrentStopDistance();
            _targetStopDistance = ResolveTargetStopDistance();
            _clockSeconds += 180;
            break;
    }
}
```

**三个阶段：**
1. `Stopped`：停靠站，开门（ただいま）
2. `Departed`：发车，关门（次は）
3. `Approaching`：接近下一站（まもなく）

---

## 3. DoorOpen 相关的判断

### 3.1 在 DisplayStateResolver 中（[src/JRETS.Go.Core/Services/DisplayStateResolver.cs](src/JRETS.Go.Core/Services/DisplayStateResolver.cs)）

```csharp
public TrainDisplayState Resolve(LineConfiguration lineConfiguration, RealtimeSnapshot snapshot)
{
    var stations = lineConfiguration.Stations;
    var currentIndex = Array.FindIndex(stations.ToArray(), x => x.Id == snapshot.NextStationId);
    
    if (currentIndex < 0)
    {
        currentIndex = 0;
    }
    
    var currentStation = stations.Count == 0 ? null : stations[currentIndex];
    var nextStation = currentIndex < stations.Count - 1 ? stations[currentIndex + 1] : null;
    
    // **关键逻辑：DoorOpen决定CurrentStopStation**
    var currentStopStation = snapshot.DoorOpen ? currentStation : null;
    
    var displayText = snapshot.DoorOpen
        ? currentStation is null ? "Door Open" : $"Stopped: {currentStation.NameJp}"
        : nextStation is null ? "Running" : $"Next: {nextStation.NameJp}";
    
    var denominator = Math.Max(1, stations.Count - 1);
    var progressIndex = Math.Max(0, currentIndex);
    var progressRatio = Math.Clamp((double)progressIndex / denominator, 0.0, 1.0);
    
    return new TrainDisplayState
    {
        DoorOpen = snapshot.DoorOpen,
        CurrentStopStation = currentStopStation,
        NextStation = nextStation,
        DisplayText = displayText,
        ProgressRatio = progressRatio
    };
}
```

**关键点：**
- `DoorOpen == true` → `CurrentStopStation` 为当前站
- `DoorOpen == false` → `CurrentStopStation` 为 null
- 这决定了UI显示"ただいま"或"次は"

### 3.2 在 MainWindow.xaml.cs 中的应用

#### 记录DoorOpen状态（第137-139行）
```csharp
private bool _previousDoorOpen;
private DateTime? _lastDoorOpenTransitionAt;
private double? _activeApproachTargetStopDistance;
```

#### DoorOpen状态变化时的处理（第782-787行）
```csharp
_previousDoorOpen = currentSnapshot.DoorOpen;
if (_previousDoorOpen)  // 从关门→开门的变化
{
    _lastDoorOpenTransitionAt = currentSnapshot.CapturedAt;  // 记录开门时间
}
ResetAnnouncementState(currentSnapshot.DoorOpen);
```

#### 捕获停靠评分（第1135-1165行）
```csharp
private void CaptureStopScore(RealtimeSnapshot snapshot, TrainDisplayState state)
{
    bool doorOpenTransition = snapshot.DoorOpen && !_previousDoorOpen;
    
    if (doorOpenTransition)
    {
        // 在门打开时捕获目标位移和时刻表信息
        var departureStationId = state.CurrentStopStation?.Id ?? _lastKnownStopStationId;
        _activeApproachTargetStopDistance = snapshot.TargetStopDistanceMeters;
        _activeApproachScheduledSeconds = snapshot.TimetableHour * 3600 
                                        + snapshot.TimetableMinute * 60 
                                        + snapshot.TimetableSecond;
        _activeApproachStationId = ResolveNextStoppingStationFromCurrentId(departureStationId);
        _activeApproachOvershootFaultTriggered = false;
        ResetApproachDisplaySmoothing();
    }
    
    if (!snapshot.DoorOpen && _activeApproachTargetStopDistance is not null)
    {
        // 门关闭后，检查是否超车（Overshoot）
        var approachPositionErrorSigned = snapshot.CurrentDistanceMeters - _activeApproachTargetStopDistance.Value;
        if (approachPositionErrorSigned >= OvershootFaultMeters)  // 超过容差阈值
        {
            _activeApproachOvershootFaultTriggered = true;
        }
    }
}
```

---

## 4. StopScoringService 中的位置和时间评分

位置：[src/JRETS.Go.Core/Services/StopScoringService.cs](src/JRETS.Go.Core/Services/StopScoringService.cs)

### 4.1 位置误差计算（第52-80行）

```csharp
public StationStopScore ScoreStop(StationInfo station, RealtimeSnapshot snapshot)
{
    // **位置误差 = 当前距离 - 目标停止位等距离**
    var positionErrorSigned = snapshot.CurrentDistanceMeters - snapshot.TargetStopDistanceMeters;
    var positionError = Math.Abs(positionErrorSigned);
    
    // 时间误差 = 实际时间 - 计划时间
    var scheduledSeconds = snapshot.TimetableHour * 3600 + snapshot.TimetableMinute * 60 + snapshot.TimetableSecond;
    var timeErrorSigned = snapshot.MainClockSeconds - scheduledSeconds;
    var timeError = Math.Abs(timeErrorSigned);
    
    // 转换为厘米（分数表按厘米）
    var positionErrorCm = positionError * 100;
    var positionScore = StepScoreFromTable(positionErrorCm, PositionScoreTable);
    var timeScore = StepScoreFromTable(timeError, TimeScoreTable);
    
    var perfectBonus = 0;
    
    // 完美停靠奖励
    if (positionErrorCm <= PerfectPositionToleranceCm)  // <= 0.0001 cm
    {
        perfectBonus += 10;
    }
    
    if (timeError == 0)
    {
        perfectBonus += 10;
    }
    
    var finalScore = Math.Round(positionScore + timeScore + perfectBonus, 1);
    
    return new StationStopScore
    {
        StationId = station.Id,
        StationName = station.NameJp,
        PositionErrorMeters = Math.Round(positionErrorSigned, 2),
        TimeErrorSeconds = timeErrorSigned,
        PositionScore = Math.Round(positionScore, 1),
        TimeScore = Math.Round(timeScore, 1),
        FinalScore = finalScore,
        IsScoredStop = true
    };
}
```

### 4.2 评分表

#### 位置评分表（第11-22行）
```csharp
private static readonly (double ErrorCm, double Score)[] PositionScoreTable =
[
    (5, 50),      // 误差 ≤5cm => 50分
    (15, 45),     // 误差 ≤15cm => 45分
    (30, 40),
    (60, 35),
    (90, 30),
    (120, 25),
    (180, 20),
    (240, 15),
    (300, 10),
    (360, 5),
    (480, 0)      // 误差 ≥480cm => 0分
];
```

#### 时间评分表（第24-35行）
```csharp
private static readonly (double ErrorSeconds, double Score)[] TimeScoreTable =
[
    (3, 50),      // 误差 ≤3秒 => 50分
    (6, 45),      // 误差 ≤6秒 => 45分
    (12, 40),
    (18, 35),
    (24, 30),
    (36, 25),
    (48, 20),
    (60, 15),
    (90, 10),
    (120, 5),
    (180, 0)      // 误差 ≥180秒 => 0分
];
```

---

## 5. 接近状态的逻辑（MainWindow中）

位置：[src/JRETS.Go.App/MainWindow.xaml.cs](src/JRETS.Go.App/MainWindow.xaml.cs#L982-L1010)

### 5.1 状态判断逻辑

```csharp
private void UpdateDisplay()
{
    // ... 前置代码 ...
    
    // 确定显示的站点和状态文本
    StationInfo? displayStation = null;
    string statusText = "次は";
    var nextStoppingStation = ResolveAnnouncementTargetStationId(state) 
        is int nextStoppingStationId
            ? _lineConfiguration.Stations.FirstOrDefault(x => x.Id == nextStoppingStationId)
            : null;
    
    if (snapshot.DoorOpen && state.CurrentStopStation is not null)
    {
        // 状态1：ただいま（停靠状态）
        // 条件：车门打开 && 有当前停靠站
        displayStation = state.CurrentStopStation;
        statusText = "ただいま";
    }
    else if (!snapshot.DoorOpen && nextStoppingStation is not null)
    {
        // 检查到下站的剩余距离
        var remainingDistance = snapshot.TargetStopDistanceMeters - snapshot.CurrentDistanceMeters;
        
        if (remainingDistance < 100)
        {
            // 状态2：まもなく（接近状态）
            // 条件：车门关闭 && 距离目标站 < 100米
            displayStation = nextStoppingStation;
            statusText = "まもなく";
        }
        else
        {
            // 状态3：次は（正常行驶）
            // 条件：车门关闭 && 距离目标站 >= 100米
            displayStation = nextStoppingStation;
            statusText = "次は";
        }
    }
    else
    {
        // 降级处理：无法解析停靠站时使用物理下一站
        displayStation = state.NextStation;
        statusText = "次は";
    }
    
    NextStationStatusTextBlock.Text = statusText;
    NextStationNameTextBlock.Text = displayStation?.NameJp ?? "--";
}
```

### 5.2 关键距离阈值

| 条件 | 显示状态 | 英文含义 | 说明 |
|------|--------|--------|------|
| DoorOpen=true | **ただいま** | Now | 停靠中，可上下车 |
| DoorOpen=false && 剩余距离 < 100m | **まもなく** | Approaching | 即将到达，接近状态 |
| DoorOpen=false && 剩余距离 >= 100m | **次は** | Next | 正常行驶到下一站 |

---

## 6. 跨文件的完整流程

### 流程图

```
[RealtimeSnapshot（内存读取）]
           ↓
[CurrentDistanceMeters, TargetStopDistanceMeters, DoorOpen, NextStationId]
           ↓
      ┌────┴────┐
      ↓         ↓
[DisplayStateResolver]  [StopScoringService]
      ↓         ↓
  DoorOpen逻辑 距离误差计算
      ↓         ↓
CurrentStopStation PositionError
NextStation        TimeError
      ↓         ↓
    ┌───────────┘
    ↓
[MainWindow.UpdateDisplay()]
    ↓
┌─────────────────────────────────┐
│ if DoorOpen → "ただいま"        │
│ elif remainingDistance < 100m   │
│   → "まもなく"                 │
│ else → "次は"                   │
└─────────────────────────────────┘
    ↓
[UI显示 + 评分结算]
```

---

## 7. 相关常数定义

位置：[src/JRETS.Go.App/MainWindow.xaml.cs](src/JRETS.Go.App/MainWindow.xaml.cs#L34-L46)

```csharp
private const double NextAnnouncementDepartureDistanceMeters = 200;      // 发车声明触发距离
private const double ApproachAnnouncementRemainingDistanceMeters = 400;  // 接近声明的剩余距离
```

位置：[src/JRETS.Go.Core/Services/DebugRealtimeDataSource.cs](src/JRETS.Go.Core/Services/DebugRealtimeDataSource.cs#L8-L9)

```csharp
private const double DebugDepartureAdvanceMeters = 200;    // 发车时往前推进200米
private const double DebugApproachRemainingMeters = 100;   // 接近时距离目标还有100米
```

---

## 8. 测试用例参考

位置：[tests/JRETS.Go.Core.Tests/UnitTest1.cs](tests/JRETS.Go.Core.Tests/UnitTest1.cs)

### 完美停靠测试（第193-215行）
```csharp
[Fact]
public void ScoreStop_AtPerfectStop_GetsDoubleBonus()
{
    var station = new StationInfo { Id = 11, ... };
    var snapshot = new RealtimeSnapshot
    {
        CurrentDistanceMeters = 1000,
        TargetStopDistanceMeters = 1000,  // 完全匹配
        MainClockSeconds = 3600,
        TimetableHour = 1, TimetableMinute = 0, TimetableSecond = 0
    };
    
    var service = new StopScoringService();
    var result = service.ScoreStop(station, snapshot);
    
    Assert.Equal(50, result.PositionScore);   // 完美位置分
    Assert.Equal(50, result.TimeScore);        // 完美时间分
    Assert.Equal(120, result.FinalScore);      // 50+50+20奖励
}
```

### 位置时间误差测试（第162-189行）
```csharp
[Fact]
public void ScoreStop_UsesPositionAndTimeFiftyPointTables()
{
    var snapshot = new RealtimeSnapshot
    {
        CurrentDistanceMeters = 999.7,        // 误差 -0.3m
        TargetStopDistanceMeters = 1000,
        MainClockSeconds = 3605,              // 误差 +5秒
        TimetableHour = 1, TimetableMinute = 0, TimetableSecond = 0
    };
    
    var result = service.ScoreStop(station, snapshot);
    
    Assert.Equal(-0.3, result.PositionErrorMeters);
    Assert.Equal(5, result.TimeErrorSeconds);
    Assert.Equal(30, result.PositionScore);    // 表查询结果
    // ...
}
```

---

## 9. 记忆偏移量配置

位置：[src/JRETS.Go.Core/Configuration/MemoryOffsetsConfiguration.cs](src/JRETS.Go.Core/Configuration/MemoryOffsetsConfiguration.cs)

```csharp
public sealed class MemoryOffsets
{
    public required long NextStationId { get; init; }        // 下一站ID的内存偏移
    public required long DoorState { get; init; }            // 车门状态的内存偏移
    public required long CurrentDistance { get; init; }       // 当前距离的内存偏移
    public required long TargetStopDistance { get; init; }   // 目标停止距离的内存偏移
    // ... 其他偏移量
}
```

**默认偏移量示例：**
- NextStationId: `0x110B1D8`
- TargetStopDistance: `0x10BEDF0`

---

## 总结

| 功能点 | 关键文件 | 关键方法/属性 |
|-------|--------|-------------|
| **下一站位移计算** | DebugRealtimeDataSource.cs | ResolveCurrentStopDistance(), ResolveTargetStopDistance() |
| **DoorOpen判断** | DisplayStateResolver.cs | snapshot.DoorOpen → CurrentStopStation |
| **位置评分** | StopScoringService.cs | ScoreStop() - PositionScoreTable |
| **接近状态** | MainWindow.xaml.cs (line 982) | remainingDistance = TargetStopDistance - CurrentDistance |
| **三态循环** | DebugRealtimeDataSource.cs | DebugAdvance() → Stopped/Departed/Approaching |
| **状态转换** | MainWindow.xaml.cs (line 782) | DoorOpen变化时 |
