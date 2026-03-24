# 列车HUD显示逻辑改进总结

## 改动内容

### 1. 显示逻辑优化 (NextStationStatusTextBlock & NextStationNameTextBlock)

将固定的"次は"显示改为根据列车状态动态显示，有三种状态：

**状态判断优先级：**
1. **ただいま（现在）** - 当车门开启且在停靠站时
   - 显示当前停靠车站名称
   - 代表列车正停靠在此站，可上下车

2. **まもなく（即将）** - 当车门关闭且距离下一站<350m时
   - 显示下一个车站名称
   - 代表列车正快速接近下一站

3. **次は（下一个）** - 其他情况
   - 显示下一个车站名称
   - 常规运行状态

### 2. Debug模式按钮合并

将原来的两个独立按钮：
- "Depart"（发车）
- "Arrive"（到站）

合并为一个单一的按钮：
- "Advance"（推进）

**工作流程：**
按钮每次点击依次推进到下一个阶段：
```
ただいま（开门） 
  ↓ [Advance]
次は（关门）
  ↓ [Advance] 
まもなく（接近）
  ↓ [Advance]
ただいま（下一站，开门）
  ↓ [Advance]
次は（关门）
  ↓ ...
```

## 技术实现细节

### DebugRealtimeDataSource.cs 更改

添加了`DebugPhase`枚举来追踪当前阶段：
- `Stopped` - 停靠，开门
- `Departed` - 发车，关门
- `Approaching` - 接近下一站

新增`DebugAdvance()`方法，通过状态机依次转换各个阶段。

### MainWindow.xaml.cs 更改

1. 修改`UpdateDisplay()`方法中的显示逻辑
   - 根据`snapshot.DoorOpen`和`snapshot.TargetStopDistanceMeters`判断当前状态
   - 使用相应的车站信息和状态文本更新UI

2. 替换事件处理器
   - 删除`DebugDepartClick()`和`DebugArriveClick()`
   - 新增`DebugAdvanceClick()`

### MainWindow.xaml 更改

更新Debug Tools面板：
- 移除两个独立按钮（Depart, Arrive）
- 替换为单一的"Advance"按钮，宽度280px

## 测试结果

✅ 所有8个单元测试通过
✅ 无编译错误或警告
✅ 状态转换逻辑验证完毕

## 使用说明

在Debug模式下，每点击"Advance"按钮一次，列车会推进到下一个阶段，HUD显示会相应更新以反映当前的运输模式（停靠/行驶/接近）。
