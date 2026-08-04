# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概览

RM500U.Web 是基于 .NET 10 Minimal API 的 Quectel RM500U 5G 模组 Web 管理控制台。单一 Web 项目（`RM500U.Web.csproj`，仅依赖 `System.IO.Ports`），通过 AT 命令与模组交互，静态前端挂在 `wwwroot/`。仅支持 RM500U-CN / RM500U-CNV / RM500U-EA 三个变体，可在 Linux 与 Windows 运行。

## 构建与运行

用户环境是 Windows，**一律用 PowerShell 工具执行 dotnet，不要用 WSL**。

```powershell
dotnet build RM500U.Web.csproj
dotnet publish RM500U.Web.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
dotnet publish RM500U.Web.csproj -c Release -r linux-arm64 --self-contained true -o publish/linux-arm64
```

网络不通导致 restore 失败时使用：`dotnet restore --ignore-failed-sources --disable-parallel -p:NuGetAudit=false`

无硬件预览：设 `RM500U_SIMULATE=true` 后运行，会走 `MockAtTransport`。默认监听 `http://0.0.0.0:5080`。Linux 部署脚本见 `deploy/install.sh`（systemd + `/etc/rm500u-web.env` 环境变量文件，首次安装生成 Basic Auth 凭据）。

## 架构分层

调用链自底向上：

- **IAtTransport**（`Services/AtTransport.cs`）：串口读写抽象。三个实现：
  - `LinuxAtTransport`：`CommandRunner`（stty 配置） + 直接 `FileStream` 读写 + 行缓冲正则判定 `OK`/`ERROR` 终止；
  - `WindowsAtTransport`：`System.IO.Ports.SerialPort`；
  - `MockAtTransport`：模拟设备，供 `RM500U_SIMULATE=true` 时使用。
  - 所有实现都会**脱敏 `AT+QICSGP` 中的口令**，且按端口加串行信号量防并发串线。
- **设备发现**：`WindowsDeviceScanner`（SetupAPI PnP 识别 "Quectel USB AT Port"）、`LinuxDeviceScanner`（/dev/tty* + /sys 设备树）。
- **AtGateway**（`Services/AtGateway.cs`）：端口解析 + AT 分发。`AtPort=auto` 时扫描候选口，用 `ATI`/`AT+CGMI`/`AT+CGMM`/`AT+CGSN` 探测并校验是 Quectel RM500U，结果按分数选优并缓存 10 秒。`ExecuteAsync` 对命令做合法性校验（必须以 AT 开头、单行、≤512 字符）。
- **Rm500uService**（`Services/Rm500uService.cs`）：业务核心。`GetStatusAsync` 并发发一串 AT 查询拼 `ModemStatus`，结果缓存 4 秒。频段档案 `BandProfiles` 按 Variant 区分（CN/CNV/EA）。QoS 解析用无参 `AT+C5GQOSRDP`（因实际数据 CID 常是 2，而 APN 存在 CID 1），无法解析时回退按 CID 定向查询。
- **DialService**：拨号/断开（`AT+QNETDEVCTL`、`AT+CGDCONT`、`AT+QICSGP`），MBIM 模式在 Linux 走 `mmcli`、在 Windows 直接报不支持；ECM/NCM/RNDIS 走系统 DHCP。`AutoConnectService` 是后台服务，启动 8 秒后按配置自动拨号。
- **短信**：`SmsPduCodec`（GSM 7-bit / UCS2、UDH 长短信拼接）、`Rm500uService` 内按号码聚合会话、`SmsWebhookService` 转发新短信（托管后台服务）。
- **AtParser**：纯静态解析助手（CSV 拆行、+XXX: 取值、UCS2 解码等），被上层广泛复用。

`Program.cs` 用 Minimal API 声明了全部 `/api/*` 端点并注册所有单例服务；中间件链包含：全局异常归一化（499/400/500 + `ApiError`）、可选 Basic Auth（`RM500U_WEB_USER`/`RM500U_WEB_PASSWORD`，恒定时比较）、跨域写请求拒绝（Origin 必须等于本机）。配置经 `ConfigStore` 持久化到 `RM500U_DATA_DIR/config.json`（`ModemConfigValidator.Validate` 统一归一化）。

## 前端

`wwwroot/` 是单页应用（无框架，原生 JS + hash 导航），页面为 dashboard / connection / radio / sms / tools。`app.js` 用 `state` 对象 + `setInterval` 轮询 `/api/status` 刷新，`app.css` 负责样式。页面加载时并行拉 config / candidates / status。版本号手写维护在 `index.html`（如 `20260803-2`），改动页面记得递增。

## 关键约束

- **配置归一化**：所有写配置的入口都经过 `ModemConfigValidator.Validate`，APN 的 PDP context 固定为 1（RM500U 限制），variant 仅限三个型号。
- **AT 安全**：命令必须 `AT` 开头、单行；日志脱敏 `AT+QICSGP` 口令；`Rm500uService` 中用 `+CGMM` 返回是否含 `RM500U` 作为权威型号校验。
- **不要提交**：`data/`、`publish/`、`.claude/`、日志、临时文件（见 `.gitignore`）；运行时数据由 csproj 显式排除，不进发布目录。