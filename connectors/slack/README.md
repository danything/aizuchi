# Slack コネクタ

Socket Mode で動くので公開 URL は要らない。

## アプリを作る

1. https://api.slack.com/apps → **Create New App → From a manifest** → ワークスペースを選ぶ
2. 貼り付け画面は **JSON タブが既定**。[`manifest.json`](manifest.json) をそのまま貼る(YAML タブに切り替えれば YAML でも可)
3. **Basic Information → App-Level Tokens → Generate** で `connections:write` のトークンを作る → `SLACK_APP_TOKEN`(xapp-)
4. **Install App → Install to Workspace** → Bot User OAuth Token → `SLACK_BOT_TOKEN`(xoxb-)
5. 使いたいチャンネルで `/invite @aizuchi`。DM はそのまま話しかければよい

同じアプリを別のワークスペースでも使いたければ、そのワークスペースで 1〜4 を繰り返して aizuchi をもう 1 つ立てる
(Public Distribution にすると OAuth コールバックの実装が必要になるので、自分のワークスペースだけならこちらが早い)。

## スコープの意味

| スコープ | 用途 |
|---|---|
| `app_mentions:read` | `@aizuchi` を受け取る |
| `chat:write` | 返信と途中経過の書き換え |
| `im:history` | DM の本文と履歴 |
| `channels:history` / `groups:history` / `mpim:history` | ボットが参加しているスレッドの追従と履歴 |

## 反応する条件

| 場面 | 反応 |
|---|---|
| DM | 常に返す(トップレベルにはトップレベルで、スレッドにはスレッドで) |
| チャンネルで `@aizuchi` メンション | そのメッセージのスレッドに返す |
| ボットが既に発言しているスレッドの続き | メンション無しでも返す |
| メンション無しのチャンネル発言、編集・削除、他ボット、自分 | 無視 |

`app_mention` と `message` は同じ発言で両方届き、Slack の再送もあるので `channel:ts` で重複排除している。
Markdown → mrkdwn の変換(太字・見出し・リンク・表・箇条書き・エスケープ)もこのコネクタの中。
