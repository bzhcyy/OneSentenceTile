# Cloudflare Worker：实时磁贴 + Pages 保活

这个 Worker 有两件事：

1. **实时磁贴**：`/tile.xml` 每次请求现取一言，`Cache-Control: no-store`，永远不会「停在某一条」。
2. **给 Pages 保活**：Cron Trigger 每 15 分钟调一次 GitHub Actions 的 `workflow_dispatch`，
   让 `https://<owner>.github.io/<repo>/tile.xml` 真的 15 分钟重新生成一次。

## 为什么需要第 2 件事

GitHub Actions 文档写得很清楚：`schedule` 只是「尽力而为」，高峰期会推迟，忙起来直接跳过。
本仓库实测的定时运行间隔：

```
16:02 → 21:47  （5 小时 45 分）
21:47 → 02:23  （4 小时 36 分）
02:23 → 11:05  （8 小时 42 分）
```

而工作流写的是 `cron: '*/15 * * * *'`。

Pages 是纯静态托管，自己没有「刷新」能力，只会把上一次部署的 `tile.xml` 原样发出去。
于是 App 里选了「Pages 优先」、或者 Worker 连不上（`workers.dev` 在国内经常被墙）时，
磁贴会停在几个小时前那条 —— 这就是「备用磁贴服务器 15 分钟后不更新」的根因。

Worker 的 Cron Trigger 由 Cloudflare 调度，不会像 GitHub 那样被饿死；它只是发一个
「请跑一次工作流」的 API 请求，真正的生成和部署仍然在 GitHub 上完成，所以 `github.io`
这个国内可直连的投递地址不变。

## 部署

```bash
cd cloudflare-worker
npm i -g wrangler        # 只需一次
wrangler login

# 1) GitHub 令牌：fine-grained PAT，只授权 bzhcyy/OneSentenceTile 仓库，
#    权限选 Actions: Read and write（不需要 Contents 权限）
wrangler secret put GITHUB_TOKEN

# 2) 可选：给 /refresh 加一个口令，防止别人乱点
wrangler secret put REFRESH_TOKEN

# 3) 部署（wrangler.toml 里已经配好 */15 的 cron）
wrangler deploy
```

## 验证

```bash
# 1. cron 有没有配好（看 scheduled 事件）
wrangler tail

# 2. 手动触发一次 Pages 重新生成（需要 REFRESH_TOKEN）
curl -X POST "https://onesentencetile.onesentencetile.workers.dev/refresh" \
     -H "x-refresh-token: 你的口令"

# 3. 等 1～2 分钟，看 Pages 上的快照年龄有没有归零
python tile/check_freshness.py --base-url https://bzhcyy.github.io/OneSentenceTile
```

`/health` 里的 `pageRefresh.enabled` 为 `true`，说明 `GITHUB_TOKEN` 已经配好，
cron 触发链路是通的；为 `false` 说明只部署了 Worker、还没配密钥。

## 环境变量

| 名称 | 必需 | 说明 |
|---|---|---|
| `GITHUB_TOKEN` | cron / `/refresh` 需要 | fine-grained PAT，仓库只给这一个，权限 `Actions: Read and write` |
| `REFRESH_TOKEN` | 可选 | `/refresh` 的口令；设了就必带 `?token=` 或 `x-refresh-token` 头 |
| `GITHUB_REPO` | 否 | 默认 `bzhcyy/OneSentenceTile` |
| `TILE_WORKFLOW` | 否 | 默认 `tile-pages.yml` |
| `TILE_REF` | 否 | 默认 `master`（工作流所在分支） |

## 没有 Cloudflare 账号怎么办

任何能可靠跑 cron 的地方都行，只要它会发这个请求：

```bash
curl -X POST \
  -H "Authorization: Bearer <PAT>" \
  -H "Accept: application/vnd.github+json" \
  https://api.github.com/repos/bzhcyy/OneSentenceTile/actions/workflows/tile-pages.yml/dispatches \
  -d '{"ref":"master"}'
```

`cron-job.org`、UptimeRobot 的「HTTP 监控」、自己的一台小机器都可以。
另外 App 侧也做了兜底：检测到 Pages 快照过期、且 Worker 连不上时，会直接连
`v1.hitokoto.cn` 自己生成磁贴，所以就算两边都停了，开着 App 的时候磁贴也不会卡住。
