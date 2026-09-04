# aizuchi

チャットで相槌を打つ AI ボット。**コネクタ(Slack …)× プロバイダ(Claude …)** を差し替えられる器で、
.NET 10 Native AOT の単一バイナリ。Socket Mode なので公開 URL も Ingress も要らず、k3s に Helm で置けば動く。

```
@aizuchi に質問 / DM で話しかける / @aizuchi で始めたスレッドで続けて話す
  → コネクタが「返すべき発言」だけを拾う(判定・重複排除はコネクタの中)
    → 履歴を user / assistant に組み直して LLM にストリーミングで投げる
      → 仮メッセージを 1.5 秒ごとに書き足し、終わったら整えて確定
```

| 種別 | 実装済み | 設定 |
|---|---|---|
| コネクタ | `slack`([connectors/slack](connectors/slack/README.md)) | `CHAT_CONNECTOR` / values `connector` |
| プロバイダ | `claude`(Claude Console の API キー) | `LLM_PROVIDER` / values `provider` |

## 記憶(memory)

毎回スレッドで社内の文脈を説明しなくて済むよう、ボットは **Markdown の記憶**を持つ。
system prompt の末尾に差し込まれ、LLM 自身が道具(`memory_append` / `memory_replace`)で書き換える。

- **共有**(ワークスペース全体)と **チャンネルごと** の 2 スコープ。迷ったら共有に入る
- 「これ覚えといて」「さっきのは間違い、正しくは…」「その項目は忘れて」が普通の会話で通る。保存したら一言添えて返す
- 手動: `@aizuchi memory` で今の中身を表示。`memory`(または `memory channel`)に続けて全文をコードブロックで送ると丸ごと置き換え
- 上限は 1 スコープ 8,000 文字(`bot.memoryMaxChars`)。近づくと LLM が整理して書き直す
- 置き場は `BOT_MEMORY_DIR`(k3s は PVC の `/data/memory`、compose は volume)。`off` で機能ごと無効
- 中身は Slack の全員が読める・書ける前提。個人の秘密は入れない

スレッドへの返信では、スレッドの外の **チャンネル直近 20 件**(`bot.channelContext`)も「最近の流れ」として渡す。
複数人のスレッドでは発言に `[名前]` を前置する(Slack の `users:read` スコープが要る。無ければ ID のまま)。

## GitHub を読む

`github.enabled=true` にすると、LLM が **読み取り専用**の道具でリポジトリを調べられる。
「denpa の open な PR を見せて」「aizuchi の Bot.cs どうなってる」「昨日の infisical-push-bridge のコミット何」がそのまま通る。

| 道具 | 中身 |
|---|---|
| `github_repos` | 読めるリポジトリ一覧。名前の揺れはまずこれで確かめる |
| `github_search` | コード / Issue / PR の横断検索(GitHub の検索構文) |
| `github_read_file` | ファイルを行範囲で読む(既定 300 行、1MB まで) |
| `github_list` | Issue / PR の一覧(状態・ラベル) |
| `github_get` | Issue / PR の本文・コメント・変更ファイル |
| `github_commits` | 直近のコミット(ブランチ・パス・日時で絞る) |

書き込み(Issue 作成やコメント)は持たない。読める範囲は App のインストール先(または `github.owners`)に閉じ、
それ以外の owner は道具側で断る。

### GitHub App の作り方(推奨)

1. https://github.com/settings/apps → **New GitHub App**。名前は `aizuchi`、Webhook は無効
2. Repository permissions: **Contents / Issues / Pull requests / Metadata = Read-only**。他は無し
3. 作成後、**App ID** を控え、**Generate a private key** で `.pem` を落とす
4. **Install App** で danything(と 5ym)にインストール(All repositories)
5. Secret にキー `github-app-private-key`(PEM 全文)を入れ、values に `github.enabled=true`、`github.appId=<App ID>`

インストールトークンは 1 時間で自動更新される。複数の owner にインストールすれば起動時に全部拾う。
PAT で済ませるなら `github.auth=token`、Secret のキー `github-token`、`github.owners` を必須で書く。

## 構成

```
src/Aizuchi.Core/     IChatConnector / ILlmProvider / ITool / IConversation / IReplyDraft と、返信の流れ本体(Bot)、記憶(FileMemoryStore / MemoryTools / MemoryCommand)
src/Aizuchi.Slack/    Slack コネクタ: Socket Mode、Web API、反応判定、履歴→messages、Markdown→mrkdwn
src/Aizuchi.Claude/   Claude プロバイダ: /v1/messages のストリーミング(SSE)とツール呼び出しの往復
src/Aizuchi.GitHub/   GitHub の道具パック: App(JWT → installation token)/ PAT 認証、REST の薄い皮、道具 6 つ
src/Aizuchi/          ホスト。環境変数でコネクタとプロバイダを選び、/healthz /readyz を出す
tests/                純粋関数・JSON 形状・Bot の流れ(偽コネクタ / 偽プロバイダ)のテスト。TUnit(Microsoft.Testing.Platform)
connectors/slack/     Slack アプリのマニフェストと手順
charts/aizuchi/       Deployment + 任意の ExternalSecret
compose.yml           ローカル開発(genkan 経由で https://aizuchi.localhost)。認証情報は compose.override.yml
```

### 増やし方

- **コネクタを足す**(Mattermost、Discord …): `Aizuchi.Core.IChatConnector` を実装するプロジェクトを作り、
  「どの発言に返すか」「履歴をどう `ChatMessage` にするか」「返信をどう書き換えるか(`IReplyDraft`)」をその中に閉じる。
  `src/Aizuchi/Program.cs` の辞書に 1 行、チャートの `deployment.yaml` に Secret のキーを足す
- **プロバイダを足す**(OpenAI、Ollama …): `ILlmProvider.StreamAsync` を実装して増分テキストを `onText` に流す。
  `stop_reason` 相当は `StopKind` に寄せ、`LlmRequest.Tools`(JSON Schema 文字列の `ITool`)の呼び出し往復もプロバイダの中で済ませる。同じく辞書に 1 行
- **道具を足す**(GitHub 以外の情報源): `IToolPack`(説明文 + `ITool` の一覧)を実装して `Program.cs` の packs に足す。
  道具の結果は LLM が読む前提で短い Markdown にし、例外は `ToolResult(IsError: true)` で返して会話を止めない
- 共通ルール: **Native AOT で動くこと**。JSON は `JsonSerializerContext`(ソースジェネレータ)、正規表現は `[GeneratedRegex]`。
  リフレクション前提の SDK は使えない(公式 Anthropic C# SDK が実際にそうで、起動時に落ちる)

## ローカル開発(genkan)

[danything/genkan](https://github.com/danything/genkan) を起動しておくと、`proxy` ネットワーク経由で
https://aizuchi.localhost に振り分けられる(ポートは公開しない)。

トークンと API キーは `compose.override.yml`(git 管理外)に書く。Compose が `compose.yml` に自動で重ねる。

```sh
cp compose.override.example.yml compose.override.yml   # 中身を埋める
docker compose up -d --build
curl -k https://aizuchi.localhost/readyz   # Socket Mode が繋がれば ok
docker compose logs -f
```

コンテナを挟まず直接動かすなら:

```sh
export SLACK_BOT_TOKEN=xoxb-... SLACK_APP_TOKEN=xapp-... ANTHROPIC_API_KEY=sk-ant-...
dotnet run --project src/Aizuchi                 # JIT
dotnet run --project tests/Aizuchi.Tests.csproj  # テスト
dotnet publish src/Aizuchi -c Release -o out && ./out/aizuchi   # AOT(clang が必要)
```

## インストール(Helm / OCI)

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

k3s の helm-controller なら `HelmChart` CR で同じことができる(`valuesContent` に上記 values)。

主な values:

| キー | 既定 | 意味 |
|---|---|---|
| `connector` / `provider` | `slack` / `claude` | 使うコネクタとプロバイダ |
| `bot.systemPrompt` | (空) | 既定のシステムプロンプトへの追記 |
| `bot.maxHistory` | `50` | LLM に渡す直近メッセージ数 |
| `bot.memoryMaxChars` | `8000` | 記憶 1 スコープの上限文字数 |
| `bot.channelContext` | `20` | スレッド返信で参考に渡すチャンネル直近件数。0 で無効 |
| `persistence.enabled` / `size` / `storageClass` | `true` / `1Gi` / 既定 | 記憶を置く PVC(`helm.sh/resource-policy: keep`) |
| `github.enabled` / `auth` / `appId` / `owners` | `false` / `app` / - / - | GitHub を読む道具。Secret のキー `github-app-private-key` か `github-token` |
| `claude.model` | `claude-opus-5` | モデル ID(thinking は adaptive のまま) |
| `claude.maxTokens` | `16000` | 1 応答の上限トークン |
| `claude.effort` | (空 = high) | `low` / `medium` / `high` / `xhigh` / `max` |
| `claude.fallbacks` | `default` | 拒絶時のサーバー側フォールバック(beta)。`off` で無効 |

## 環境変数

| 変数 | 意味 |
|---|---|
| `CHAT_CONNECTOR` / `LLM_PROVIDER` | `slack` / `claude`(既定) |
| `SLACK_BOT_TOKEN` / `SLACK_APP_TOKEN` | Slack コネクタ |
| `ANTHROPIC_API_KEY` `CLAUDE_MODEL` `CLAUDE_MAX_TOKENS` `CLAUDE_EFFORT` `CLAUDE_FALLBACKS` `ANTHROPIC_BASE_URL` | Claude プロバイダ |
| `BOT_SYSTEM_PROMPT` `BOT_MAX_HISTORY` `BOT_UPDATE_INTERVAL_MS` `BOT_MEMORY_DIR` `BOT_MEMORY_MAX_CHARS` `BOT_CHANNEL_CONTEXT` | 共通 |
| `GITHUB_APP_ID` + `GITHUB_APP_PRIVATE_KEY`(PEM)、または `GITHUB_TOKEN` + `GITHUB_OWNERS` | GitHub の道具(任意) |

## ヘルスチェック

- `GET /healthz` — プロセスが生きていれば 200
- `GET /readyz` — コネクタが接続を確立するまで 503
