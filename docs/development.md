# 開発

## 構成

```
src/Aizuchi.Core/        IChatConnector / ILlmProvider / ITool / IWebhookSource など抽象と、返信の流れ本体(Bot)、記憶、webhook 端点
src/Aizuchi.Slack/       Slack コネクタ: Socket Mode、Web API、反応判定、履歴→messages、Markdown→mrkdwn
src/Aizuchi.Claude/      Claude プロバイダ: /v1/messages のストリーミング(SSE)、ツール往復、web_search
src/Aizuchi.GitHub/      GitHub の道具パック: App(JWT → installation token)/ PAT 認証、道具 6 つ
src/Aizuchi.OpenProject/ OpenProject の道具パック: API キーで Basic 認証、道具 6 つ
src/Aizuchi.Anarlog/     Anarlog の webhook 受信: HMAC 検証、再送の重複排除、要約の描画、DM からの鍵登録
src/Aizuchi/             ホスト。環境変数でコネクタ・プロバイダ・道具を組み、/healthz /readyz /webhooks/* を出す
tests/                   純粋関数・JSON 形状・Bot の流れ(偽コネクタ / 偽プロバイダ)。TUnit(Microsoft.Testing.Platform)
connectors/slack/        Slack アプリのマニフェストと手順
charts/aizuchi/          Deployment + PVC + 任意の ExternalSecret / Service / IngressRoute
compose.yml              ローカル開発(genkan 経由で https://aizuchi.localhost)。認証情報は compose.override.yml
```

## 増やし方

- **コネクタを足す**(Mattermost、Discord …): `IChatConnector` を実装するプロジェクトを作り、
  「どの発言に返すか」「履歴をどう `ChatMessage` にするか」「返信をどう書き換えるか(`IReplyDraft`)」をその中に閉じる。
  webhook の投稿先にもするなら `IChannelPoster` も。`src/Aizuchi/Program.cs` の辞書に 1 行、チャートの `deployment.yaml` に Secret のキーを足す
- **プロバイダを足す**(OpenAI、Ollama …): `ILlmProvider.StreamAsync` を実装して増分テキストを `onText` に流す。
  `stop_reason` 相当は `StopKind` に寄せ、`LlmRequest.Tools` の呼び出し往復もプロバイダの中で済ませる。同じく辞書に 1 行
- **道具を足す**: `IToolPack`(説明文 + `ITool` の一覧)を実装して `Program.cs` の packs に足す。
  結果は LLM が読む前提で短い Markdown にし、例外は `ToolResult(IsError: true)` で返して会話を止めない
- **webhook の送り元を足す**: `IWebhookSource` を実装して `/webhooks/{name}` に載せる。署名検証 → 重複排除 → 描画の順。
  LLM には通さない。人が打つ設定コマンドが要るなら `IChatCommand`
- 共通ルール: **Native AOT で動くこと**。JSON は `JsonSerializerContext`(ソースジェネレータ)、正規表現は `[GeneratedRegex]`。
  リフレクション前提の SDK は使えない(公式 Anthropic C# SDK が実際にそうで、起動時に落ちる)

## ローカルで動かす(genkan)

[danything/genkan](https://github.com/danything/genkan) を起動しておくと、`proxy` ネットワーク経由で
https://aizuchi.localhost に振り分けられる(ポートは公開しない)。
トークンと API キーは `compose.override.yml`(git 管理外)に書く。

```sh
cp compose.override.example.yml compose.override.yml   # 中身を埋める
docker compose up -d --build
curl -k https://aizuchi.localhost/readyz   # Socket Mode が繋がれば ok
docker compose logs -f
```

コンテナを挟まず直接:

```sh
export SLACK_BOT_TOKEN=xoxb-... SLACK_APP_TOKEN=xapp-... ANTHROPIC_API_KEY=sk-ant-...
dotnet run --project src/Aizuchi                 # JIT
dotnet run --project tests/Aizuchi.Tests.csproj  # テスト
dotnet publish src/Aizuchi -c Release -o out && ./out/aizuchi   # AOT(clang が必要)
```

## リリース

main に push → GitHub Actions が image `ghcr.io/danything/aizuchi:<sha>` とチャート
`oci://ghcr.io/danything/charts/aizuchi`(version `0.1.<run 番号>`)を出す。Test の CI は Docker を組まないので、
AOT の違反は push 前に `docker build .` で確かめる。
