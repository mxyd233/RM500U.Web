# RM500U.Web

基于 .NET 10 的 Quectel RM500U 管理程序，内置 Web 管理页面。仅支持 RM500U 系列（RM500U-CN / RM500U-CNV / RM500U-EA），可在 Linux（Debian / Ubuntu / Armbian）和 Windows 上运行。

## 功能

- 自动发现并识别 RM500U 的 AT 串口
- 设备状态、信号质量、服务小区、QoS（5QI / 签约速率）、温度和流量
- APN 档案管理、拨号与断开、USB 网络模式（ECM / MBIM / RNDIS / NCM）
- 拨号模式切换：网卡模式、路由 / NAT、网桥模式
- 网络制式偏好、频段锁定、邻区查看、小区锁定、双 SIM 切换
- 短信：PDU 编解码、长短信拼接、按号码会话、直接回复、webhook 转发
- 原始 AT 终端、设备重启、运行日志
- 可选 Basic Auth

## 运行

### Linux

```bash
# 生成 Linux ARM64 Native AOT 输出（在 Linux ARM64 或 WSL Ubuntu 中执行）
dotnet publish RM500U.Web.csproj -c Release -p:PublishProfile=native-aot-linux-arm64

# 将 deploy/install.sh 和 publish/native-aot/linux-arm64/ 复制到目标设备后安装
# AOT 输出目录可以是任意位置，只需通过 --bin-dir 指定。
sudo ./install.sh --bin-dir ./linux-arm64
```

也兼容常规 self-contained 发布：

```bash
# 构建
dotnet publish RM500U.Web.csproj -c Release -r linux-arm64 --self-contained true -o publish/linux-arm64

# 安装为 systemd 服务（监听 http://0.0.0.0:5080）
sudo ./deploy/install.sh --bin-dir publish/linux-arm64
```

`install.sh` 自带 systemd unit 定义，因此独立拷贝时不需要额外复制 `rm500u-web.service`。首次安装会生成 Basic Auth 凭据并只显示一次。x64 设备使用 `linux-x64`。卸载执行 `sudo ./deploy/uninstall.sh`（加 `--purge` 会连同配置和凭据一起删除）。

### Windows

```bash
dotnet publish RM500U.Web.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```

运行 `publish/win-x64/RM500U.Web.exe`，浏览器访问 `http://127.0.0.1:5080`。

程序自动识别 AT 串口，无需手动配置端口。已实测 ECM、MBIM 模式可在 Windows 使用；Linux 下 ECM 模式实测可用。

### 无硬件时体验

设置环境变量 `RM500U_SIMULATE=true` 后运行，会使用模拟设备，方便在未接入模组时预览界面和接口。

## 配置

配置保存在 `RM500U_DATA_DIR`（Linux 默认 `/var/lib/rm500u-web`，Windows 默认程序目录下 `data/`）。常用环境变量：

| 变量 | 说明 |
| --- | --- |
| `RM500U_WEB_USER` / `RM500U_WEB_PASSWORD` | 设置后启用 Basic Auth |
| `ASPNETCORE_URLS` | 监听地址，默认 `http://0.0.0.0:5080` |
| `RM500U_DATA_DIR` | 数据目录 |
| `RM500U_SIMULATE` | `true` 时使用模拟设备 |

Linux systemd 服务从 `/etc/rm500u-web.env` 读取这些变量，修改后 `sudo systemctl restart rm500u-web`。

## 注意事项

- 不要让 ModemManager 等其他程序同时占用 AT 串口，否则 AT 响应会串线。
- 页面中的拨号模式、频段、USB 模式等设置由模组固件保存，部分在模块重启后生效。
- Basic Auth 在纯 HTTP 下不加密，公网使用请置于 HTTPS 反向代理之后。
