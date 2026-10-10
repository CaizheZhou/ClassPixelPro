# 发布流程

本文档记录 ClassPixelPro 的完整发版流程。以后每次发版都照此执行，避免踩坑。

## 一、仓库结构（双仓库 + 官网）

| 仓库/目录 | 作用 | 远端 |
|---|---|---|
| `ClassPixelPro/`（本仓库，私有） | 客户端源码 | `CaizheZhou/ClassPixelPro`（私有） |
| `ClassPixelPro-Releases/`（公开） | 构建 workflow + 更新日志 + Banner 图 | `CaizheZhou/ClassPixelPro-Releases`（公开） |
| `website/`（私有目录，无 git remote） | 官网前端 + 云函数 | 无 remote，通过 EdgeOne CLI 部署 |

**关键认知**：
- 代码存在私有仓库；**构建完全跑在公开仓库**。
- 更新日志（release notes）的权威来源是**公开仓库的 `CHANGELOG.md`**，不是私有仓库的那份。
- 私有仓库打 tag 会自动触发公开仓库构建（通过私有仓库的 `.github/workflows/trigger-build.yml`）。

## 二、版本号规范

格式 `主版本.X.Y.Z`（见 `VERSION_CONVENTION.md`）：

- 主版本恒为 **2**
- X：重大更新
- Y：功能新增
- Z：Bug 修复 / 已有功能改动

## 三、发版步骤

### 1. 写更新日志

在**公开仓库** `ClassPixelPro-Releases/CHANGELOG.md` **顶部**添加段落，格式：

```markdown
## [2.2.5.0]

### ✨ 新功能

- **功能名**：描述

### 🐛 Bug 修复

- **修复 xxx**：描述

### 🔧 内部改进

- 描述
```

注意：
- 版本段落是 `## [版本号]`，**不带日期**。
- 分类带 emoji：`✨ 新功能`、`🐛 Bug 修复`、`🔧 内部改进`（还可视情况用 `⚡ 优化` 等）。
- release notes 会按 `## [版本号]` 正则提取该段落；缺失则退化成 `Release <版本>`。

### 2. 提交三处改动

按仓库分别提交：

```powershell
# 私有仓库（客户端代码）
cd ClassPixelPro
git add <改动的文件>
git commit -F <msg-file>          # 建议用消息文件，避免 PowerShell 中文编码问题

# 公开仓库（workflow / 更新日志 / Banner）
cd ../ClassPixelPro-Releases
git add .
git commit -F <msg-file>

# 官网（如有改动）
cd ../website
git add <改动的文件>
git commit -F <msg-file>
```

提交信息遵循 Conventional Commits，scope 见 `doc/Contributing/Scopes.md`（如 `feat(update):`、`ci(release):`）。

### 3. 推送并打 tag

```powershell
# 公开仓库：先推（确保构建用的 workflow 是最新的）
cd ClassPixelPro-Releases
git push origin main

# 私有仓库：打 tag 并推送（会自动触发公开仓库构建）
cd ../ClassPixelPro
git tag -a 2.2.5.0 -m "2.2.5.0"
git push https://github.com/CaizheZhou/ClassPixelPro.git master
git push https://github.com/CaizheZhou/ClassPixelPro.git 2.2.5.0
```

注意：
- **必须用直连 URL 推送**（`https://github.com/CaizheZhou/ClassPixelPro.git`）。仓库里配置的 `ghproxy` remote 走代理，认证会失败（`Repository not found`）。
- tag push 触发私有仓库 `trigger-build.yml` → 触发公开仓库 `build-and-release.yml` → 打包、发 GitHub Release、调官网 API 登记分发记录。

### 4. 监控构建

构建在公开仓库跑，可通过 API 查询状态：

```powershell
Invoke-RestMethod -Uri "https://api.github.com/repos/CaizheZhou/ClassPixelPro-Releases/actions/runs?per_page=3" -Headers @{ "User-Agent"="cursor" }
```

等 `status=completed` 且 `conclusion=success`。

### 5. 部署官网（仅当 website 有改动）

```powershell
cd website
npm run build
edgeone makers deploy
```

注意：
- **`edgeone makers deploy` 不带参数**（在 `website` 目录内执行）。它会把 `dist`（静态）+ `cloud-functions/`（云函数）一起构建并部署。
- **不要传 `dist` 参数**（`edgeone makers deploy dist` 只部署静态资源，会删掉云函数，导致 `/api/*` 全部 404）。
- 需要先 `edgeone login`（或设置 `EDGEONE_PAGES_API_TOKEN`）；`edgeone` 是项目本地依赖，在 `website` 目录内用 `npx edgeone ...`。

### 6. 验证

```powershell
# GitHub Release
curl.exe -s https://api.github.com/repos/CaizheZhou/ClassPixelPro-Releases/releases/tags/2.2.5.0

# 官网下载（应 302 到 gh-proxy 加速链接）
curl.exe -s -o NUL -w "%{http_code} %{redirect_url}" https://class.imzcz.cn/api/public/download

# 官网后端健康
curl.exe -s https://class.imzcz.cn/api/health
```

## 四、其他约定

- **Banner 图**：放在公开仓库 `ClassPixelPro-Releases/BannerMain_App2.png`。release notes 会在更新日志上方插入它（图片 URL 走 `gh-proxy.com` 加速，国内可达）。源文件在 `ClassPixelPro/ClassPixelPro/Assets/BannerMain_App2.png`，改图后需同步复制到公开仓库。
- **下载链接加速**：分发记录的 `archiveUrl` 自动拼成 `https://gh-proxy.com/https://github.com/.../releases/download/<版本>/<zip>`，在 `build-and-release.yml` 里处理，无需手动。
- **文件数**：链接发布模式下服务端不解包 zip，文件数由 CI 统计后随分发提交（`build-and-release.yml` 里用 `ZipFile` 数非目录条目）。

## 五、踩过的坑（务必记住）

1. 更新日志写错位置 → 写在公开仓库 `CHANGELOG.md`，不是私有仓库。
2. 推送用代理 remote 失败 → 用直连 URL push。
3. `edgeone makers deploy dist` 会删云函数 → 用不带参数的 `edgeone makers deploy`。
4. PowerShell 直接 `git commit -m "中文"` 可能乱码 → 用 `git commit -F <utf8 消息文件>`。
