# webhook を受ける(既定で無効)

SaaS の「Slack 連携」は有料枠になっていることが多い。aizuchi はその置き換え先として、外部サービスの
webhook を受けて Slack のチャンネルに流せる。会話ボットとは独立した機能で、**LLM は通さない**
(定型の描画だけ。外部が作る本文を LLM に渡すとプロンプトインジェクションの入口になるため)。

- `webhooks.enabled=true` と `webhooks.host` で、Service と `/webhooks/` だけを通す IngressRoute が作られる。
  `/healthz` `/readyz` は外に出ない
- 送り元ごとに `IWebhookSource` を 1 つ実装する。**署名を検証してから本文を読む**、通らなければ 401
- 投稿できたら 200、Slack が落ちていれば 503 を返して**送り元の再送に任せる**。こちらでは溜めない
- 本文は 4 MB まで。超えたら 413

## Anarlog

[Anarlog](https://github.com/fastrepl/anarlog)(会議メモ AI)の webhook を受けて、**タイトル + AI 要約 + 未完了の
アクションアイテム**をチャンネルに投稿する。有料の「Share a meeting recap in Slack」と同じ形。

Anarlog の webhook は**デスクトップアプリごとの設定**で、端点を作るたびに別の `whsec_…` が出る。
そのため鍵は Secret に固定せず、各自が Slack から登録する。

管理者(一度だけ):
1. 投稿先チャンネルにボットを招待し、チャンネル ID(`C0…`)を控える
2. values に `webhooks.enabled=true`、`webhooks.host`、`anarlog.enabled=true`、`anarlog.channel` を書いてデプロイ
   (`persistence.enabled` も要る。鍵は記憶と同じ PVC の `/data/anarlog` に置く)

使う人(デスクトップアプリごと):
1. Anarlog の **Settings → Developers → Webhooks** で `https://<host>/webhooks/anarlog` を追加
2. 表示された `whsec_…` を、**aizuchi への DM で** `anarlog add whsec_…` と送る(一度しか表示されない)。
   チャンネルに貼っても受け付けない
3. Anarlog の **Test** を押す。チャンネルに「テスト配信を受け取りました」が出れば完成

`anarlog list` で自分の登録、`anarlog remove <ID>` で削除。これらは `memory` と同じ手動コマンドで **LLM を通らない**。
DM に残った鍵は次の会話で履歴として読まれうるので LLM に渡す前に伏せ字にしているが、登録後は DM のメッセージを消しておくのが安全。

| Anarlog のイベント | 動き |
|---|---|
| `note.enhanced`(AI 要約ができた) | 投稿する。要約が無ければ手書きメモ、それも無ければ何もしない |
| `meeting.completed`(録音が終わった) | 投稿しない(要約がまだ無い。有料版もここでは動かない) |
| `webhook.test` | 経路確認のメッセージを投稿する |

検証は `x-anarlog-signature`(`sha256=` + HMAC-SHA256 の hex、鍵は `whsec_…`、対象は生の本文)を登録された鍵で順に定数時間で比べ、
`x-anarlog-timestamp` が 5 分以上ずれていれば捨てる。失敗時は 5 秒・30 秒後に再送されるので、本文の `id` で重複を弾く。
**配信はデスクトップアプリが開いている間だけ**で、閉じている間の会議は届かない(Anarlog 側の仕様)。
