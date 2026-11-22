# 一言 Tile

Windows 10 / 11 桌面磁贴：每小时给你一句「一言」。

<p align="center">
  <a href="https://bzhcyy.github.io/OneSentenceTile/"><b>磁贴服务器</b></a> ·
  <a href="https://github.com/bzhcyy/OneSentenceTile/releases">Releases</a> ·
  <a href="https://developer.hitokoto.cn/">一言 API</a>
</p>

---

## 磁贴长什么样

```
每日一言
　　没有谈判，硝烟中，炮火轰鸣中，世界褪去了色彩。
　　　　　　　　　　　　—— 郑芳浩《止战之殇》
```

标题 24 居左、正文 15 空两格、出处 12 靠右；磁贴透明，壁纸由系统透出。

## 功能

| | |
|---|---|
| **分类** | 随机 / 动画 / 漫画 / 游戏 / 文学 / 原创 / 网络 / 其他 / 影视 / 诗词 / 网易云 / 哲学 / 抖机灵 |
| **刷新** | 实时（现拉现推，默认）/ 30 分钟 / 1 / 6 / 12 / 24 小时；任何模式下改完都会立刻推一次 |
| **双源** | Worker 优先，连不上自动退 Pages |
| **磁贴文字** | 标题 / 正文 / 出处 各自字号、对齐（左/中/右）、显示开关；XML 由 App 本地重建，两源都生效 |
| **界面文字** | 字体（含系统字体选择框 + 未知字体自动回退）、三段字号、加粗 |
| **壁纸** | 关闭 / 本地图片 / 网络地址 / 文件夹轮播（1–60 分钟） |
| **外观** | 背景色 + 亚克力透明度（全屏同样生效）、字体色、全屏、屏幕常亮 |

## 默认设置

| 项 | 默认值 |
|---|---|
| 动态磁贴 | 开 |
| 磁贴源 | Worker 优先 |
| 刷新间隔 | 实时 |
| 标题 / 正文 / 出处 | 24 / 15 / 12 |
| 对齐 | 标题靠左、出处靠右 |
| 壁纸 | 关闭 |
| 界面字号 | 标题 60 加粗、正文 40 加粗、出处 20 常规 |

设置存在 `RoamingSettings`，升级后带一次性迁移（`settingsRev`），改默认值也能生效。

## 安装

下载 Release 里的 `uwp-package.zip`，解压后**双击 `安装.cmd`**：

1. 自动请求管理员权限
2. 证书 → 受信任人
3. 依赖框架 → `Dependencies\x64\*.appx`
4. 主程序包 → `Add-AppxPackage`

手动等价操作：

```powershell
Import-Certificate -FilePath .\OneSentenceTile_TemporaryKey.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path .\Dependencies\x64\Microsoft.NET.CoreRuntime.1.1.appx
Add-AppxPackage -Path .\Dependencies\x64\Microsoft.VCLibs.x64.14.00.appx
Add-AppxPackage -Path .\OneSentenceTile_*_x64.appxbundle -ForceUpdateFromAnyVersion
```

免证书（需开「开发人员模式」）：

```powershell
powershell -ExecutionPolicy Bypass -File .\install-app.ps1 -Register
```

### 报错对照

| 报错 | 原因 | 处理 |
|---|---|---|
| `0x80073CF3` 找不到框架 | 依赖没装 | 先装 `Dependencies\<arch>\*.appx` |
| 证书缺少基本约束扩展 | 证书装进了「受信任的根」 | 改装「受信任人」 |
| `APPX0107` 签名无效 | 用了 CA 证书签名 | 用 `-Type CodeSigningCert` 的证书 |

## 构建

推送即触发 Actions → `Build UWP package` → 下载 `uwp-package`；Job Summary 会列出本次版本、产物、安装步骤。

打 Release：推 `v*` tag。

```bash
git tag v1.3.35 && git push origin v1.3.35
```

本地构建（需 VS 2022 + UWP 工作负载）：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-uwp.ps1 -Configuration Release -NoDotNetNative
```

签名证书缓存在 Actions `build/cert`（key `uwp-dev-cert-v9`），缓存不清证书就不变，升级不用重装证书。想长期固定：把 `signing-cert` 工件里的 `.pfx`/`.cer` 存本地，或放进仓库 Secrets `SIGNING_PFX_BASE64`。

## 磁贴服务器

| | 地址 | 更新方式 |
|---|---|---|
| 主 | `https://onesentencetile.onesentencetile.workers.dev/tile.xml` | 每次请求现取，缓存 60 秒；`?type=d` 指定分类 |
| 备 | `https://bzhcyy.github.io/OneSentenceTile/tile.xml` | Actions 每 15 分钟生成；分类为 `tile-a…l.xml`；首页 JS 实时取一言 |

两端生成的 XML 结构一致：`TileSmall / TileMedium / TileWide / TileLarge` 四个绑定，标题/正文/出处分别用 `hint-style` + `hint-align` 控制。

## 目录

```
OneSentenceTile/        UWP 应用
  MainPage.xaml(.cs)    主界面 + 设置面板
  TileSource.cs         双源探测、样式/对齐参数
  Package.appxmanifest  清单（Identity KienSticn.OneSentenceTile）
tile/                   磁贴静态站生成器
  gen_tile.py           生成 tile.xml / 13 个分类 / index.html / health.json
  config.json           标题、样式、对齐默认值
cloudflare-worker/      Cloudflare Worker 版磁贴（实时）
.github/workflows/      构建、发布、磁贴生成
build-uwp.ps1           打包脚本（含 CI Summary）
install-app.ps1         安装脚本
安装.cmd                 一键安装（自动提权）
```

## 许可

GPL-3.0，继承自 [chenxuuu/add-one-second-UWP](https://github.com/chenxuuu/add-one-second-UWP)。
