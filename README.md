# slack-claude-bot

Claude Console の API キーで動く、対話型 Slack ボット。**.NET 10 Native AOT** の単一バイナリで、
Socket Mode なので **公開 URL も Ingress も要らない**。k3s に Helm で置いてそのまま動く。

```
@Claude に質問 / DM で話しかける / ボットが居るスレッドで続けて話す
  → スレッド(DM はそのまま)に "…" を投稿
    → Claude の応答をストリーミングで 1.5 秒ごとに書き足す
      → 完了したら Markdown を mrkdwn に整えて確定
```

- モデルは既定で `claude-opus-5`(thinking は adaptive のまま。`effort` だけ調整できる)
- スレッドの履歴を読んで **会話として続く**。自分の発言は `assistant`、他は `user` に組み直す
- 拒絶時のサーバー側フォールバック(`fallbacks: "default"`、beta)を既定で有効。`CLAUDE_FALLBACKS=off` で無効
- 公式 Anthropic C# SDK はリフレクション JSON のため Native AOT では起動しない(実測)。
  Slack / Claude どちらも `HttpClient` + `System.Text.Json` ソースジェネレータで直接叩いている
- テストは **xUnit v3 + Microsoft.Testing.Platform**(テストプロジェクト自体が実行ファイル)

## 反応する条件

| 場面 | 反応 |
|---|---|
| DM | 常に返す(トップレベルにはトップレベルで、スレッドにはスレッドで) |
| チャンネルで `@Claude` メンション | そのメッセージのスレッドに返す |
| ボットが既に発言しているスレッドの続き | メンション無しでも返す |
| メンション無しのチャンネル発言、編集・削除、他ボット、自分 | 無視 |

`app_mention` と `message` は同じ発言で両方届き、Slack の再送もあるので `channel:ts` で重複排除している。

## Slack アプリを作る

1. https://api.slack.com/apps → **Create New App → From a manifest** に [`slack-app-manifest.yaml`](slack-app-manifest.yaml) を貼る
2. **Basic Information → App-Level Tokens** で `connections:write` のトークンを作る → `xapp-...`
3. **Install to Workspace** → **OAuth & Permissions** の Bot User OAuth Token → `xoxb-...`
4. Claude Console(https://platform.claude.com)で API キーを作る → `sk-ant-...`

## インストール(Helm / OCI)

Secret `slack-claude-bot` にキー `slack-bot-token` / `slack-app-token` / `anthropic-api-key` を入れて:

```sh
helm install slack-claude-bot \
  oci://ghcr.io/danything/charts/slack-claude-bot \
  --namespace slack-claude-bot --create-namespace
```

Infisical + External Secrets Operator なら、Infisical のフォルダ(例 `/slack-claude-bot/slack-claude-bot`)に
上の 3 つの名前でシークレットを置いて:

```sh
helm install slack-claude-bot \
  oci://ghcr.io/danything/charts/slack-claude-bot \
  --namespace slack-claude-bot --create-namespace \
  --set externalSecret.enabled=true \
  --set externalSecret.path=/slack-claude-bot/slack-claude-bot
```

k3s の helm-controller なら `HelmChart` CR で同じことができる(`valuesContent` に上記 values)。

主な values:

| キー | 既定 | 意味 |
|---|---|---|
| `claude.model` | `claude-opus-5` | モデル ID |
| `claude.maxTokens` | `16000` | 1 応答の上限トークン |
| `claude.effort` | (空 = high) | `low` / `medium` / `high` / `xhigh` / `max` |
| `claude.fallbacks` | `default` | 拒絶時のサーバー側フォールバック。`off` で無効 |
| `claude.systemPrompt` | (空) | 既定のシステムプロンプトへの追記 |
| `bot.maxHistory` | `50` | Claude に渡す直近メッセージ数 |

## ローカルで動かす

```sh
export SLACK_BOT_TOKEN=xoxb-... SLACK_APP_TOKEN=xapp-... ANTHROPIC_API_KEY=sk-ant-...
dotnet run --project src/SlackClaudeBot.csproj      # JIT
dotnet publish src/SlackClaudeBot.csproj -c Release -o out && ./out/slack-claude-bot   # AOT(clang が必要)
dotnet run --project tests/SlackClaudeBot.Tests.csproj  # テスト
```

環境変数は values と同名の `CLAUDE_MODEL` / `CLAUDE_MAX_TOKENS` / `CLAUDE_EFFORT` / `CLAUDE_FALLBACKS` /
`CLAUDE_SYSTEM_PROMPT` / `BOT_MAX_HISTORY`。`ANTHROPIC_BASE_URL` でエンドポイントも差し替えられる。

## ヘルスチェック

- `GET /healthz` — プロセスが生きていれば 200
- `GET /readyz` — Socket Mode が `hello` を受け取るまで 503

## 構成

```
src/
  Program.cs            起動・環境変数検証・auth.test・/healthz
  Slack/                Web API(HttpClient)、Socket Mode(ClientWebSocket)、JSON ソースジェネレータ
  Claude/               /v1/messages のストリーミング(SSE パーサ)、JSON ソースジェネレータ
  Bot/                  反応判定、履歴→messages 変換、Markdown→mrkdwn、ストリーム書き込み、設定
tests/                  上の純粋関数とシリアライズ形状のテスト(xUnit v3 / MTP)
charts/slack-claude-bot Deployment + 任意の ExternalSecret
```
