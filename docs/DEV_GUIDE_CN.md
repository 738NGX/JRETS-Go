# 开发者文档

## 自动更新总览

当前版本采用三通道强制更新：

1. 程序通道（App）
2. 配置通道（Configs）
3. 音频通道（Audio）

启动时会检查 GitHub Release 的目标版本。若任一通道落后，客户端会执行下载与校验；配置和音频先在主程序内应用，程序通道通过外部更新器完成替换和重启。

## Release 资产规范

请在每次发布中提供以下资产：

1. app-win-x86.zip
2. configs.zip
3. audio-manifest.json
4. audio-files-*.zip（可多个）
5. release-state.json
6. checksums.txt

### release-state.json

该文件定义三通道目标版本，例如：

{
  "appVersion": "1.1.0",
  "configsVersion": "2026.03.30.2",
  "audioPackVersion": "2026.03.30.5"
}

### checksums.txt

采用 SHA256 清单。建议每行格式：

<sha256>  <fileName>

其中 fileName 需与 Release 资产名称一致。

## 本地配置文件

客户端读取 src/JRETS.Go.App/configs/update.yaml。

关键字段：

1. github.owner / github.repo
2. release_state_asset_name
3. mandatory
4. check_timeout_seconds
5. max_retry_count
6. app.asset_pattern
7. configs.asset_pattern
8. audio.manifest_name / audio.package_pattern

如果仍使用占位值（例如 your-org），客户端会跳过远程更新检查。

## 构建与发布步骤

1. 构建解决方案
2. 发布 App 目录产物（用于打包 app-win-x86.zip）
3. 打包 configs 目录为 configs.zip
4. 按清单打包音频增量为 audio-files-*.zip
5. 生成 audio-manifest.json（包含 files、deleteFiles、sha256）
6. 生成 release-state.json
7. 生成 checksums.txt
8. 上传到 GitHub Release

## 本地演练脚本

仓库内提供脚本：scripts/Build-ReleaseAssets.ps1

用途：

1. 生成 release-state.json
2. 生成 checksums.txt
3. 校验关键资产是否齐全

示例：

powershell -ExecutionPolicy Bypass -File scripts/Build-ReleaseAssets.ps1 -ReleaseDir .\artifacts\release -AppVersion 1.1.0 -ConfigsVersion 2026.03.30.2 -AudioPackVersion 2026.03.30.5

## 一键构建与打包

仓库内提供一键脚本：scripts/Build-OneClickRelease.ps1

该脚本会自动执行：

1. 构建 updater
2. 发布 app（目录发布）
3. 打包 app-win-x86.zip
4. 打包 configs.zip
5. 打包 audio-files-base.zip 并生成 audio-manifest.json
6. 调用 Build-ReleaseAssets.ps1 生成 release-state.json 与 checksums.txt

示例：

powershell -ExecutionPolicy Bypass -File scripts/Build-OneClickRelease.ps1 -ReleaseDir .\artifacts\release -AppVersion 1.1.0 -ConfigsVersion 2026.03.31.1 -AudioPackVersion 2026.03.31.5

## 更新器行为说明

程序通道由 JRETS.Go.Updater 执行：

1. 等待主进程退出
2. 解压 app 包
3. 备份当前目录
4. 覆盖更新（跳过 updater 自身目录）
5. 失败回滚
6. 写入本地状态文件
7. 重启主程序

## 日志与状态文件

1. 状态文件：%LocalAppData%\JRETS.Go.App\update-state.json
2. 更新器日志：%LocalAppData%\JRETS.Go.App\logs\updater.log

## 音频文件更新指南

### 新增或更新音频文件

当线路音频需要更新时（例如新增 "shinjuku" 线路或修改现有线路的语音），执行以下步骤：

#### 第一步：更新本地音频文件

将新增或修改后的音频文件放置于：

```
src/JRETS.Go.App/audio/
  ├── keihin-negishi/          # 现有线路
  │   ├── stations/
  │   ├── operations/
  │   └── ...
  ├── yamanote/                # 现有线路
  │   ├── stations/
  │   ├── operations/
  │   └── ...
  ├── shinjuku/                # 新增线路示例
  │   ├── stations/
  │   │   ├── shinjuku.mp3
  │   │   ├── yotsuya.mp3
  │   │   └── ...
  │   └── operations/
  │       └── ...
  └── melodies/                # 全局音效
```

#### 第二步：运行构建脚本

使用一键打包脚本，脚本会自动：
- 扫描所有新增/修改的音频文件
- 计算 SHA256 校验和
- 生成 audio-manifest.json
- 打包为 audio-files-base.zip
- 更新版本号

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-OneClickRelease.ps1 `
  -ReleaseDir .\artifacts\release `
  -AppVersion 1.0.0 `
  -ConfigsVersion 2026.04.15.1 `
  -AudioPackVersion 2026.04.15.1
```

#### 第三步：查看生成的清单

脚本会生成 `artifacts/release/audio-manifest.json`，内容示例：

```json
{
  "version": "2026.04.15.1",
  "files": [
    {
      "relativePath": "shinjuku/stations/shinjuku.mp3",
      "sha256": "a1b2c3d4...",
      "sizeBytes": 125000,
      "packageName": "audio-files-base.zip"
    },
    ...
  ],
  "deleteFiles": [],
  "minimumClientVersion": "1.0.0",
  "publishedAt": "2026-04-15T10:30:00Z"
}
```

### 删除过时音频文件

如果需要删除某些过时的音频文件（例如保留旧线路向后兼容），在生成新 release 前修改脚本逻辑或手动编辑生成后的 audio-manifest.json：

```json
{
  "version": "2026.04.15.1",
  "files": [...],
  "deleteFiles": [
    "old-line/stations/retired-station.mp3",
    "test-audio/experimental.mp3"
  ],
  ...
}
```

客户端启动时会自动检测到这些删除指令并清理本地文件。

### 增量音频打包（推荐）

当前一键脚本已支持音频增量模式，不需要按线路拆很多包。做法是：

1. 读取上一版 `audio-manifest.json`（作为对比基线）
2. 仅把“新增/被修改”的文件打入 `audio-files-*.zip`
3. 自动把“上一版存在、当前已删除”的文件写入 `deleteFiles`
4. 自动按体积分片（默认 80MB/包），避免单包过大

示例：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-OneClickRelease.ps1 `
  -ReleaseDir .\artifacts\release `
  -AppVersion 1.2.0 `
  -ConfigsVersion 2026.04.15.1 `
  -AudioPackVersion 2026.04.20.1 `
  -AudioPackageMode delta `
  -PreviousAudioManifestPath .\artifacts\prev\audio-manifest.json
```

可选参数：

1. `-AudioPackageMode full|delta`：默认 `full`，建议发版使用 `delta`
2. `-AudioPackageMaxSizeMb`：单个音频包最大体积，默认 `80`

说明：

1. 修改旧线路音频文件时，只要文件内容变了（SHA256 变化），就会自动进入增量包并覆盖客户端旧文件。
2. 删除旧音频文件时，会自动写入 `deleteFiles`，客户端应用更新时会删除本地文件。
3. 若本次仅删除无新增，脚本会生成最小占位包，保证更新通道资产校验通过。

### 应用端行为

客户端应用重启时，如果检测到新的 audioPackVersion：

1. 下载新的 audio-manifest.json 和 audio-files-*.zip
2. 校验 zip 内所有文件的 SHA256
3. 按 manifest 指示：
   - **添加/覆盖**：将文件复制到 audio/ 目录
   - **删除**：移除指定的本地文件
4. 再次校验已应用的每个文件
5. 更新本地 update-state.json 中的 audioPackVersion

整个过程无需重启应用，配置和音频通道采用**增量更新**，只有程序通道更新时才需重启。

## 配置文件更新指南

### 配置文件结构

配置文件位于 `src/JRETS.Go.App/configs/`，主要包含：

```
configs/
  ├── lines-path.yaml          # 线路路径映射（.tse文件关联）
  ├── memory-offsets.yaml      # 内存偏移量配置
  ├── update.yaml              # 更新系统配置
  ├── lines/                   # 线路参数目录
  │   ├── keihin-negishi.yaml
  │   ├── yamanote.yaml
  │   └── ...
  └── map/                     # 地图资源
      ├── map.html
      └── ...
```

### 更新配置文件的流程

#### 场景1：修改线路映射 (lines-path.yaml)

当 JREAST 模拟器版本更新导致路径文件名变化时：

1. **修改 lines-path.yaml**
   ```yaml
   paths:
     - keihintohoku/latest_version/727B.tse:  # 例如将 omiya_ofuna 改为 latest_version
         - line_id: keihin-negishi
           train_id: 727B
   ```

2. **可选：同时修改线路参数** (configs/lines/*.yaml)
   - 调整站点间距离：line_distance_m
   - 修改加速度：max_acceleration_ms2
   - 更新勾选参数等

#### 场景2：更新内存偏移量 (memory-offsets.yaml)

当游戏版本更新导致内存结构变化时：

```yaml
offsets:
  next_station_id: "0x112BE58"      # 下列车 ID 偏移
  door_state: "0x1552440"           # 车门状态偏移
  main_clock_seconds: "0x14CBB04"   # 时钟秒数偏移
  # ... 其他偏移量
```

使用 Cheat Engine 工程文件重新扫描获得新的偏移量。

#### 场景3：配置自动更新系统 (update.yaml)

修改更新源、超时、重试次数等：

```yaml
github:
  owner: your-org
  repo: your-repo

release_state_asset_name: release-state.json

mandatory: true  # 是否强制更新
check_timeout_seconds: 30
max_retry_count: 3

app:
  asset_pattern: "app-win-x86.zip"
  # app 通道采用外部更新器处理

configs:
  asset_pattern: "configs.zip"
  # 配置进程内更新

audio:
  manifest_name: "audio-manifest.json"
  package_pattern: "audio-files-*.zip"
```

#### 发布配置更新

修改完配置文件后，运行构建脚本：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-OneClickRelease.ps1 `
  -ReleaseDir .\artifacts\release `
  -AppVersion 1.0.0 `
  -ConfigsVersion 2026.04.15.1 `
  -AudioPackVersion 2026.03.31.1
```

脚本会自动：
1. 扫描 `src/JRETS.Go.App/configs/` 目录下所有文件
2. 生成新的 `configs.zip` （包含所有配置）
3. 计算 SHA256 校验和
4. 更新 `checksums.txt`

### 应用端行为

客户端启动时，如果检测到新的 configsVersion：

1. 下载新的 `configs.zip`
2. 校验 zip 内所有文件的 SHA256
3. 解压到临时目录
4. 将整个 `configs` 目录替换为新版本
5. 再次校验已应用的每个文件
6. 更新本地 `update-state.json` 中的 `configsVersion`

新配置立即生效，无需重启应用。如果配置变更影响 TLE/地图，下次会话时会使用新参数。

## 程序更新指南

### 程序版本号管理

程序版本号定义在 `JRETS.Go.App.csproj`：

```xml
<PropertyGroup>
  <Version>1.1.0</Version>
  <FileVersion>1.1.0.0</FileVersion>
  <ProductVersion>1.1.0.0</ProductVersion>
</PropertyGroup>
```

版本号格式：`MAJOR.MINOR.PATCH`（可选 BUILD 号）

### 发布程序更新

#### 第一步：修改源代码并测试

- 修改业务逻辑、UI、服务等代码
- 本地 `dotnet build` 和 `dotnet test` 验证
- 确保所有单元测试通过

#### 第二步：更新版本号

修改 `src/JRETS.Go.App/JRETS.Go.App.csproj` 中的版本号：

```xml
<Version>1.2.0</Version>
```

同时更新 `AssemblyInfo.cs` 中的版本信息（如果有）。

#### 第三步：一键打包发布

运行构建脚本，指定新版本号：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-OneClickRelease.ps1 `
  -ReleaseDir .\artifacts\release `
  -AppVersion 1.2.0 `
  -ConfigsVersion 2026.04.15.1 `
  -AudioPackVersion 2026.03.31.1
```

脚本会自动：
1. 编译 Updater 项目
2. 发布 App 项目至目录（启用 ReadyToRun，禁用 SingleFile）
3. 打包为 `app-win-x86.zip` （约 30+ MB）
4. 打包配置和音频
5. 生成 `release-state.json` 和 `checksums.txt`

#### 第四步：上传到 GitHub Release

将 `artifacts/release/` 目录下的六个资产上传到 GitHub Release：

- `app-win-x86.zip` （新程序包）
- `configs.zip`
- `audio-files-base.zip`
- `audio-manifest.json`
- `release-state.json`
- `checksums.txt`

### 程序更新过程

客户端启动时，如果检测到新的 appVersion：

1. **启动时检查**
   - 下载 `release-state.json` 和 `checksums.txt`
   - 对比本地 `update-state.json` 中的 `appVersion`
   - 若版本落后，标记为待更新

2. **下载与验证**
   - 下载 `app-win-x86.zip` 至临时目录
   - 验证 SHA256 校验和

3. **启动外部更新器**
   - 主程序调用 `AppUpdateHandoffService`
   - 启动 `JRETS.Go.Updater.exe`
   - 传递更新包路径、主程序 PID、新版本号等参数

4. **主程序正常关闭**
   - 执行必要的清理操作（关闭数据库连接、保存状态等）
   - 调用 `Application.Current.Shutdown()`

5. **更新器执行替换**
   - 等待主程序完全退出（超时 2 分钟）
   - 解压 `app-win-x86.zip` 到临时目录
   - **备份当前应用目录**（用于失败回滚）
   - 逐文件覆盖应用目录（跳过 `updater/` 自身）
   - 验证所有文件的 SHA256
   - 更新 `update-state.json` 中的 `appVersion`
   - 写入操作日志到 `%LocalAppData%\JRETS.Go.App\logs\updater.log`

6. **故障恢复**
   - 若任何步骤失败，从备份恢复原应用目录
   - 记录详细错误日志

7. **程序重启**
   - 启动新版本的主程序
   - 用户界面无感知更新完成

### 强制更新机制

若在 `update.yaml` 中设置 `mandatory: true`：

- **启动时强制检查**：客户端必须完成所有通道的更新才能开始运行
- **UI 锁定**：开始会话按钮禁用，显示"检测到强制更新未完成，请先完成更新后再开始运行。"
- **自动应用**：无需用户操作，自动应用配置和音频，程序通道自动调用更新器

若配置 `mandatory: false`，则为静默更新。

### 程序更新失败排查

| 症状 | 原因 | 解决方案 |  
|------|------|--------|
| "Updater executable not found" | 发布产物缺少 updater 目录 | 确保 `Build-OneClickRelease.ps1` 正确打包了 updater 相关文件 |
| "Failed to wait for process exit" | 主程序未在 2 分钟内退出 | 检查是否有阻塞操作（如文件 I/O）；增加超时时间 |
| 更新后程序无法启动 | ZIP 包损坏或文件不完整 | 验证 `checksums.txt` 和 SHA256；重新下载 release-state.json |
| 更新失败自动回滚但未提示 | 备份成功但重启失败 | 检查 `updater.log` 中是否有异常；手动运行备份文件 |

### 回滚操作

如果程序更新后出现严重问题，可以：

1. **本地回滚**：更新器备份了更新前的应用目录，可从 `%LocalAppData%\JRETS.Go.App\` 查看日志并手动恢复
2. **远程回滚**：修改 GitHub Release 中 `release-state.json` 的 `appVersion` 回退到前一版本号，客户端下次启动时会自动降级

## 常见问题

### 1. "Update config load failed: xxx"

应用启动时出现该错误，说明 update.yaml 配置文件加载或验证失败。根据错误消息原因排查：

#### 原因 1：文件不存在
**错误信息**：`Update config load failed: Update config file was not found.`

**原因**：
- 应用包中缺少 `configs/update.yaml` 文件
- 文件被误删或路径不正确

**解决方案**：
1. 检查 `src/JRETS.Go.App/configs/` 目录是否存在 `update.yaml`
2. 若文件丢失，从模板恢复或重新创建
3. 重新发布应用包后上传到 GitHub Release

#### 原因 2：YAML 格式错误
**错误信息**：`Update config load failed: (Exception during YAML parsing)` 或 `Update config load failed: ...`

**原因**：
- YAML 语法错误（缩进、冒号、引号不匹配）
- 特殊字符未正确转义
- BOM（字节序标记）导致解析失败

**解决方案**：
1. 用在线 YAML 验证工具检查文件格式：https://www.yamllint.com/
2. 常见错误：
   ```yaml
   # ❌ 错误：缩进不一致
   github:
     owner: your-org
    repo: your-repo    # 缩进少一个空格
   
   # ✅ 正确：统一两个空格缩进
   github:
     owner: your-org
     repo: your-repo
   ```
3. 使用 VS Code 的 YAML 扩展验证语法
4. 确保文件编码为 UTF-8 无 BOM

#### 原因 3：必需字段缺失
**错误信息**：
- `Update config load failed: update.github.owner and update.github.repo are required.`
- `Update config load failed: update.release_state_asset_name is required.`
- `Update config load failed: update channels are missing required asset settings.`

**原因**：
配置文件缺少以下必需字段：
- `github.owner` / `github.repo`
- `release_state_asset_name`
- `app.asset_pattern` / `configs.asset_pattern`
- `audio.manifest_name` / `audio.package_pattern`
- 其他必需字段

**解决方案**：

检查 `update.yaml` 包含以下完整结构：

```yaml
github:
  owner: your-org              # GitHub 组织或用户名
  repo: your-repo              # 仓库名

release_state_asset_name: release-state.json  # 目标版本文件名

mandatory: true                # 是否强制更新
check_timeout_seconds: 30      # 检查超时（秒）
max_retry_count: 3             # 重试次数

app:
  asset_pattern: app-win-x86.zip    # 应用包模式

configs:
  asset_pattern: configs.zip         # 配置包模式

audio:
  manifest_name: audio-manifest.json # 音频清单
  package_pattern: audio-files-*.zip # 音频包模式
  require_full_on_first_install: false
```

#### 原因 4：字段值无效
**错误信息**：
- `Update config load failed: update.check_timeout_seconds must be greater than 0.`
- `Update config load failed: update.max_retry_count must be 0 or greater.`

**原因**：
- `check_timeout_seconds` ≤ 0
- `max_retry_count` < 0

**解决方案**：

在 `update.yaml` 中调整参数：

```yaml
check_timeout_seconds: 30      # 至少 1 秒，建议 30 秒
max_retry_count: 3             # 可为 0（无重试），建议 3
```

#### 原因 5：占位符未替换
**错误信息**：`Update config load failed: (应用忽略更新检查)`

**原因**：
`update.yaml` 仍使用占位值（例如 `your-org` / `your-repo`）

**解决方案**：

修改 `update.yaml` 中的 GitHub 信息为实际值：

```yaml
# ❌ 占位符（会被忽略）
github:
  owner: your-org
  repo: your-repo

# ✅ 实际值
github:
  owner: my-org
  repo: jrets-go-releases
```

#### 调试建议

1. **启用详细日志**：
   - 检查 `%LocalAppData%\JRETS.Go.App\logs\` 目录是否有日志文件
   - 日志中会显示配置加载的完整错误堆栈

2. **本地验证**：
   ```powershell
   # 在本地测试 YAML 加载
   [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
   $yaml = Get-Content "src/JRETS.Go.App/configs/update.yaml" -Raw
   Write-Host $yaml  # 检查内容是否正确
   ```

3. **重新生成配置**：
   - 删除损坏的 `update.yaml`
   - 从 GitHub Release 的 `configs.zip` 中提取正确版本
   - 或手动按上述模板重新编写

### 2. 提示 Updater executable not found

检查发布产物中是否包含 updater 目录及 JRETS.Go.Updater.exe。

### 3. 提示 Missing checksum entry

检查 checksums.txt 是否覆盖了本次所有必需资产。

### 4. 音频更新后仍缺文件

检查 audio-manifest.json 的 relativePath 是否与压缩包目录结构一致。

### 5. 强更状态无法解除

检查 update-state.json 中三通道版本是否已更新到 release-state.json 目标值。