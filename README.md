# 一言 Tile — 磁贴服务器（Worker + Pages 双份）

两个服务器同时在线，内容格式完全一致，随时可以换：

| | 地址 | 说明 |
|---|---|---|
| **主：Cloudflare Worker** | `https://onesentencetile.onesentencetile.workers.dev/tile.xml` | 每次请求现取一言，无缓存、无需定时任务；免费 10 万请求/天 |
| **备：GitHub Pages** | `https://bzhcyy.github.io/OneSentenceTile/tile.xml` | 每小时生成一次；`github.io` 国内基本可直连 |

App 当前指向 **Worker**（`Package.appxmanifest` 与 `MainPage.xaml.cs`）。

## 自动切换（Worker 挂了自动用 Pages）

App 里的逻辑在 `OneSentenceTile/TileSource.cs`：

1. **每次启动 App**（`App.OnLaunched`）都会探一次 Worker：GET `/tile.xml`，4 秒超时，
   返回内容里出现 `<tile` 才算「通」；
2. 通 → 用 Worker（每次现取，缓存 1 小时）；不通 → 用 Pages；
3. 探测是异步的，不卡界面；探完立刻 `StartPeriodicUpdate(选中的地址, HalfHour)`；
4. 清单里的 `<uap:TileUpdate>` 默认写 **Pages**——App 从没打开过也不会白板。

手动改地址（两个都能单独改，只写一个另一个保持原值）：

```powershell
.\set-tile-url.ps1 -WorkerUrl https://onesentencetile.onesentencetile.workers.dev
.\set-tile-url.ps1 -PagesUrl  https://bzhcyy.github.io/OneSentenceTile
.\set-tile-url.ps1 -WorkerUrl https://a.workers.dev -PagesUrl https://b.github.io/x -Verify   # 顺带验活
```

改完 push → 跑一次 **Build UWP package** → 装新包（地址是编译进包里的）。

> 限制：探测只在 App 启动时做。App 长期不打开，磁贴就一直按清单里的 Pages 地址更新；
> 想让它在后台也能切换，需要加后台任务（`ApplicationTrigger`/`MaintenanceTrigger`），没必要。

## 两个前提

1. **Pages 备份要真的能跑**：Settings → Pages → Source 选 **GitHub Actions**，否则
   `Publish live tile` 每小时都会红一次（可以先去 Actions 里 **Disable workflow** 关掉它）。
2. **Worker 在国内的可用性**：`workers.dev` 子域常被 DNS 污染，磁贴不刷新时先开浏览器验证
   `…/health`。要彻底稳定，需要绑一个自己的域名（见下）。

## 关于域名要不要花钱

Cloudflare Registrar 只是按成本价卖域名，**没有免费域名**（你截图里 `addonesecondtile.com` 是 $11.17/年）。
三条路线：

- **不花钱，用 github.io**（推荐先这样）：Pages 免费、国内基本能通，就是内容每小时更新一次。
- **花几块钱买个便宜域名**：`.top` / `.xyz` 首年常见 ¥1–10（阿里云/腾讯云/Namecheap/Porkbun），
  注册后把 NS 指到 Cloudflare（免费套餐），再在 Worker → Settings → Domains & Routes → Custom domain 绑定。
  之后每年续费也就几块到十几块。**这是让 Worker 在国内稳定的唯一可靠办法。**
- **免费子域**（is-a.dev、js.org、eu.org）：只能 CNAME 到静态托管（如 `*.pages.dev`），
  **绑不了 Worker 自定义域名**（Worker 要求域名托管在 Cloudflare），而且都要审核、等几天起步。

一句话：想用 Worker + 自定义域名，绕不开一个真域名；不想花钱就用 github.io，两套并存正好互补。

---

## 界面（本轮改版）

应用标识（本轮已改）：

| 项 | 值 |
|---|---|
| Identity Name | `KienSticn.OneSentenceTile` |
| Publisher | `CN=KienSticn` |
| DisplayName | 一言 Tile |
| PublisherDisplayName | KienSticn |
| Version | 1.2.0.0 |

> ⚠️ **Identity / Publisher 一改，这就是一个新 App**：装之前先把旧版（`800948F61A16.1s` / `CN=chenx`）
> 卸载掉，否则会变成两个应用并存、或者报 0x80073CF3 发布者不匹配。
> 签名证书也换成 `CN=KienSticn`：CI 的证书缓存 key 已改成 `uwp-dev-cert-v2`，
> 本地 `build/cert` 里的旧证书会被 `build-uwp.ps1` 自动识别并重签。

主界面：

- 顶部中间固定 **每日一言**，下面大字显示磁贴当前那条语录（`FontSize=34`、自动换行、居中），
  右下角灰字显示「—— 作者《出处》」；
- 删掉了原来的 `6362:01:33:31` 大数字、底部的「时钟图标 + +1s」按钮、「已贡献 N 秒」计数；
- 左上角保留「你的实际时间」，左下角保留齿轮设置。

设置面板：

| 保留/新增 | 说明 |
|---|---|
| 全屏 | 不变 |
| 动态磁贴 | 开关文案去掉「半小时一刷新」 |
| **磁贴刷新间隔**（新） | 30 分钟 / 1 小时 / 6 小时 / 12 小时 / 24 小时 |
| **磁贴源：Worker / Pages 优先**（新） | 存进 RoamingSettings，App 启动时按优先级探测 |
| **一言分类**（新） | 随机 / 动画 / 漫画 / 游戏 / 文学 / 原创 / 来自网络 / 其他 / 影视 / 诗词 / 哲学 / 抖机灵 |
| 屏幕常亮 | 不变 |
| 背景/字体颜色 | 不变 |
| 本项目地址（新） | `https://github.com/bzhcyy/OneSentenceTile` |

**删掉**：自动 +1s 开关、音效开关（含 2333 秒限制）、关闭软件后语音指令那段说明、整段「关于」。

> 刷新间隔只能选上表这 5 档——这是 Windows `PeriodicUpdateRecurrence` 枚举的固定值，
> 系统不让磁贴按任意分钟数轮询。
>
> **分类怎么生效**：
> - Worker：拼成 `?type=d` → Worker 转成 `?c=d` 请求 hitokoto，缓存键也带分类；
> - Pages：生成器对每个分类各生成一个文件（`tile.xml` + `tile-a.xml` … `tile-l.xml` 共 13 个），
>   App 连 Pages 时按分类换文件名，所以兜底路径下分类**同样有效**。
>
> 「网易云(j)」官方已于 2022-11-18 停用该插件，选中后接口大概率失败，
> 此时会退回仓库内置的语录（磁贴不会空，但内容不是网易云）。
> 分类码参考 <https://developer.hitokoto.cn/sentence/>。

# 附：GitHub Pages 方案（已停用，留作参考）

# 用 GitHub Pages 当磁贴服务器（替代已失效的 Cloudflare Worker）

原来的磁贴地址 `https://add-one-second.papapoi.workers.dev/` 已经返回 `404 error code: 1042`，
`/health`、`/pic/1.png` 同样 404——线上根本没有可执行的 Worker 脚本了（详见上一份《磁贴服务器可用性报告》）。

这套东西把磁贴服务器搬到 **GitHub 自己的基础设施**上：

- 一个 GitHub Actions 定时任务（每 30 分钟）重新生成 `tile.xml`
- 通过 Pages 部署成静态站点：`https://<owner>.github.io/<repo>/tile.xml`
- 图片直接从仓库里的 `OneSentenceTile/Assets/pic/*.png` 发布成 `https://.../pic/<n>.png`

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
   - `OneSentenceTile/Package.appxmanifest` 的 `<uap:TileUpdate UriTemplate="...">`
   - `OneSentenceTile/MainPage.xaml.cs` 的 `tileContent = new Uri("...")`
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
- 换图片：替换 `OneSentenceTile/Assets/pic/N.png`。要求：**≤ 1024×1024、≤ 200KB**
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
powershell -ExecutionPolicy Bypass -File .\push-to-github.ps1 -Token github_pat_你的token -DryRun
powershell -ExecutionPolicy Bypass -File .\push-to-github.ps1 -Token github_pat_你的token
```

可选参数：`-Repo bzhcyy/OneSentenceTile`、`-Branch master`、`-Message "..."`、
`-Proxy http://127.0.0.1:7897`（走代理时）。脚本会自动跳过自身、`_site/`、`build/`、`*.pfx`。

跑完会打印 commit 链接；Actions 会自动重新生成磁贴（`tile/**` 有变动即触发）。

> 嫌建 Token 麻烦的话：装个 GitHub Desktop，把这堆文件放进本地克隆目录，
> 它会自动列出「删除的文件」，直接 Commit + Push 也一样。
