# EasyTier 群友直连客户端

给群友的**傻瓜式 Windows 桌面客户端**：双击即连开服服务器，无需懂组网、无需敲命令。底层基于 [EasyTier](https://github.com/EasyTier/EasyTier) 私有组网，服务器侧配合打洞+兜底中继。

## 功能特性

**客户端（C#/WPF，单文件 76MB exe）**
- 自包含 .NET 8 运行时 + 内嵌 easytier 2.6.4（core/cli/wintun/WinDivert），首次运行自解压，群友零依赖零配置
- 免配置：无需邀请码，打开点「连接」即可；机器标识自动生成，重连自动拿回同一虚拟 IP
- 系统托盘常驻（品牌图标 + 状态徽标：绿=已连接 / 橙=连接中 / 灰=未连接）
- 显示虚拟 IP、↑↓流量、在线时长；一键断开/重连；断线自动重连（指数退避）
- 点 × 可选「隐藏进托盘 / 退出程序」，支持记住选择
- 客户端侧 Windows 防火墙隔离：群友之间互访默认阻断，仅可访问开服服务器
- TLS 证书指纹固定，防中间人截获口令；日志全程脱敏

**IP 分配服务（Go，零第三方依赖，docker 单容器）**
- 池 10.144.0.100~200 原子分配（随机挑选空闲位），心跳保活（30s），TTL 90s 自动回收僵尸租约
- 以 machine_id 为客户端唯一身份；邀请码机制已停用（codes.json 可选，仅作备注展示）
- 接口：`/api/alloc` `/api/heartbeat` `/api/release` `/api/status`（管理密钥）`/api/health`

**服务器侧（璐璐娅 / Arch Linux）**
- nftables 白名单：仅 easytier TUN 网卡 + 群友网段生效，放行 ICMP/已建立连接，默认丢弃新建入站；带开服端口模板，物理网卡与 SSH 零影响（防自锁设计）

## 快速开始（群友）

1. 到 [Releases](../../releases) 下载 `EasyTierGroupClient.exe`
2. 双击运行，UAC 弹窗点「是」
3. 点「连接」，首次若弹出网络驱动安装窗口点「允许」
4. 托盘出现绿色徽标即连接成功

详细使用说明见 [README_给群友.md](README_给群友.md)，运维与部署见 [docs/部署文档.md](docs/部署文档.md)、[docs/群友分发说明.md](docs/群友分发说明.md)。

## 构建

前置：.NET 8 SDK；将 `easytier-windows-x86_64-v2.6.4.zip` 解压到 `tools/easytier/easytier-windows-x86_64/`（该目录不入库）。

```bash
dotnet publish client/EasyTierGroupClient/EasyTierGroupClient.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/
```

图标资产由 `tools/make-icons.ps1` 从源图生成（品牌图 + 状态徽标合成）。

## 目录结构

```
client/EasyTierGroupClient   # Windows 客户端（C#/WPF）
ip-alloc-service             # 腾讯云 IP 分配服务（Go + Dockerfile + compose）
server-luluya                # 璐璐娅 nftables 白名单规则与 systemd 服务
docs/                        # 部署文档、群友分发说明
tools/                       # 构建工具脚本（不入库）
```

## 安全模型

- 组网口令仅存服务端 `.env`（600 权限），客户端连接时经 HTTPS 换取、仅存内存
- 客户端以自动生成的 machine_id 标识身份（持久化本机），服务端据此管理租约；**邀请码机制已于 v1.0.3 停用**（知道服务地址即可连接，属可接受的群内场景取舍）
- 客户端与分配服务通信采用自签 TLS + 证书指纹固定
- easytier 参数经 TOML 配置文件下发，口令不进命令行
- 注：EasyTier 开源版无对端级 ACL，群友互访隔离由客户端防火墙兜底
