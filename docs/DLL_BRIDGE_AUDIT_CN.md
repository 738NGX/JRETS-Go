# DLL Bridge 可行性审计

审计日期：2026-08-20  
审计范围：本地已安装的 JR EAST Train Simulator build `24443769`；只读静态 PE 分析和只读模块枚举。未注入、未挂钩、未修改游戏进程。

## 结论

**可行，但不应直接把它当作稳定的官方插件接口。**

`TrainUnit_DLL.dll` 保留了丰富的 x64 C++ 导出符号，并且在游戏运行时已加载。因此可以把它作为一个比裸内存地址更强的语义锚点：优先观察列车状态消息和仿真帧，而不是在主程序内扫描独立字段。

不过没有发现公开、文档化的插件加载 ABI 或 IPC 接口。Bridge 仍需要非官方的进程内加载与一次性的 ABI/消息验证；游戏更新可能改变 C++ ABI、消息编号或调用路径。它应是可选数据源，必须有严格的版本指纹和故障关闭机制。

## 审计证据

| 文件 | 架构 | 导出 | 运行时已加载 | 观察 |
| --- | --- | ---: | --- | --- |
| `JREAST_TrainSimulator.exe` | x64 | 0 | 是 | 主程序没有公开导出接口。 |
| `TrainUnit_DLL.dll` | x64 | 583 | 是 | 存在未剥离的 C++ 类、方法与 vtable 符号，是最有价值的候选。 |
| `Station.dll` | x64 | 0 | 是 | 体积大（约 505 MB），没有可直接使用的公开导出。 |
| `EncryptProcess.dll` | x64 | 12 | 是 | 提供加/解密函数，主程序和 `TrainUnit_DLL.dll` 都依赖它。 |

四个文件都启用了 ASLR（`DYNAMIC_BASE`）、DEP（`NX_COMPAT`）和高熵 VA；因此任何方案都不能保存绝对虚拟地址。审计范围内没有发现 CFG 标志，但这不构成实施 detour 或注入的授权理由。

### `TrainUnit_DLL.dll` 的高价值符号

以下符号由导出表直接提供，名称来自二进制而非猜测：

- `TrainBody::CGetCurFrame()` / `TrainBody::CSetCurFrame(double)`
- `TrainBody::execute()`、`initialize()`、`reset()`、`render()`
- `TrainBody::sendMsgValueToTrainState(...)` / `setMsgValueFromTrainState(...)`
- `TrainBody::sendMsgValueToEvDirect(...)` / `setMsgValueFromEvDirect(...)`
- `TrainBody::getObserversNameFromMsg(...)`
- `AddObserverToMediator(...)`、`SendToMediator(...)`
- `TrainUnit_GetEvdDefCount()`、`TrainUnit_GetTrainStateDefCount()`、`TrainUnitSetFPS(...)`

同时存在 `BaseEntity`、`TrainBody` 和多种具体车辆类的 vtable 符号。它们表明列车状态通过消息/观察者机制在内部传播；相比读取零散 RVA，这更适合作为时间、车门与列车状态的事件边界。

## 风险评估

1. 导出的是 C++ ABI，不是稳定的 C API。调用成员函数需要正确的对象实例、x64 调用约定和类布局；不能只凭函数名直接调用。
2. `EncryptProcess.dll` 与 `IsDebuggerPresent` 导入说明存在保护和反调试相关逻辑。现有证据不足以断言有反作弊或禁止注入，但也不能假定注入一定安全或符合游戏许可条款。
3. 直接安装内联 Hook、修改代码页或创建远程线程的兼容性和风险最高，且容易被更新、保护逻辑或安全软件阻断。
4. 游戏更新仍可能变更消息 ID、对象构造路径和函数实现；需要以版本指纹隔离 Profile，不能跨版本盲用。

## 推荐的 Bridge 路线

### 第一阶段：被动消息观察（首选 PoC）

只针对 `TrainBody::sendMsgValueToTrainState` 或 `SendToMediator` 建立一个**只记录参数**的观测点。目标是先获得消息 ID、数值类型、触发频率和与驾驶场景事件的对应关系；不调用游戏函数、不改写状态、不拦截输入。

若这些消息已经包含车门、运行状态、帧位置或时钟，则 Bridge 可以直接输出语义事件。

### 第二阶段：小型 x64 Bridge

Bridge 只负责：

1. 校验当前模块指纹和已批准的 Profile；
2. 定位一个已验证的消息入口；
3. 将经过类型和范围校验的只读快照发往本机 Named Pipe；
4. 任意定位或校验失败时自行关闭观测，不触碰游戏状态。

JRETS-Go 将 Pipe 数据源作为首选，现有外部内存读取只保留为开发诊断回退。

### 第三阶段：版本修补

每个 Profile 保存：模块 SHA-256、入口 AOB、参数布局、已识别消息 ID 与验证规则。普通更新只需要重新验证 AOB；若调用路径变化，修补 Profile 即可。只有内部消息模型被重写时才需要重新分析。

## 不建议的做法

- 不要对每个字段分别安装 Hook 或保存绝对地址。
- 不要在 Bridge 中调用未验证的 C++ 成员函数。
- 不要默认绕过保护、反调试或游戏许可限制。
- 不要在定位失败后退回到猜测地址继续读取。

## 下一步决策点

在开始任何注入式 PoC 前，应先确认用户接受非官方进程内加载的兼容性与许可风险。若接受，第一项工作应仅验证 `TrainBody` 消息入口在当前埼京线驾驶场景中的参数，不导出或修改游戏逻辑。
