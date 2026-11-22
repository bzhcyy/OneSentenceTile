# 一言 Tile

Windows 10 / 11 桌面磁贴，每小时给你一句「一言」。

<p align="center">
  <a href="https://bzhcyy.github.io/OneSentenceTile/"><b>磁贴服务器</b></a> ·
  <a href="https://github.com/bzhcyy/OneSentenceTile/releases">Releases</a> ·
  <a href="https://developer.hitokoto.cn/">一言 API</a>
</p>

---

## 磁贴效果

```
每日一言
　　没有谈判，硝烟中，炮火轰鸣中，世界褪去了色彩。
　　　　　　　　　　　　—— 郑芳浩《止战之殇》
```

默认排版：标题 24 居左、正文 15 空两格、出处 12 靠右。全部可在设置里改。

## 功能

| | |
|---|---|
| **分类** | 随机 / 动画 / 漫画 / 游戏 / 文学 / 原创 / 网络 / 其他 / 影视 / 诗词 / 网易云 / 哲学 / 抖机灵 |
| **刷新间隔** | 实时（现拉现推）/ 30 分钟 / 1 / 6 / 12 / 24 小时 |
| **双源** | Worker 优先，失败自动退 Pages |
| **磁贴文字** | 标题 / 正文 / 出处 各自字号、对齐（左/中/右）、显示开关；XML 由 App 本地重建，Worker 与 Pages 源都生效 |
| **界面文字** | 字体（含系统字体选择框）、三段字号、加粗 |
| **壁纸** | 关闭 / 本地图片 / 网络地址 / 文件夹轮播（1–60 分钟） |
| **外观** | 背景色 + 亚克力透明度（全屏自动改用 alpha 纯色）、字体色、全屏、屏幕常亮 |

## 安装

下载 `uwp-package.zip` 解压后：

1. **双击 `安装.cmd`**（会自动请求管理员权限）
2. 它依次完成：证书 → 受信任人、依赖框架、主程序包

手动方式：

```powershell
# 管理员 PowerShell
Import-Certificate -FilePath .\OneSentenceTile_TemporaryKey.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path .\Dependencies\x64\Microsoft.NET.CoreRuntime.1.1.appx
Add-AppxPackage -Path .\Dependencies\x64\Microsoft.VCLibs.x64.14.00.appx
Add-AppxPackage -Path .\OneSentenceTile_*_x64.appxbundle -ForceUpdateFromAnyVersion
```

免证书方式（需先开「开发人员模式」）：

```powershell
powershell -ExecutionPolicy Bypass -File .\install-app.ps1 -Register
```

| 报错 | 原因 | 处理 |
|---|---|---|
| `0x80073CF3` 找不到框架 | 依赖没装 | 装 `Dependencies\<arch>\*.appx` |
| 证书缺少基本约束扩展 | 装进了「受信任的根」 | 改装「受信任人」 |
| `APPX0107` 签名无效 | 用的是 CA 证书 | 用 `-Type CodeSigningCert` 的证书 |

## 构建

推送到 `master` 触发 Actions → `Build UWP package` → 下载 `uwp-package`。

打 Release：推 `v*` tag 自动编译并挂包。

```bash
git tag v1.3.31 && git push origin v1.3.31
```

本地构建需要 VS 2022 + UWP 工作负载：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-uwp.ps1 -Configuration Release -NoDotNetNative
```

签名证书缓存在 Actions 的 `build/cert`（key `uwp-dev-cert-v9`），**缓存不清理证书就不会变**，升级安装不用重装证书。想长期固定，把 `signing-cert` 工件里的 `.pfx`/`.cer` 存好，或放进 Secrets（`SIGNING_PFX_BASE64`）。

## 目录

```
OneSentenceTile/        UWP 应用
  MainPage.xaml(.cs)    主界面 + 设置面板
  TileSource.cs         双源探测、样式参数
  Package.appxmanifest  清单
tile/                   磁贴静态站生成器
  gen_tile.py           生成 tile.xml + 13 个分类
  config.json           标题/样式/对齐默认值
cloudflare-worker/      Cloudflare Worker 版磁贴
.github/workflows/      构建、发布、磁贴生成
build-uwp.ps1           打包脚本
install-app.ps1         安装脚本
安装.cmd                 一键安装（自动提权）
```

## 磁贴服务器

| | 地址 | 说明 |
|---|---|---|
| 主 | `https://onesentencetile.onesentencetile.workers.dev/tile.xml` | 现取现返回，缓存 1 小时，支持 `?type=d` |
| 备 | `https://bzhcyy.github.io/OneSentenceTile/tile.xml` | 每 15 分钟生成，分类为 `tile-d.xml`；首页 JS 实时取一言 |

## 许可

GPL-3.0，继承自 [chenxuuu/add-one-second-UWP](https://github.com/chenxuuu/add-one-second-UWP)。
