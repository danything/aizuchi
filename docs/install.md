# インストールと設定

## Helm(OCI)

Secret `aizuchi` にキー `slack-bot-token` / `slack-app-token` / `anthropic-api-key` を入れて:

```sh
helm install aizuchi oci://ghcr.io/danything/charts/aizuchi \
  --namespace aizuchi --create-namespace
```

Infisical + External Secrets Operator なら、Infisical のフォルダ(例 `/aizuchi/aizuchi`)に上の 3 つの名前で置いて:

```sh
helm install aizuchi oci://ghcr.io/danything/charts/aizuchi \
  --namespace aizuchi --create-namespace \
  --set externalSecret.enabled=true \
  --set externalSecret.path=/aizuchi/aizuchi
```

k3s の helm-controller なら `HelmChart` CR で同じことができる(`valuesContent` に下記 values)。
チャートの version は CI の run 番号(`0.1.<N>`)、image tag はコミット SHA。

## values

| キー | 既定 | 意味 |
|---|---|---|
| `connector` / `provider` | `slack` / `claude` | 使うコネクタとプロバイダ |
| `slack.threadFollowUp` | `true` | `@aizuchi` で始めたスレッドにメンション無しで追従する |
| `bot.systemPrompt` | (空) | 既定のシステムプロンプトへの追記 |
| `bot.maxHistory` | `50` | LLM に渡す直近メッセージ数 |
| `bot.memoryMaxChars` | `8000` | 記憶 1 スコープの上限文字数 |
| `bot.channelContext` | `20` | スレッド返信で参考に渡すチャンネル直近件数。0 で無効 |
| `persistence.enabled` / `size` / `storageClass` | `true` / `1Gi` / 既定 | 記憶と Anarlog の鍵を置く PVC(`helm.sh/resource-policy: keep`) |
| `claude.model` | `claude-opus-5` | モデル ID(thinking は adaptive のまま) |
| `claude.maxTokens` | `16000` | 1 応答の上限トークン |
| `claude.effort` | (空 = high) | `low` / `medium` / `high` / `xhigh` / `max` |
| `claude.fallbacks` | `default` | 拒絶時のサーバー側フォールバック(beta)。`off` で無効 |
| `claude.webSearchMaxUses` | `0` | 1 応答あたりの Web 検索回数。0 で無効 → [web-search.md](web-search.md) |
| `github.enabled` / `auth` / `appId` / `owners` | `false` / `app` / - / - | GitHub を読む道具。Secret のキー `github-app-private-key` か `github-token` → [github.md](github.md) |
| `openproject.enabled` / `url` | `false` / - | OpenProject を読む道具。Secret のキー `openproject-api-key` → [openproject.md](openproject.md) |
| `webhooks.enabled` / `host` | `false` / - | `/webhooks/` を出す Service + IngressRoute(Traefik) → [webhooks.md](webhooks.md) |
| `anarlog.enabled` / `channel` | `false` / - | Anarlog の会議要約を流すチャンネル ID。`webhooks` と `persistence` が要る |
| `extraEnv` | `[]` | 追加の環境変数 |

## 環境変数(compose / 直接起動)

| 変数 | 意味 |
|---|---|
| `CHAT_CONNECTOR` / `LLM_PROVIDER` | `slack` / `claude`(既定) |
| `SLACK_BOT_TOKEN` / `SLACK_APP_TOKEN` / `SLACK_THREAD_FOLLOWUP` | Slack コネクタ(`off` で追従を切る) |
| `ANTHROPIC_API_KEY` `CLAUDE_MODEL` `CLAUDE_MAX_TOKENS` `CLAUDE_EFFORT` `CLAUDE_FALLBACKS` `CLAUDE_WEB_SEARCH_MAX_USES` `ANTHROPIC_BASE_URL` | Claude プロバイダ |
| `BOT_SYSTEM_PROMPT` `BOT_MAX_HISTORY` `BOT_UPDATE_INTERVAL_MS` `BOT_MEMORY_DIR` `BOT_MEMORY_MAX_CHARS` `BOT_CHANNEL_CONTEXT` | 共通 |
| `GITHUB_APP_ID` + `GITHUB_APP_PRIVATE_KEY`(PEM)、または `GITHUB_TOKEN` + `GITHUB_OWNERS` | GitHub の道具(任意) |
| `OPENPROJECT_URL` + `OPENPROJECT_API_KEY` | OpenProject の道具(任意。両方必須) |
| `ANARLOG_SLACK_CHANNEL`(必須)`ANARLOG_STORE_DIR` `ANARLOG_PUBLIC_URL` | Anarlog の webhook 受信(任意) |

## ヘルスチェック

- `GET /healthz` — プロセスが生きていれば 200
- `GET /readyz` — コネクタが接続を確立するまで 503
- `POST /webhooks/{name}` — `webhooks.enabled` のときだけ Ingress に出る
