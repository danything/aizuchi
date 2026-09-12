# GitHub を読む

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

書き込みは持たない。読める範囲は App のインストール先(または `github.owners`)に閉じ、それ以外の owner は道具側で断る。
API のレート制限(`Retry-After` / `X-RateLimit-Remaining: 0`)は 30 秒以内なら待って 2 回まで再試行し、それ以上はエラーとして LLM に返す。

## GitHub App の作り方(推奨)

1. https://github.com/settings/apps → **New GitHub App**。名前は `aizuchi`、Webhook は無効
2. Repository permissions: **Contents / Issues / Pull requests / Metadata = Read-only**。他は無し
3. 作成後、**App ID** を控え、**Generate a private key** で `.pem` を落とす
4. **Install App** で使う owner にインストール(All repositories)
5. Secret にキー `github-app-private-key`(PEM 全文)を入れ、values に `github.enabled=true`、`github.appId=<App ID>`

インストールトークンは 1 時間で自動更新される。複数の owner にインストールすれば起動時に全部拾う。
PAT で済ませるなら `github.auth=token`、Secret のキー `github-token`、`github.owners` を必須で書く。
