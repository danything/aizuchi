# 記憶(memory)

毎回スレッドで社内の文脈を説明しなくて済むよう、ボットは **Markdown の記憶**を持つ。
system prompt の末尾に差し込まれ、LLM 自身が道具(`memory_append` / `memory_replace`)で書き換える。

- **共有**(ワークスペース全体)と **チャンネルごと** の 2 スコープ。迷ったら共有に入る
- 「これ覚えといて」「さっきのは間違い、正しくは…」「その項目は忘れて」が普通の会話で通る。保存したら一言添えて返す
- 手動: `@aizuchi memory` で今の中身を表示。`memory`(または `memory channel`)に続けて全文をコードブロックで送ると丸ごと置き換え
- 上限は 1 スコープ 8,000 文字(`bot.memoryMaxChars`)。近づくと LLM が整理して書き直す
- 置き場は `BOT_MEMORY_DIR`(k3s は PVC の `/data/memory`、compose は volume)。`off` で機能ごと無効
- 中身は Slack の全員が読める・書ける前提。個人の秘密は入れない

## 会話に渡す文脈

- スレッドへの返信では、スレッドの外の **チャンネル直近 20 件**(`bot.channelContext`)も「最近の流れ」として渡す
- 複数人のスレッドでは発言に `[名前]` を前置する(Slack の `users:read` スコープが要る。無ければ ID のまま)
- 履歴に `whsec_…` / `xoxb-…` / `sk-ant-…` の形が残っていても、LLM に渡す前に伏せ字にする(`Secrets.Redact`)
