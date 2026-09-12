# aizuchi

チャットで相槌を打つ AI ボット。**コネクタ(Slack …)× プロバイダ(Claude …)× 道具(GitHub、OpenProject …)** を
差し替えられる器で、.NET 10 Native AOT の単一バイナリ。Socket Mode なので公開 URL も Ingress も要らず、
k3s に Helm で置けば動く。

SaaS の有料「Slack 連携」の置き換え先も兼ねる。外部サービスの webhook を受けてチャンネルに流すときだけ `/webhooks/` を Ingress に出す。

```
@aizuchi に質問 / DM で話しかける / @aizuchi で始めたスレッドで続けて話す
  → コネクタが「返すべき発言」だけを拾う(判定・重複排除はコネクタの中)
    → 履歴を user / assistant に組み直して LLM にストリーミングで投げる
      → 仮メッセージを 1.5 秒ごとに書き足し、終わったら整えて確定
```

## できること

| 機能 | 概要 | 詳しく |
|---|---|---|
| Slack コネクタ | Socket Mode。DM / メンション / 自分で始めたスレッドに返す | [connectors/slack](connectors/slack/README.md) |
| 記憶 | 共有 + チャンネル別の Markdown を LLM 自身が書き換える | [docs/memory.md](docs/memory.md) |
| GitHub を読む | 読み取り専用 6 道具。GitHub App か PAT | [docs/github.md](docs/github.md) |
| OpenProject を読む | 読み取り専用 6 道具。スプリント・ベロシティも | [docs/openproject.md](docs/openproject.md) |
| Web 検索 | Anthropic 側の `web_search`。回数上限つき、既定で無効 | [docs/web-search.md](docs/web-search.md) |
| webhook 受信 | 署名検証 → 定型描画 → チャンネル投稿。LLM は通さない。Anarlog 対応 | [docs/webhooks.md](docs/webhooks.md) |

## 使い始める

1. Slack アプリを作る → [connectors/slack](connectors/slack/README.md)
2. Secret `aizuchi` に `slack-bot-token` / `slack-app-token` / `anthropic-api-key` を入れる
3. ```sh
   helm install aizuchi oci://ghcr.io/danything/charts/aizuchi --namespace aizuchi --create-namespace
   ```

values・環境変数の一覧と Infisical / k3s helm-controller の例は [docs/install.md](docs/install.md)、
構成・増やし方・ローカル開発は [docs/development.md](docs/development.md)。

## ライセンス

[AGPL-3.0-or-later](LICENSE)。改変してネットワーク越しに提供する場合も、そのソースを利用者に公開すること。
