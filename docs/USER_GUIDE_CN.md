# 用户文档

## 重要说明

由于本插件底层基于内存扫描来获得游戏实时数据，因为每次游戏更新会导致数据的内存基址产生偏移从而使插件生效，必须等待更新。

或者你也可以查阅 [开发者文档](./DEV_GUIDE_CN.md) 中的内存偏移配置文件（memory-offsets.yaml）章节来尝试手动搜索内存基址。

## 自动更新说明

客户端使用 GitHub Release 自动更新，分为三部分：

1. 程序更新
2. 配置更新
3. 音频资源更新

当前策略为强制更新。若检测到版本落后，开始运行按钮会被禁用，需先完成更新。

## 首次安装与后续更新

1. 首次安装后，音频会按发布内容完成全量可用。
2. 后续版本默认走音频增量更新，只下载新增或变更资源，减少重复下载。

## 更新过程中会发生什么

1. 启动时静默检查版本
2. 下载并校验文件完整性（SHA256）
3. 应用配置和音频更新
4. 若程序需要更新，自动启动更新器并重启客户端

## 常见提示

1. Update pending
	说明仍有通道尚未更新完成，请等待或重试。

2. Update check failed
	可能是网络异常、Release 资产缺失或校验失败。

3. Updater launched. App will exit for replacement.
	属于正常提示，主程序会退出并由更新器完成替换后自动重启。

## 故障排查

如更新异常，可提供以下文件给开发者：

1. %LocalAppData%\JRETS.Go.App\logs\updater.log
2. %LocalAppData%\JRETS.Go.App\update-state.json

如果更新中断，直接重新启动客户端会再次触发更新流程。

## 操作演示

可以参考以下视频

https://github.com/user-attachments/assets/1656bf4a-2c8f-441d-be78-293378d6c677






