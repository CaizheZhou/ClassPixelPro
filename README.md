<div align="center">

# <image src="ClassPixelPro/Assets/AppLogo_AppLogo.svg" height="28" width="28"/> ClassPixelPro

![Banner](ClassPixelPro/Assets/BannerMain_App2.png)

ClassPixelPro 是一款适用于班级多媒体屏幕的跨平台课表信息显示工具，可以在 Windows PC、Mac 及 Linux 设备屏幕上显示各种信息。

</div>

> [!IMPORTANT]
> **本项目基于 [ClassIsland](https://github.com/ClassIsland/ClassIsland) 二次开发**，遵循 **GPL-3.0** 开源协议发布（详见 [LICENSE.txt](LICENSE.txt)）。感谢原项目作者 [HelloWRC](https://github.com/HelloWRC) 与社区贡献者。
> 本仓库（[CaizheZhou/ClassPixelPro](https://github.com/CaizheZhou/ClassPixelPro)）为二开仓库，全部源码公开；衍生作品同样以 GPL-3.0 开源。


## 构建

1. 安装 [.NET 8.0 SDK](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)、PowerShell Core 与 Git。
2. 克隆仓库并初始化子模块：

   ```bash
   git clone <仓库地址>
   cd ClassPixelPro
   git submodule update --init --recursive
   ```

3. 首次编译前在 PowerShell Core 中执行一次：

   ```powershell
   ./tools/plugin/build.ps1
   ```

4. 之后使用 Visual Studio / Rider 打开 `ClassPixelPro.sln`，将 `ClassPixelPro.Desktop` 设为启动项目即可编译调试。

## 许可

本项目基于 [GNU General Public License v3.0](LICENSE.txt) 获得许可。
