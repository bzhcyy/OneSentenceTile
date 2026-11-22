# 一言 Tile

> Windows 10 / 11 的桌面磁贴，每小时给你一句「一言」。
> 数据来自 [hitokoto.cn](https://hitokoto.cn)，服务器托管在 Cloudflare Worker 与 GitHub Pages。

<p align="center">
  <a href="https://bzhcyy.github.io/OneSentenceTile/"><b>🌐 磁贴服务器</b></a> ·
  <a href="https://github.com/bzhcyy/OneSentenceTile/releases">📦 Releases</a> ·
  <a href="https://developer.hitokoto.cn/">📖 一言 API</a>
</p>

---

## 它长什么样

磁贴（大尺寸）：

```
每日一言
　　先别说话，再别康桥。
　　　　　　—— 无名氏《弱智吧》
```

- 标题 **每日一言** 34px（粗体），正文 20px 首行缩进两格，出处 15px 右对齐
- 磁贴完全透明，壁纸由系统磨砂透出；也可以关掉图标、只留文字
- 每秒都在变的只有你的心情，文案每小时更新一条

主界面：中间大字显示当前这条一言，左上角齿轮进设置。

## 功能

| | |
|---|---|
| 🗂 **13 种分类** | 随机 / 动画 / 漫画 / 游戏 / 文学 / 原创 / 来自网络 / 其他 / 影视 / 诗词 / 网易云 / 哲学 / 抖机灵 |
| ⏱ **刷新间隔** | 实时（点一下就换）/ 30 分钟 / 1 / 6 / 12 / 24 小时 |
| 🔀 **双服务器** | Worker 优先，连不上自动退回 GitHub Pages；也能手动指定优先谁 |
| 🖼 **壁纸** | 关闭 / 本地图片 / 网络地址 / 文件夹轮播（1–60 分钟可调） |
| 🔠 **磁贴文字** | 标题 / 正文 / 出处 三段各自选字号档位（12/15/20/24/34/46，含淡色版），即时生效 |
| 🖋 **界面字体** | 系统默认 / 微软雅黑 / 等线 / 宋体 / 楷体 / Consolas |
| 🎨 **外观** | 背景色 + 亚克力透明度、字体颜色、全屏、屏幕常亮；右上角标题栏按钮跟随主题（有壁纸用白、无壁纸用黑） |
| 🧩 **系统集成** | 语音指令「一秒」打开设置 |

> **关于磁贴字体**：Windows 磁贴 XML 只接受 `hint-style`（固定 7 档字号，粗细随档位变化），
> **不能指定字体族和任意像素值**。所以「界面字体」只作用于 App 界面；磁贴的字号/粗细靠上面的
> 三段档位来调，参数会拼进磁贴 URL（`?titleStyle=…&quoteStyle=…&attrStyle=…`），
> Worker 会据此改 XML；如果退回 Pages 静态文件，则使用服务器默认样式。

## 目录

```
OneSentenceTile/           UWP 应用（C#）
  MainPage.xaml            主界面 + 设置面板
  TileSource.cs            Worker / Pages 双源探测与分类切换
  Package.appxmanifest     清单（Identity: KienSticn.OneSentenceTile）
tile/                      磁贴静态站生成器（GitHub Pages 用）
  gen_tile.py              取一言 → 生成 tile.xml + 13 个分类文件
  config.json              标题/样式/字数上限
.github/workflows/
  build-uwp.yml            云端打包 UWP
  tile-pages.yml           每小时生成并发布磁贴
  release.yml              打 tag 自动出 Release
build-uwp.ps1              本地/CI 打包脚本（自签证书 + SDK 自适应）
push-to-github.ps1         无 Git 环境下一键同步到 GitHub
set-tile-url.ps1           改磁贴服务器地址
```

## 磁贴服务器

| | 地址 | 说明 |
|---|---|---|
| 主 | `https://onesentencetile.onesentencetile.workers.dev/tile.xml` | 每次请求现取，缓存 1 小时；支持 `?type=d` |
| 备 | `https://bzhcyy.github.io/OneSentenceTile/tile.xml` | 每小时生成；分类各自一个文件 `tile-d.xml` |

App 启动时真实 GET 一次优先源，4 秒超时，内容含 `<tile` 才算可用，否则换另一个。
两个都不通时仍按优先源走，磁贴内容由清单里的默认地址兜底。

Worker 源码见 `worker.js`（部署到 Cloudflare Workers，免费额度 10 万请求/天）。

## 构建

**云端（推荐）**：push 后 Actions → `Build UWP package` → 下载 `uwp-package` → 右键 `Add-AppDevPackage.ps1` 运行。

**打 Release**：推一个 `v*` tag，`release.yml` 会自动编译并把 `.appxbundle` 挂到 Release 上：

```bash
git tag v1.3.0 && git push origin v1.3.0
```

**本地**：需要 VS 2022 + UWP 工作负载。

```powershell
powershell -ExecutionPolicy Bypass -File .\build-uwp.ps1 -Configuration Release -NoDotNetNative
```

签名证书按清单里的 `Publisher`（`CN=KienSticn`）自动生成；换了 Publisher 脚本会自动重签。

## 安装

1. 卸载旧版（历史版本 Identity 是 `800948F61A16.1s`）
2. 装新的 `OneSentenceTile_<版本>_x64.appxbundle`（首次需信任自签证书）
3. 打开 App → 设置 → 打开「动态磁贴」

## 许可

GPL-3.0（继承自上游 [chenxuuu/add-one-second-UWP](https://github.com/chenxuuu/add-one-second-UWP)）。
