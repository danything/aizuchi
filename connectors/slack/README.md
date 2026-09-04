# Slack コネクタ

Socket Mode で動くので公開 URL は要らない。

## アプリを作る

1. https://api.slack.com/apps → **Create New App → From a manifest** → ワークスペースを選ぶ
2. 貼り付け画面は **JSON タブが既定**。[`manifest.json`](manifest.json) をそのまま貼る(YAML タブに切り替えれば YAML でも可)
3. **Basic Information → App-Level Tokens → Generate** で `connections:write` のトークンを作る → `SLACK_APP_TOKEN`(xapp-)
4. **Install App → Install to Workspace** → Bot User OAuth Token → `SLACK_BOT_TOKEN`(xoxb-)
5. **Basic Information → Display Information → App icon** に [`icon.png`](icon.png)(1024×1024)を上げる。マニフェストではアイコンを指定できないので手動
6. 使いたいチャンネルで `/invite @aizuchi`。DM はそのまま話しかければよい

同じアプリを別のワークスペースでも使いたければ、そのワークスペースで 1〜4 を繰り返して aizuchi をもう 1 つ立てる
(Public Distribution にすると OAuth コールバックの実装が必要になるので、自分のワークスペースだけならこちらが早い)。

## スコープの意味

| スコープ | 用途 |
|---|---|
| `app_mentions:read` | `@aizuchi` を受け取る |
| `chat:write` | 返信と途中経過の書き換え |
| `im:history` | DM の本文と履歴 |
| `channels:history` / `groups:history` / `mpim:history` | ボットが参加しているスレッドの追従と履歴、チャンネル直近の流れ |
| `users:read` | 複数人のスレッドで発言者名を引く(無くても動く。ID のままになるだけ) |

スコープを後から足したときは **Install App → Reinstall to Workspace** が要る(トークンは変わらない)。
`users:read` が無いまま動かすと発言者名が `U0123ABCD` のまま LLM に渡り、返信でもその ID で呼ばれる。
起動後に一度だけ `発言者名を引けません(users.info: missing_scope)` と警告が出るので、それが目印。

## 反応する条件

| 場面 | 反応 |
|---|---|
| DM | 常に返す(トップレベルにはトップレベルで、スレッドにはスレッドで) |
| チャンネルで `@aizuchi` メンション | そのメッセージのスレッドに返す |
| `@aizuchi` で始まったスレッドの続き | メンション無しでも返す |
| 人同士のスレッドに途中から呼ばれたあとの続き | 無視(そのつど `@aizuchi` が要る) |
| メンション無しのチャンネル発言、編集・削除、他ボット、自分 | 無視 |

スレッドの続きに返すかは**親の発言がボットを呼んでいるか**で決める。
「aizuchi と話すために立てたスレッド」なら会話が続けられて、人同士の相談に呼ばれただけのときは割り込まない。
`SLACK_THREAD_FOLLOWUP=off`(values `slack.threadFollowUp`)にすると追従ごと切れて、いつでもメンションが要る。
今どちらかは起動ログの `thread_followup=` で分かる。

`app_mention` と `message` は同じ発言で両方届き、Slack の再送もあるので `channel:ts` で重複排除している。
Markdown → mrkdwn の変換(太字・見出し・リンク・表・箇条書き・エスケープ)もこのコネクタの中。
