# SurfingWin

适用于 Windows 的轻量级 Clash 运行环境，采用“免安装、即开即用”的便携式结构。  

内置 Mihomo/Clash 核心、Zashboard 面板、Windows 系统托盘，以及一组基于 VBS、Task Scheduler 和批处理的自动化脚本，支持一键启停、运行状态查看、代理模式切换和开机自启。

- 📁 项目结构
```text
SurfingWin
├── bin
│   ├── clash-amd64.exe       # Mihomo v1.19.34 (Windows amd64-v3)
│         # MD5   | ef0c3eec16800caec1a54ff002731ec5
│         # SHA256| 7916f0e5a8459f9bc59e8ee7c485b425f358ec4f0bd80e70d2d6b52ee30378bb
│   └── SurfingTray.exe       # 系统托盘与本项目内核管理
├── config
│   ├── config.yaml           # 配置文件
│   └── webroot/Zash/         # Zashboard v3.30.0（完整离线资源）
├── scripts
│   ├── Surfing.tray.vbs      # 无控制台窗口启动托盘或执行启停
│   ├── build-tray.ps1        # 从 C# 源码编译托盘程序
│   ├── Surfing.start.ps1     # 备用启动 Clash 服务脚本
│   ├── Surfing.start.bat     # 调用启动 Clash 服务脚本
│   ├── Surfing.start.vbs     # 保留的旧版独立启动脚本
│   ├── Surfing.delay.vbs     # 登录后延迟启动托盘和内核
│   ├── Surfing.startup.xml   # 注册计划任务配置文件
│   ├── Surfing.task.stop.vbs # 删除计划任务
│   ├── Surfing.task.start.vbs# 注册计划任务
│   ├── Surfing.stop.vbs      # 停止 Clash 进程
│   └── Surfing.stop.bat      # 批处理版本的停止 Clash 进程
├── 关闭自启.bat                # 关闭开机自启（删除计划任务）
├── 开机自启.bat                # 设置开机自启（注册计划任务）
├── 启动服务.bat                # 打开托盘并启动代理
├── 停止服务.bat                # 仅停止本项目目录中的内核
└── 托盘管理.bat                # 仅打开托盘，不自动启动代理
│
│
└── # 脚本依赖: PowerShell / Windows Script Host (WSH) / cmd.exe
```

内置组件于 **2026-11-16** 核对并更新至最新正式版：

- [Mihomo v1.19.34](https://github.com/MetaCubeX/mihomo/releases/tag/v1.19.34)：采用官方 `mihomo-windows-amd64-v3-v1.19.34.zip`，保留原有 amd64-v3 架构要求。
- [Zashboard v3.30.0](https://github.com/Zephyruso/zashboard/releases/tag/v3.30.0)：采用官方 `dist.zip`，包含本地字体及第三方依赖声明。两个下载包均已核对官方发布资产提供的 SHA-256。

内置配置的混合代理端口为 `1071`，三个订阅地址需在 `proxy-providers` 中填写；`external-ui` 指向 `./webroot/Zash/`，使用随包提供的 Zashboard 面板。

<details>
<summary>GPL-3.0 License</summary>

```text
Copyright (C) 2025 GitMetaio

This project is licensed under the GNU General Public License version GPL-3.0.
You are free to use, modify, and distribute this project under the terms GPL-3.0.

Dependencies:
The binary file bin/clash-amd64.exe is part of the Mihomo project and is not created or maintained by SurfingWin.
SurfingWin scripts may require this binary file to function properly. must comply separately with the license and usage terms of Mihomo.
The authors of SurfingWin assume no responsibility for the binary file or any consequences arising from its use.

Disclaimer:
SurfingWin scripts are provided "as is", without any warranty of any kind.
The authors are not liable for any direct or indirect damages, including but not limited to system issues, data loss, or network problems resulting from the use of these scripts.
```

</details>

## 使用方法
1. 通过 [发布页](https://github.com/ztyawc/SurfingWin/releases/tag/Windows) 下载**zip**，并解压至桌面
2. 如果通过从 Windows 客户端浏览器下载，建议在**解压前**右键文件 -> **属性** -> 点击 **解除锁定**（Unblock）解决每次执行所带来的烦人安全提示
3. 托盘启停内核时会按需请求管理员权限，出现 UAC 提示时允许即可。托盘自身以当前用户权限运行；TUN 模式需要管理员权限。
4. 编辑 `config/config.yaml` 填入订阅，使用方法与 [Android](https://github.com/GitMetaio/Surfing) 相同
5. 双击 **启动服务.bat**，右下角出现 SurfingWin 托盘图标；单击图标打开 Fluent 快捷控制中心，双击图标打开 Zashboard。图标也可能被 Windows 收纳在通知区域的折叠菜单中。
6. 控制台地址: 
   ```text
   http://localhost:9090/ui    
   ```
7. 默认密码: 无，可在 **config.yaml** 中的 `secret` 值设置
8. 最后建议禁用智能多宿主解析，防止DNS请求泄露

## 系统托盘

运行环境：Windows 10/11，.NET Framework 4.8 或更高的 4.x 版本。`bin/SurfingTray.exe` 使用 C#、Windows Forms 托盘入口与 WPF 控制中心，无需安装 Python、PowerShell 7、WebView2 或第三方界面框架。

- **单击图标**打开 Fluent 快捷控制中心：查看内核和 TUN 状态，使用电源按钮启停代理，一键切换规则／全局／直连模式，打开 Zashboard、配置文件或日志目录。为区分单击和双击，单击弹出会等待系统设定的双击判定时间。
- **双击图标**在内核就绪时打开 Zashboard；**右键图标**或点击控制中心右上角“更多”保留完整菜单，包括重启、退出托盘和停止代理并退出。
- 控制中心跟随 Windows 应用的深浅主题，使用圆角、柔和阴影和矢量图标；支持高对比度模式，关闭系统界面动画时不播放淡入。界面按需创建，使用原生 WPF 控件，不启动浏览器进程来渲染托盘 UI。
- 点击控制中心外部、按 **Esc** 或关闭弹窗只会收起控制中心。面板按所点显示器的工作区定位，采用 Per-Monitor V2 DPI 感知；在可用高度不足时允许滚动，确保操作入口可达。
- 绿色表示本项目内核及控制接口正常，灰色表示已停止，黄色表示正在操作或控制接口异常。状态约每 3 秒刷新，菜单同时显示文字、当前模式和 TUN 状态。这里的“运行中”表示内核接口就绪，不代表订阅或外网连通性已经检测通过。
- **退出托盘，保持代理运行**只关闭托盘；**停止代理并退出**会确认本项目内核已停止后再退出，停止失败时保留托盘。
- 多次打开同一目录的托盘只保留一个实例。启停和 API 操作按内核可执行文件路径及控制端口归属识别当前项目，不操作其他目录的同名内核。
- 控制地址和密码读取自 `config/config.yaml` 的顶层 `external-controller` 和 `secret`。支持常见的单行普通、单引号、双引号字符串；别名、多行字符串等复杂写法会提示调整。通配监听地址会转为本机回环地址连接，密码不会放进浏览器地址。
- 菜单中的模式切换仅修改当前运行状态，不改写配置文件；重启后按配置文件恢复。TUN 在首版中只显示状态，开关仍在面板内操作。
- 启动前运行内核配置校验，结果在 `config/log/tray-validation.log`；内核运行日志为 `clash_日期时间.log`，托盘错误记录在 `tray-error.log`。订阅仍需自行填写，配置校验不会检查订阅实际可用性。

修改 `src/tray` 源码后，在 Windows PowerShell 中执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-tray.ps1
```

编译脚本使用系统 .NET Framework 的 C# 编译器，输出 `bin/SurfingTray.exe` 并嵌入源码校验标识。提交源码修改时应同时提交重新编译的程序；Windows 回归测试会核对两者是否一致。

## 开机自启动

双击 **开机自启.bat**，并在 UAC 提示中允许管理员权限。重复运行会更新已有任务的脚本路径；关闭时双击 **关闭自启.bat**，脚本会检查删除结果。

如需手动配置：

1. 打开 Windows **任务计划程序**
2. 新建一个任务在**用户登录时**运行 `scripts/Surfing.delay.vbs`，使用交互式用户会话，以便显示托盘图标。
3. 按需修改任务名称、**文件路径**、触发器、条件等等。
4. **在“常规”选项卡中，勾选“使用最高权限运行”**。（如果不设置此选项，每次启动会跳出 UAC 窗口）

## 构建发布包

在具有 Git 和 POSIX shell 的环境中运行 `sh build.sh`。脚本仅从已提交的 `HEAD` 导出发布文件，生成 `Surfing_windows-amd64_<提交短哈希>.zip`，并包含 `LICENSE`。

本地未提交的代码、订阅配置修改、运行日志、缓存和旧压缩包不会进入发布包。需要发布的代码改动应先提交。

回归测试：`python3 -B -m unittest discover -s tests -v`（Windows 可用 `python`）。测试会根据环境运行打包、Windows 脚本和原生托盘检查。脚本测试不创建或删除系统计划任务；托盘测试使用回环模拟内核，并用关闭 TUN 的真实 Mihomo 检查启停和模式切换，不修改系统代理或路由。CI 会在 Windows 回归测试及 Ubuntu 打包测试通过后发布。
