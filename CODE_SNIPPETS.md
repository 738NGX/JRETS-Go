# 关键代码片段集合

## 第1部分：下一站位移的计算与三态循环

### DebugRealtimeDataSource 中的位移数组管理

```csharp
// src/JRETS.Go.Core/Services/DebugRealtimeDataSource.cs

// 【初始化】构建每个车站的停靠距离数组
private static double[] BuildStationStopDisplacements(
    IReadOnlyList<StationInfo> stations,
    IReadOnlyDictionary<int, double>? stationDisplacementsMeters)
{
    var result = new double[stations.Count];
    var last = 0d;

    for (var i = 0; i < stations.Count; i++)
    {
        if (stationDisplacementsMeters is not null && stationDisplacementsMeters.TryGetValue(stations[i].Id, out var mapped))
        {
            last = mapped;
            result[i] = mapped;  // 从配置文件读取
            continue;
        }

        if (i == 0)
        {
            result[i] = 0;
            last = 0;
        }
        else
        {
            last += FallbackStationSpacingMeters;  // 默认 1000m/站 间距
            result[i] = last;
        }
    }

    return result;  // 结果：[0m, 1000m, 2000m, ...]
}

// 【查询1】获取当前停靠站的距离
private double ResolveCurrentStopDistance()
{
    // 示例：_currentStationIndex=1 → 返回1000m
    return _stationStopDisplacements[Math.Clamp(_currentStationIndex, 0, _stationStopDisplacements.Length - 1)];
}

// 【查询2】获取下一个目标站的距离
private double ResolveTargetStopDistance()
{
    // 示例：_currentStationIndex=1 → 返回2000m（下站）
    var targetIndex = Math.Min(_currentStationIndex + 1, _stationStopDisplacements.Length - 1);
    return _stationStopDisplacements[targetIndex];
}

// 【查询3】接近状态时的距离（目标站 - 100m）
private double ResolveApproachingDistance()
{
    var current = ResolveCurrentStopDistance();      // 1000m
    var target = ResolveTargetStopDistance();        // 2000m
    var approach = target - DebugApproachRemainingMeters;  // 2000-100 = 1900m
    
    if (approach <= current)  // 防止异常情况
    {
        approach = (current + target) / 2;  // 取中点
    }
    
    return approach;
}

// 【查询4】发车状态时的距离（当前站 + 200m）
private double ResolveDepartedDistance()
{
    var current = ResolveCurrentStopDistance();      // 1000m
    var target = ResolveTargetStopDistance();        // 2000m
    var departed = current + DebugDepartureAdvanceMeters;  // 1000+200 = 1200m
    return Math.Min(departed, target);  // 不超过下一站
}
```

### 三态循环完整实现

```csharp
/// <summary>
/// 推进状态：ただいま → 次は → まもなく → ただいま(Next)
/// 每次点击"Advance"按钮调用一次
/// </summary>
public void DebugAdvance()
{
    switch (_currentPhase)
    {
        case DebugPhase.Stopped:
            // 【状态1 → 状态2】停靠(ただいま) → 发车(次は)
            //   - 关闭车门
            //   - 列车轻微推进（200m）
            _currentPhase = DebugPhase.Departed;
            _doorOpen = false;
            _currentDistance = ResolveDepartedDistance();  // 当前位+200m
            break;

        case DebugPhase.Departed:
            // 【状态2 → 状态3】发车(次は) → 接近(まもなく)
            //   - 列车继续前进至接近范围
            _currentPhase = DebugPhase.Approaching;
            _currentDistance = ResolveApproachingDistance();  // 目标位-100m
            break;

        case DebugPhase.Approaching:
            // 【状态3 → 状态1】接近(まもなく) → 停靠(ただいま)
            //   - 到达下一站
            //   - 打开车门
            //   - 时间推进180秒
            if (_currentStationIndex < _stations.Count - 1)
            {
                _currentStationIndex++;  // 移到下一站
            }

            _currentPhase = DebugPhase.Stopped;
            _doorOpen = true;
            _currentDistance = ResolveCurrentStopDistance();  // 精确停在目标位
            _targetStopDistance = ResolveTargetStopDistance();  // 更新下一目标站
            _clockSeconds += 180;  // 模拟停靠3分钟
            break;
    }
}

public RealtimeSnapshot GetSnapshot()
{
    var current = _stations[_currentStationIndex];
    var timetable = TimeSpan.FromSeconds(_clockSeconds + 120);

    // 返回当前快照（供MainWindow读取）
    return new RealtimeSnapshot
    {
        CapturedAt = DateTime.Now,
        NextStationId = current.Id,
        DoorOpen = _doorOpen,
        MainClockSeconds = _clockSeconds,
        TimetableHour = timetable.Hours,
        TimetableMinute = timetable.Minutes,
        TimetableSecond = timetable.Seconds,
        CurrentDistanceMeters = _currentDistance,
        TargetStopDistanceMeters = _targetStopDistance,
        LinePath = null
    };
}
```

---

## 第2部分：DoorOpen 状态变化与评分捕获

### 状态变化检测和评分锁定

```csharp
// src/JRETS.Go.App/MainWindow.xaml.cs

// 【追踪状态】每帧保存前一帧的DoorOpen值
private bool _previousDoorOpen;
private DateTime? _lastDoorOpenTransitionAt;
private double? _activeApproachTargetStopDistance;       // 锁定的目标停止位置
private int? _activeApproachScheduledSeconds;            // 锁定的计划时刻
private int? _activeApproachStationId;                   // 锁定的评分车站ID
private bool _activeApproachOvershootFaultTriggered;     // 超车故障标记

public void OnDataSnapshotReceived(RealtimeSnapshot currentSnapshot, TrainDisplayState state)
{
    // ... 其他处理 ...

    // 【第1步】记录DoorOpen变化
    _previousDoorOpen = currentSnapshot.DoorOpen;
    if (_previousDoorOpen)  // 从关→开的变化
    {
        _lastDoorOpenTransitionAt = currentSnapshot.CapturedAt;
    }
    
    ResetAnnouncementState(currentSnapshot.DoorOpen);
    
    // 【第2步】捕获停靠评分
    CaptureStopScore(currentSnapshot, state);
    
    // ... UI更新等 ...
}

private void CaptureStopScore(RealtimeSnapshot snapshot, TrainDisplayState state)
{
    // 【关键】在开门瞬间锁定评分所需的所有数据
    bool doorOpenTransition = snapshot.DoorOpen && !_previousDoorOpen;

    if (doorOpenTransition)  // 仅在门打开时执行一次
    {
        // 获取当前停靠的车站
        // 如果state.CurrentStopStation已经为null（边界情况），用上一次记录的值
        var departureStationId = state.CurrentStopStation?.Id ?? _lastKnownStopStationId;
        
        // 【锁定1】锁定目标停止位置（用于后续评分）
        _activeApproachTargetStopDistance = snapshot.TargetStopDistanceMeters;
        
        // 【锁定2】锁定时刻表值（用于后续时间评分）
        _activeApproachScheduledSeconds = snapshot.TimetableHour * 3600 
                                        + snapshot.TimetableMinute * 60 
                                        + snapshot.TimetableSecond;
        
        // 【锁定3】锁定下一停靠车站（用于找到评分对象）
        _activeApproachStationId = ResolveNextStoppingStationFromCurrentId(departureStationId);
        
        _activeApproachOvershootFaultTriggered = false;
        ResetApproachDisplaySmoothing();
    }

    // 【监控】门关闭后，检查是否有超车（Overshoot）
    if (!snapshot.DoorOpen && _activeApproachTargetStopDistance is not null)
    {
        var approachPositionErrorSigned = snapshot.CurrentDistanceMeters 
                                        - _activeApproachTargetStopDistance.Value;
        
        if (approachPositionErrorSigned >= OvershootFaultMeters)  // 超过容差阈值
        {
            _activeApproachOvershootFaultTriggered = true;  // 标记故障
        }
    }

    _previousDoorOpen = snapshot.DoorOpen;

    // 【评分】在门打开的下一帧执行实际评分
    var normalizedApproachStationId = _activeApproachStationId.HasValue
        ? NormalizeStationIdForScoring(_activeApproachStationId.Value)
        : (int?)null;
    
    StationInfo? scoringStation = normalizedApproachStationId.HasValue
        ? _lineConfiguration.Stations.FirstOrDefault(x => x.Id == normalizedApproachStationId.Value)
        : null;

    if (!doorOpenTransition || scoringStation is null)
    {
        return;
    }

    if (_activeApproachTargetStopDistance is null || _activeApproachScheduledSeconds is null)
    {
        return;
    }

    if (_lastScoredStationId == scoringStation.Id)
    {
        return;  // 防止重复评分
    }

    // 【执行评分】
    var scoringSnapshot = BuildScoringSnapshot(snapshot);
    var stopScore = _stopScoringService.ScoreStop(scoringStation, scoringSnapshot);
    
    _stationScores.Add(stopScore);
    _runningTotalScore = Math.Round(_runningTotalScore + (stopScore.FinalScore ?? 0), 1);
    _lastScoredStationId = scoringStation.Id;
    
    // 清空锁定数据，准备下一个车站
    _activeApproachTargetStopDistance = null;
    _activeApproachScheduledSeconds = null;
    _activeApproachOvershootFaultTriggered = false;
}
```

### DisplayStateResolver中的DoorOpen逻辑

```csharp
// src/JRETS.Go.Core/Services/DisplayStateResolver.cs

public TrainDisplayState Resolve(LineConfiguration lineConfiguration, RealtimeSnapshot snapshot)
{
    var stations = lineConfiguration.Stations;
    
    // 【步骤1】根据NextStationId找到当前物理站的索引
    var currentIndex = Array.FindIndex(stations.ToArray(), x => x.Id == snapshot.NextStationId);

    if (currentIndex < 0)
    {
        currentIndex = 0;  // 未找到时默认第一站
    }

    var currentStation = stations.Count == 0 ? null : stations[currentIndex];
    var nextStation = currentIndex < stations.Count - 1 ? stations[currentIndex + 1] : null;
    
    // 【关键决策】DoorOpen决定CurrentStopStation
    var currentStopStation = snapshot.DoorOpen ? currentStation : null;
    // 说明：
    // - DoorOpen=true  → 有人在此站，返回currentStation
    // - DoorOpen=false → 列车已离站，返回null（此时显示下一站）

    // 【步骤2】生成显示文本
    var displayText = snapshot.DoorOpen
        ? currentStation is null ? "Door Open" : $"Stopped: {currentStation.NameJp}"
        : nextStation is null ? "Running" : $"Next: {nextStation.NameJp}";

    // 【步骤3】计算进度比例（用于进度条）
    var denominator = Math.Max(1, stations.Count - 1);
    var progressIndex = Math.Max(0, currentIndex);
    var progressRatio = Math.Clamp((double)progressIndex / denominator, 0.0, 1.0);

    return new TrainDisplayState
    {
        DoorOpen = snapshot.DoorOpen,
        CurrentStopStation = currentStopStation,  // 【核心】仅当DoorOpen时有值
        NextStation = nextStation,
        DisplayText = displayText,
        ProgressRatio = progressRatio
    };
}
```

---

## 第3部分：停靠评分的计算

### 位置和时间误差评分

```csharp
// src/JRETS.Go.Core/Services/StopScoringService.cs

public StationStopScore ScoreStop(StationInfo station, RealtimeSnapshot snapshot)
{
    // 【步骤1】计算位置误差（单位：米）
    var positionErrorSigned = snapshot.CurrentDistanceMeters - snapshot.TargetStopDistanceMeters;
    var positionError = Math.Abs(positionErrorSigned);
    
    // 示例：
    // CurrentDistance=1000.5m, TargetDistance=1000m
    // → positionErrorSigned = +0.5m (超前)
    // → positionError = 0.5m

    // 【步骤2】计算时间误差（单位：秒）
    var scheduledSeconds = snapshot.TimetableHour * 3600 
                         + snapshot.TimetableMinute * 60 
                         + snapshot.TimetableSecond;
    var timeErrorSigned = snapshot.MainClockSeconds - scheduledSeconds;
    var timeError = Math.Abs(timeErrorSigned);
    
    // 示例：
    // MainClock=3605s (1:00:05), Scheduled=3600s (1:00:00)
    // → timeErrorSigned = +5s (晚到)
    // → timeError = 5s

    // 【步骤3】转换位置误差为厘米（评分表使用厘米）
    var positionErrorCm = positionError * 100;  // 0.5m → 50cm

    // 【步骤4】通过评分表查询分数
    var positionScore = StepScoreFromTable(positionErrorCm, PositionScoreTable);
    // 从表中查询：50cm → 介于 30cm(40分) 和 60cm(35分) 之间 → 35分
    
    var timeScore = StepScoreFromTable(timeError, TimeScoreTable);
    // 从表中查询：5s → 介于 3s(50分) 和 6s(45分) 之间 → 45分

    // 【步骤5】计算完美停靠奖励
    var perfectBonus = 0;

    if (positionErrorCm <= PerfectPositionToleranceCm)  // <= 0.0001cm（几乎完美）
    {
        perfectBonus += 10;
    }

    if (timeError == 0)  // 时间完全匹配
    {
        perfectBonus += 10;
    }

    // 【步骤6】计算最终评分
    var finalScore = Math.Round(positionScore + timeScore + perfectBonus, 1);
    // 最终 = 35 + 45 + 0 = 80.0

    // 【步骤7】返回详细评分结果
    return new StationStopScore
    {
        StationId = station.Id,
        StationName = station.NameJp,
        CapturedAt = snapshot.CapturedAt,
        ScheduledArrivalSeconds = scheduledSeconds,        // 1:00:00 → 3600s
        ActualArrivalSeconds = snapshot.MainClockSeconds,  // 1:00:05 → 3605s
        PositionErrorMeters = Math.Round(positionErrorSigned, 2),  // +0.50m
        TimeErrorSeconds = timeErrorSigned,                // +5s
        PositionScore = Math.Round(positionScore, 1),      // 35.0分
        TimeScore = Math.Round(timeScore, 1),              // 45.0分
        FinalScore = finalScore,                           // 80.0分
        IsScoredStop = true
    };
}

// 【工具方法】通过表查询分数
private static double StepScoreFromTable(double errorValue, IReadOnlyList<(double ErrorThreshold, double Score)> table)
{
    // 通过二分查找找到对应的分数
    // 如果errorValue超过表中最大值，返回0
    // 如果errorValue在表中两个值之间，返回较低一页的分数
    
    // 位置评分表示例：
    // (5cm, 50分), (15cm, 45分), (30cm, 40分), ..., (480cm, 0分)
    // errorValue = 50cm → 返回 35分（30cm表项对应的分数）
    
    for (var i = 0; i < table.Count; i++)
    {
        if (errorValue <= table[i].ErrorThreshold)
        {
            return table[i].Score;
        }
    }

    return 0;  // 超过最大限制
}
```

### 评分表定义

```csharp
// 位置误差评分表（单位：厘米）
private static readonly (double ErrorCm, double Score)[] PositionScoreTable =
[
    (5, 50),         // 误差 ≤ 5cm    → 50分（完美）
    (15, 45),        // 误差 ≤ 15cm   → 45分（很好）
    (30, 40),        // 误差 ≤ 30cm   → 40分（良好）
    (60, 35),        // 误差 ≤ 60cm   → 35分
    (90, 30),        // 误差 ≤ 90cm   → 30分
    (120, 25),       // 误差 ≤ 120cm  → 25分
    (180, 20),       // 误差 ≤ 180cm  → 20分
    (240, 15),       // 误差 ≤ 240cm  → 15分
    (300, 10),       // 误差 ≤ 300cm  → 10分
    (360, 5),        // 误差 ≤ 360cm  → 5分
    (480, 0)         // 误差 ≤ 480cm  → 0分
];

// 时间误差评分表（单位：秒）
private static readonly (double ErrorSeconds, double Score)[] TimeScoreTable =
[
    (3, 50),         // 误差 ≤ 3秒   → 50分（完美）
    (6, 45),         // 误差 ≤ 6秒   → 45分（很好）
    (12, 40),        // 误差 ≤ 12秒  → 40分（良好）
    (18, 35),        // 误差 ≤ 18秒  → 35分
    (24, 30),        // 误差 ≤ 24秒  → 30分
    (36, 25),        // 误差 ≤ 36秒  → 25分
    (48, 20),        // 误差 ≤ 48秒  → 20分
    (60, 15),        // 误差 ≤ 60秒  → 15分
    (90, 10),        // 误差 ≤ 90秒  → 10分
    (120, 5),        // 误差 ≤ 120秒 → 5分
    (180, 0)         // 误差 ≤ 180秒 → 0分
];
```

---

## 第4部分：接近状态的判断与UI更新

### 三状态显示逻辑

```csharp
// src/JRETS.Go.App/MainWindow.xaml.cs - UpdateDisplay() 方法节选

private void UpdateDisplay()
{
    // ... 前置代码：获取当前display state ...
    var state = _displayStateResolver.Resolve(_lineConfiguration, snapshot);

    // 【决定显示的站点和状态文本】
    StationInfo? displayStation = null;
    string statusText = "次は";
    
    // 获取下一个需要停靠的车站
    var nextStoppingStation = ResolveAnnouncementTargetStationId(state) is int nextStoppingStationId
        ? _lineConfiguration.Stations.FirstOrDefault(x => x.Id == nextStoppingStationId)
        : null;

    // ========================================
    // 【分支1】车门打开 → 显示"ただいま"（停靠）
    // ========================================
    if (snapshot.DoorOpen && state.CurrentStopStation is not null)
    {
        displayStation = state.CurrentStopStation;
        statusText = "ただいま";
        // 示例：向用户指示"现在停靠在大宫站"
    }
    // ========================================
    // 【分支2】车门关闭 → 判断距离
    // ========================================
    else if (!snapshot.DoorOpen && nextStoppingStation is not null)
    {
        // 计算到下一停靠站的剩余距离
        var remainingDistance = snapshot.TargetStopDistanceMeters 
                              - snapshot.CurrentDistanceMeters;
        
        // 【子分支2.1】接近状态：< 100米
        if (remainingDistance < 100)
        {
            displayStation = nextStoppingStation;
            statusText = "まもなく";
            // 示例：
            // TargetStop=2000m, CurrentDist=1950m
            // → remainingDistance = 50m < 100m
            // → 显示"まもなく"（即将到达埼玉新都心站）
        }
        // 【子分支2.2】正常状态：>= 100米
        else
        {
            displayStation = nextStoppingStation;
            statusText = "次は";
            // 示例：
            // TargetStop=3000m, CurrentDist=1500m
            // → remainingDistance = 1500m >= 100m
            // → 显示"次は"（下一站）
        }
    }
    // ========================================
    // 【分支3】降级处理
    // ========================================
    else
    {
        // 当无法确定停靠站时，使用物理意义的下一站
        displayStation = state.NextStation;
        statusText = "次は";
    }

    // 【更新UI】
    NextStationStatusTextBlock.Text = statusText;  // ただいま/次は/まもなく
    NextStationNameTextBlock.Text = displayStation?.NameJp ?? "--";

    // 更新号码牌（若有站码信息）
    var hasStationCode = displayStation is not null && !string.IsNullOrWhiteSpace(displayStation.Code);
    var lineCodeText = _lineConfiguration.LineInfo.Code;
    var lineNumberText = displayStation is null ? string.Empty : displayStation.Number.ToString("00");
    var hasLineCode = !string.IsNullOrWhiteSpace(lineCodeText);
    var hasLineNumber = !string.IsNullOrWhiteSpace(lineNumberText);
    var showStationCodeBadge = hasStationCode && hasLineCode && hasLineNumber;

    StationCodeBadgeOuterBorder.Visibility = showStationCodeBadge ? Visibility.Visible : Visibility.Collapsed;
    StationCode.Text = showStationCodeBadge ? displayStation!.Code! : string.Empty;
    LineCode.Text = showStationCodeBadge ? lineCodeText : string.Empty;
    LineNumberBadgeTextBlock.Text = showStationCodeBadge ? lineNumberText : string.Empty;

    // ... 继续处理声音播报、动画等 ...
}
```

### 声音播报的触发距离

```csharp
private void HandleAutoAnnouncements(RealtimeSnapshot snapshot, TrainDisplayState state)
{
    // 【声音播报点1】发车声明（Departure）
    // 条件：列车已离开停靠站，走行距离达到200m
    var departureStartDistance = _announcementDepartureStartDistanceMeters ?? snapshot.CurrentDistanceMeters;
    var traveledDistance = Math.Abs(snapshot.CurrentDistanceMeters - departureStartDistance);
    
    if (traveledDistance >= NextAnnouncementDepartureDistanceMeters  // 200m
        && _lastNextAnnouncementStationId != stationId)
    {
        // 播放：「次は、埼玉新都心です」
        if (TryPlayStationAnnouncement(stationId, paIndex: 0))
        {
            _lastNextAnnouncementStationId = stationId;
        }
    }

    // 【声音播报点2】接近声明（Approach）
    // 条件：列车接近下一站，剩余距离 <= 设定值（通常400m）
    var remainingDistance = snapshot.TargetStopDistanceMeters - snapshot.CurrentDistanceMeters;
    var approachTriggerDistance = ResolveAnnouncementTriggerDistanceMeters(
        stationId,
        paIndex: 1,
        defaultDistanceMeters: ApproachAnnouncementRemainingDistanceMeters);  // 默认400m

    if (remainingDistance <= approachTriggerDistance && !_approachAnnouncementTriggered)
    {
        // 播放：「まもなく、埼玉新都心に到着いたします」
        if (TryPlayStationAnnouncement(stationId, paIndex: 1))
        {
            _approachAnnouncementTriggered = true;
        }
    }
}
```

---

## 第5部分：完整的评分流程示例

```csharp
/* 完整示例：从snapshot到评分的整个流程 */

// 【场景】列车停靠在埼玉新都心站（ID:33202）
// 当前数据：
var snapshot = new RealtimeSnapshot
{
    CapturedAt = DateTime.Now,
    NextStationId = 33202,
    DoorOpen = true,           // 门打开
    MainClockSeconds = 3605,   // 1:00:05
    TimetableHour = 1,
    TimetableMinute = 0,
    TimetableSecond = 3,       // 计划1:00:03
    CurrentDistanceMeters = 10000.25,
    TargetStopDistanceMeters = 10000.0
};

// 【第1阶段】DisplayStateResolver.Resolve()
var state = resolver.Resolve(lineConfiguration, snapshot);
// 结果：
// - CurrentStopStation = 33202站（因为DoorOpen=true）
// - NextStation = 33203站
// - DisplayText = "Stopped: 埼玉新都心"

// 【第2阶段】评分捕获（CaptureStopScore）
// 在门打开时（doorOpenTransition=true）锁定：
_activeApproachTargetStopDistance = 10000.0;      // 锁定目标位
_activeApproachScheduledSeconds = 3603;           // 计划秒数(1:00:03)
_activeApproachStationId = 33203;                 // 下一停靠站

// 【第3阶段】UI显示（UpdateDisplay）
// 因为DoorOpen=true，显示：
NextStationStatusTextBlock.Text = "ただいま";
NextStationNameTextBlock.Text = "埼玉新都心";
// → UI显示"ただいま 埼玉新都心"

// 【第4阶段】进入下一个车站（门打开 → 门关闭 → 接近 → 门打开）
// 步骤1：门关闭，列车出发
snapshot2 = new RealtimeSnapshot
{
    NextStationId = 33202,
    DoorOpen = false,
    CurrentDistanceMeters = 10200.0,     // 前进了200m
    TargetStopDistanceMeters = 11000.0   // 更新到次下一站
};
state2 = resolver.Resolve(lineConfiguration, snapshot2);
// - CurrentStopStation = null（门关闭）
// - NextStation = 33203站

// UpdateDisplay()检查距离：
// remainingDistance = 11000.0 - 10200.0 = 800m >= 100m
// → 显示"次は 33203 (XXXX駅)"

// 步骤2：列车接近
snapshot3 = new RealtimeSnapshot
{
    NextStationId = 33202,
    DoorOpen = false,
    CurrentDistanceMeters = 10950.0,     // 共前进了750m
    TargetStopDistanceMeters = 11000.0
};

// UpdateDisplay()检查距离：
// remainingDistance = 11000.0 - 10950.0 = 50m < 100m
// → 显示"まもなく 33203 (XXXX駅)"

// 步骤3：到达并停靠
snapshot4 = new RealtimeSnapshot
{
    NextStationId = 33203,
    DoorOpen = true,
    CurrentDistanceMeters = 11000.1,    // 精确停靠（误差 +0.1m）
    TargetStopDistanceMeters = 11000.0,
    MainClockSeconds = 3725,            // 1:02:05
    TimetableHour = 1,
    TimetableMinute = 2,
    TimetableSecond = 0                 // 计划1:02:00
};

// 【第5阶段】CaptureStopScore()执行评分
// doorOpenTransition = true（从false→true）
// 立即计算分数
var station = lineConfiguration.Stations.First(s => s.Id == 33203);
var stopScore = stopScoringService.ScoreStop(station, snapshot4);

// 计算过程：
double positionErrorSigned = 11000.1 - 11000.0 = 0.1m;
double positionError = 0.1m = 10cm;
var positionScore = PositionScoreTable.Query(10cm) = 50分;

int scheduledSeconds = 1*3600 + 2*60 + 0 = 3720s;
int timeErrorSigned = 3725 - 3720 = 5s;
var timeScore = TimeScoreTable.Query(5s) = 45分;

var perfectBonus = 0;  // 没有完美奖励（误差>0.0001cm, 时间误差>0）

var finalScore = 50 + 45 + 0 = 95.0;

// 结果：
stopScore = {
    StationId = 33203,
    StationName = "埼玉新都心",
    PositionErrorMeters = 0.10,
    TimeErrorSeconds = 5,
    PositionScore = 50.0,
    TimeScore = 45.0,
    FinalScore = 95.0
};

// 总评分更新：
_runningTotalScore += 95.0;  // 累计评分
_stationScores.Add(stopScore);

// UI显示评分结果
BeginStopSettlementAnimation(stopScore, previousTotal, currentTotal);
```

---

这些代码片段展示了：
1. **位移计算**：如何从车站配置构建位移数组和查询
2. **状态变化**：DoorOpen如何激发评分捕获
3. **评分计算**：位置和时间误差如何转换为分数
4. **接近判断**：距离阈值如何决定UI显示
5. **完整流程**：从快照到最终评分的全路径
