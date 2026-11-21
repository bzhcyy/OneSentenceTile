# 用 GitHub Pages 当磁贴服务器（替代已失效的 Cloudflare Worker）

原来的磁贴地址 `https://add-one-second.papapoi.workers.dev/` 已经返回 `404 error code: 1042`，
`/health`、`/pic/1.png` 同样 404——线上根本没有可执行的 Worker 脚本了（详见上一份《磁贴服务器可用性报告》）。

这套东西把磁贴服务器搬到 **GitHub 自己的基础设施**上：

- 一个 GitHub Actions 定时任务（每 30 分钟）重新生成 `tile.xml`
- 通过 Pages 部署成静态站点：`https://<owner>.github.io/<repo>/tile.xml`
- 图片直接从仓库里的 `addOneSecond/Assets/pic/*.png` 发布成 `https://.../pic/<n>.png`

不需要 Cloudflare 账号，不需要 VPS，不需要任何服务器常驻进程。

## 一、放哪些文件（覆盖到仓库根目录）

```
tile/gen_tile.py                 # 生成器：语录 + 随机图 → tile.xml（自带 4 个自适应 binding 与自检）
tile/quotes.json                 # 90 条语录（从原 Worker 里提取，跟以前一模一样）
tile/config.json                 # 外观/裁剪/上限配置（baseUrl 由 set-tile-url.ps1 写入）
.github/workflows/tile-pages.yml # 每 30 分钟生成 + 发布到 Pages + 部署后自检
set-tile-url.ps1                 # 把磁贴地址写进 manifest + MainPage.xaml.cs + config.json
```

`tile/` 和 `set-tile-url.ps1` 放仓库根目录；`.github/workflows/tile-pages.yml` 合并进已有的 `.github` 目录
（不会影响原来的 `build-uwp.yml`）。

## 二、一次性配置（6 步）

1. **推仓库**（公开仓库；免费账号的 Pages 只对 public 开放）。
2. **Settings → Pages → Build and deployment → Source** 选 **GitHub Actions**
   （不要选 "Deploy from a branch"，这套用的是 Actions 部署）。
3. **改成你的地址**（在仓库根目录）：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\set-tile-url.ps1 -BaseUrl https://<你的用户名>.github.io/<仓库名>
   ```

   它会把三处改一致：
   - `addOneSecond/Package.appxmanifest` 的 `<uap:TileUpdate UriTemplate="...">`
   - `addOneSecond/MainPage.xaml.cs` 的 `tileContent = new Uri("...")`
   - `tile/config.json` 的 `baseUrl`

   > 仓库名带大写时，Pages 地址里的**用户名必须小写**、仓库名按创建时的写法。
   > 不确定就先跑一遍 Action，看日志里打印的「磁贴地址」。
   > 也可以在 Settings → Secrets and variables → Actions → **Variables** 建一个
   > `TILE_BASE_URL`，工作流会优先用它。

4. **提交推送**，然后 Actions → **Publish live tile to GitHub Pages** → Run workflow（或等定时触发）。
5. 跑完点进那次运行，Summary 里会直接给出磁贴地址，并已自动 curl 校验过。
6. **重新打包 App**：Actions → **Build UWP package** → Run workflow，装新包（旧包里的地址还是 workers.dev）。

## 三、验证清单

按顺序在浏览器打开（4 条都通才算成功）：

| 地址 | 期望 |
| --- | --- |
| `https://<owner>.github.io/<repo>/tile.xml` | 直接看到 `<tile>…</tile>`，4 个 `<binding template="Tile…">` |
| `https://<owner>.github.io/<repo>/health.json` | JSON，`"ok": true`，`generatedAt` 是最近半小时内 |
| `https://<owner>.github.io/<repo>/pic/1.png` | 直接看到图片 |
| 手机上/桌面开始菜单 | 中/宽/大磁贴：背景图 + 语录；小磁贴只有图 |

装完 App 后，去 App 设置里把「动态磁贴」**关一次再打开**，触发一次立即刷新。

## 四、想改内容

全在 `tile/config.json`：

| 字段 | 作用 |
| --- | --- |
| `title` | 磁贴标题，默认 `每日一言`（`showTitle` 打开时显示在第一行） |
| `showTitle` | 是否显示标题 |
| `showCounter` | 是否显示第二行 |
| `counterMode` | `counter` = 7 倍速计数；`beijing` = 总天数 + 北京时分；`clock` = 按 `clockFormat` 的北京时间 |
| `clockFormat` | clock 模式的格式，默认 `%Y年%m月%d日` |
| `utcOffset` | 时区偏移，`8` = 北京时间 |
| `counterLabel` | 计数行前缀，例如 `时间众筹总计 `，默认空 |
| `source` | 语录来源：`hitokoto` = 一言接口随机取（默认）；`local` = 仓库里的 `quotes.json` |
| `hitokotoUrl` | 一言接口地址，默认 `https://v1.hitokoto.cn/`（不带参数 = 随机类型） |
| `quotePrefix` | 正文前缀（默认留空）；想写成 `每日一言：正文` 就填 `每日一言：` |
| `showAttribution` | 是否在正文下面显示「—— 作者《出处》」 |
| `attributionSep` | 出处前的分隔符，默认 ` —— `（空格+破折号） |
| `backgroundImage` | **留空 = 透明磁贴**（透出壁纸 + 系统磨砂，就是第二张图的效果）；填 `bg.png` 用生成的深色底图；也可填任意 https 图片 |
| `branding` | `logo` = 右下角显示应用小图标；`none` = 什么都不显示 |
| `smallText` | 71×71 小磁贴显示的一行小字（默认 `每日一言`） |
| `clipMedium` / `clipWide` / `clipLarge` | 各尺寸最多显示多少字（超出截断加「…」） |
| `maxPayloadBytes` | 载荷上限，默认 4600；**Windows 硬上限是 5KB**，别调大 |

排版（以 310×310 大磁贴为例）：

```
每日一言                                  ← title（24px）
　　正义是杀不完的，因为真理永远存在！    ← subtitle（20px），开头空两个全角空格，自动换行
                —— 闻一多《最后一次讲演》 ← baseSubtle（15px），hint-align="right" 右对齐
```

正文的缩进是写死在 XML 里的两个全角空格（`\u3000\u3000`），需要改就改 `gen_tile.py` 里的 `INDENT`。

标题 / 计数 / 日期这几行现在默认都关掉了；想加回来把 `showTitle` / `showCounter` 打开即可。

> 磁贴默认**不放图片**（完全透明，透出壁纸）。想要不透明底板，把 `backgroundImage` 改成 `bg.png`；
> 想换成照片，直接填一个公网 https 图片地址（≤1024×1024、≤200KB）。

- 换语录：改 `tile/quotes.json`（字符串数组，别写 `&`、`<` 这类字符也行，生成器会转义）。
- 换图片：替换 `addOneSecond/Assets/pic/N.png`。要求：**≤ 1024×1024、≤ 200KB**
  （系统的硬限制，超了磁贴直接不显示；现有图片是 300×300、最大 168KB，刚好安全）。
- 改完 push 即可，工作流会重新生成并部署。

## 五、本地预览（不推 GitHub 也能看效果）

```powershell
cd <仓库根目录>
python tile\gen_tile.py --out _site --base-url https://yourname.github.io/your-repo
# 用浏览器打开 _site\tile.xml 看结构；_site\health.json 里有本次选中的图/语录
```

`--check` 只做结构自检不写文件；`--quote-index N` 固定用第 N 条语录方便调样式。

## 六、已知限制（重要）

1. **定时任务不是精确的**：GitHub 的 `schedule` 是尽力而为，高峰期可能晚十几分钟；磁贴本身也是
   30 分钟刷一次，所以影响不大。工作流现在跑 `*/15`（Pages 每仓库每小时约 10 次部署上限，别再加密）。
2. **三处缓存决定「文案多久变一次」**：GitHub cron（1 小时）→ Pages CDN（约 10 分钟）→
   Windows 自己的 30 分钟轮询。所以最坏情况约 30–55 分钟才换一次语录，这是平台限制，不是脚本 bug。
3. **60 天不活动会被停**：GitHub 会停掉长期无提交仓库的定时工作流。随便 push 一次，
   或在 Actions 里点一次 Run workflow 就能恢复。
4. **`github.io` 在国内同样被墙**：磁贴更新走 WinHTTP 通道，**不跟随 Clash 的系统代理**，
   所以要么开着 TUN 模式/透明代理，要么把磁贴地址换到国内可直连的域名（Cloudflare 自建域名、
   自己的服务器都行——客户端只要改 `set-tile-url.ps1` 里那一个地址）。
   拉取失败时 Windows 会一直保留上一次成功的磁贴，看起来就像「文案不变」。
5. **Pages 有约 10 分钟 CDN 缓存**：`tile.xml` 更新时间可能比 Actions 完成时间晚几分钟，属正常。
6. **仓库必须 public**（免费账号）；私有仓库需要 Pro/Team。
7. **JSON/XML 里的中文**：生成器统一 UTF-8 输出，别用记事本另存成 ANSI。

## 七、和 Worker 版对比

| | Cloudflare Worker（现在挂了） | GitHub Pages（这套） |
| --- | --- | --- |
| 每次请求换语录 | 是（随机） | 否（每 30 分钟换一次） |
| 计数行实时性 | 准（请求时算） | 30 分钟粒度 |
| 维护 | 要登 Cloudflare 后台 | 改仓库 push 即可 |
| 依赖 | 免费 Worker + 外部时间接口 | 公开仓库 + Actions 分钟数 |
| 国内可直连 | 否（已被 DNS 污染） | 否（github.io 同样被墙） |

> 注意：一言是在**生成时刻**取的，如果开 `showCounter`，计数行同样只有生成时刻的值；
> 语录/图片本来就只需要半小时粒度，不影响观感。

## 八、回滚

把 `Package.appxmanifest` 和 `MainPage.xaml.cs` 的地址改回 `https://add-one-second.papapoi.workers.dev/`
（或任何新的服务器地址）重新打包即可；`tile/` 和工作流留着不碍事。

## 九、一键上传（不用装 Git）

仓库根目录里有 `push-to-github.ps1`，用 GitHub API 一次性同步（新增/修改/**删除**都算），
而且只产生**一个提交**——用网页拖文件的办法删不掉多余的 76 个旧文件，这个可以。

1. 建 Token：GitHub → Settings → Developer settings → Personal access tokens →
   **Fine-grained tokens** → 新建，`Repository access` 只选这个仓库，
   `Permissions → Contents` 选 **Read and write**。
2. 在解压出来的目录里跑（先 DryRun 看一眼清单）：

```powershell
powershell -ExecutionPolicy Bypass -File .\push-to-github.ps1 -Token ghp_你的token -DryRun
powershell -ExecutionPolicy Bypass -File .\push-to-github.ps1 -Token ghp_你的token
```

可选参数：`-Repo bzhcyy/add-one-second-UWP`、`-Branch master`、`-Message "..."`、
`-Proxy http://127.0.0.1:7897`（走代理时）。脚本会自动跳过自身、`_site/`、`build/`、`*.pfx`。

跑完会打印 commit 链接；Actions 会自动重新生成磁贴（`tile/**` 有变动即触发）。

> 嫌建 Token 麻烦的话：装个 GitHub Desktop，把这堆文件放进本地克隆目录，
> 它会自动列出「删除的文件」，直接 Commit + Push 也一样。
