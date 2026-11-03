# RouteMeter 中文说明

RouteMeter 0.2 提供 **Windows 和 macOS** 原生客户端，用于查看按 App 归属的 **Proxy** 用量。主界面明确显示 Direct、Proxy、Download、Upload 和 Total。

## 核心原则

**实际流量必须经过 RouteMeter Core。** 它是加入完整生命周期计量的独立 Mihomo 修改版。统计发生在实际路由适配器的读写处，连接关闭后累计仍保留。界面刷新频率不决定累计精度。

- 同时覆盖 TCP、UDP 和两次刷新之间已经关闭的短连接。
- 根据实际叶适配器类型区分 Direct/Proxy，不按节点名称、策略组名或 TUN 接口猜测。
- 在连接创建时查询系统来源。无法识别或存在歧义时，保留在 **Unattributed**。
- macOS 将同一外层 .app 内的辅助进程合并。Windows 按完整、大小写归一后的可执行文件路径区分应用。
- Unknown routing、写入计数不确定、计数溢出和持久化失败均显式提示。

计量范围是 **该核心实际处理的应用载荷**。它不是服务商扣费、加密后的线速字节或所有绕过该核心的流量统计。仅在 QX 旁边放一个监控器，无法恢复 QX 没有暴露的来源信息。

## 安装

从 [Releases](https://github.com/MatchalatteR/RouteMeter/releases) 下载对应版本。Windows x64 包包含客户端和 .NET 运行时；macOS 需要 14+，分 Apple Silicon 和 Intel 构建。Core 一同提供。

1. 使用自己的私有 Mihomo 格式配置启动 RouteMeter Core。
2. 将要统计的应用接到该核心的实际 HTTP/SOCKS/TUN 入口。替换日常代理前，先验证分流及回退。
3. 打开客户端，点击 **Connect Core…**。
4. 选择 `<core-home>/route-meter/connection.json`。
5. 在 **Proxy usage by App** 中查看累计、实时流速及应用筛选。

完整命令及边界见 [Core 配置说明](CORE-SETUP.md)。客户端不会自动修改系统代理、DNS 或路由。TUN/全局接管属于单独部署步骤，可能需要管理员权限。

macOS 保留 **QX legacy totals** 模式。该模式只有 Direct/Proxy 汇总，不提供按 App 的 Proxy 归属。新 Core 模式不需要读取 QX 连接日志。

## 数值与保存

- ↓ = Download，↑ = Upload；流速单位为每秒字节数，K/M/G 按 1024 进位。
- Total 行包含 Direct + Proxy + Unknown routing；有 Unknown 时会单独列出。
- 每行累计 Total = Download + Upload。
- 按 App 列表只汇总 Proxy 桶，不把无法分类的路由算进 Proxy。
- Reset 只重置当前查看器的基准，不清除核心及其他查看器的累计。
- 正常情况下每秒原子保存一次，正常退出时再保存。强制退出或掉电可能丢失上次成功保存之后的数据，存储故障会延长该窗口。

核心状态只保存可执行文件/App 身份和计数，不在该状态中保存目标 IP、域名、URL、请求头或正文。私有 API 仅监听本机回环，凭据独立生成。不要在公开 issue 中分享连接文件、私有路由配置或未遮盖的应用路径。

## 限制与退出

这仍是需要分平台验收的开源版本。跨架构构建不等于实机验收；HTTP/SOCKS 本机测试也不等于全部第三方 App、TUN 或长期稳定性验证。macOS 尚未 Developer ID 公证，Windows 尚未 Authenticode 签名。

退出查看器不会停止代理核心。确实要停用该代理时，再终止核心进程。保存数据目录可保留累计。旧 QX 版本卸载前，应先禁用对应任务及登录启动项。

原生查看器使用 MIT 许可；修改后的核心与其计量代码使用 GPL-3.0。发布包旁提供对应的完整修改后源代码及构建说明。见[组件许可说明](../THIRD-PARTY-NOTICES.md)。
