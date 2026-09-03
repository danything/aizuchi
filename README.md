# aizuchi

チャットで相槌を打つ AI ボット。**コネクタ(Slack …)× プロバイダ(Claude …)** を差し替えられる器で、
.NET 10 Native AOT の単一バイナリ。Socket Mode なので公開 URL も Ingress も要らず、k3s に Helm で置けば動く。

```
@aizuchi に質問 / DM で話しかける / ボットが居るスレッドで続けて話す
  → コネクタが「返すべき発言」だけを拾う(判定・重複排除はコネクタの中)
    → 履歴を user / assistant に組み直して LLM にストリーミングで投げる
      → 仮メッセージを 1.5 秒ごとに書き足し、終わったら整えて確定
```

| 種別 | 実装済み | 設定 |
|---|---|---|
| コネクタ | `slack`([connectors/slack](connectors/slack/README.md)) | `CHAT_CONNECTOR` / values `connector` |
| プロバイダ | `claude`(Claude Console の API キー) | `LLM_PROVIDER` / values `provider` |

## 構成

```
src/Aizuchi.Core/     IChatConnector / ILlmProvider / IConversation / IReplyDraft と、返信の流れ本体(Bot)
src/Aizuchi.Slack/    Slack コネクタ: Socket Mode、Web API、反応判定、履歴→messages、Markdown→mrkdwn
src/Aizuchi.Claude/   Claude プロバイダ: /v1/messages のストリーミング(SSE)
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
  `stop_reason` 相当は `StopKind` に寄せる。同じく辞書に 1 行
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
| `BOT_SYSTEM_PROMPT` `BOT_MAX_HISTORY` `BOT_UPDATE_INTERVAL_MS` | 共通 |

## ヘルスチェック

- `GET /healthz` — プロセスが生きていれば 200
- `GET /readyz` — コネクタが接続を確立するまで 503
