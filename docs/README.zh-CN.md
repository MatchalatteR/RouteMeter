# RouteMeter 中文说明

RouteMeter 是 Quantumult X 的原生 macOS 菜单栏配套组件。它分别显示 **Direct** 和 **Proxy**，并明确标注 **Download、Upload、Total**。

它读取 QX 自己分类后的计数，不按进程名猜测代理流量，也不使用“整机流量减去某张网卡”的算法。当前是首个公开版本 **0.1.0**。项目已提供可复现构建和测试，但不将它等同于套餐计费工具或所有系统上的长期验证。

## 安装与配置

需要 macOS 14+ 和已安装的 Quantumult X。请从 [Releases](https://github.com/MatchalatteR/RouteMeter/releases) 选择 Apple Silicon（arm64）或 Intel（x86_64）版本，解压后将应用放入“应用程序”或用户的 Applications 目录。

首批构建仅使用 ad-hoc 签名，尚无 Apple Developer ID 签名及公证。下载后可能触发 Gatekeeper 限制。也可以审核源码后，在本机运行 `./scripts/install.sh` 构建安装。无需关闭系统安全保护，也不需要管理员权限。

1. 打开 RouteMeter，点击 **Copy QX task**。
2. 在 QX 的计划任务面板中，点击“添加 → 文本方式添加”。
3. 粘贴复制的整行配置，保存。
4. 对该任务执行一次“更新”，下载脚本。
5. 保持该任务、QX 计划任务总开关和 QX 隧道开启。

几秒后，组件应显示 **QX connected**。不需要断开 QX，也不需要修改分流规则或启用 HTTP Backend。每台电脑的脚本路径独立生成，请不要公开分享你复制的任务行。

## 数值说明

- **Direct**：QX 中标为 direct 的 TCP/UDP 流量。
- **Proxy**：各代理节点的 TCP/UDP 流量之和，不含 reject/discard。
- **Total 行**：Direct + Proxy，分别计算 Download 和 Upload。
- **累计区域每行的 Total 列**：该行 Download + Upload。
- 菜单栏的 ↓ 为 Download，↑ 为 Upload，所有数值都是每秒字节数；K/M/G 使用 1024 进位。

首次有效样本建立基准。之后按累计计数器的差值累加。累计值和基准会保存在本机，并在应用重启后继续使用。若 QX 同一会话中的计数器持续增长，组件关闭期间的流量也可能在下次采样时补入。

点击 **Reset** 后，下一个样本建立新基准。QX 计数器下降时，整份样本重新建立基准，避免跳出负值或异常大值。未观测到的 QX 会话重置之间的流量无法恢复。超过 9 秒没有有效数据时，实时流速显示为 `—`。

统计范围仅为 **QX 处理并计数的流量**。绕过 QX 的流量、部分局域网流量、协议开销和服务商实际扣费可能不同。它不是整机所有网卡或每个应用的流量监控器。

## 本机数据与停止方法

配置、脚本和累计数据保存在 `~/Library/Application Support/RouteMeter/`，目录权限 0700，文件权限 0600。只监听 `127.0.0.1:18764`。不上传订阅、浏览记录、请求正文或流量统计。状态文件可能包含代理节点名称。

要完整停用：先在 QX 中禁用 RouteMeter 任务，再关闭组件的 **Launch at login** 并点击 **Quit**。之后可将应用移入废纸篓。本机数据可以保留，也可以单独移入废纸篓。退出本组件不会断开 QX。

详细的故障处理、构建和卸载信息见[英文主页](../README.md)。
