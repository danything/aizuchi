# OpenProject を読む

`openproject.enabled=true` と `openproject.url` を設定すると、LLM が **読み取り専用**で作業パッケージを調べられる。
セルフホスト前提なので URL は必ず指定する(SaaS 版でも URL を書けば同じ)。

| 道具 | 中身 |
|---|---|
| `openproject_projects` | プロジェクト一覧。識別子の揺れはまずこれで確かめる |
| `openproject_search` | 作業パッケージの横断検索(既定は未完了のみ) |
| `openproject_list` | プロジェクト内の一覧(更新の新しい順。スプリントで絞れる) |
| `openproject_get` | 1 件の詳細と、人が書いたコメント |
| `openproject_sprints` | スプリント一覧。API の返しと、実際の割当を並べて出す |
| `openproject_velocity` | スプリント別ベロシティ(クローズ分の `storyPoints` 合計) |

作成も更新もできない。読める範囲は API キーを作ったユーザーの権限に閉じるので、
**ボット用のユーザーを作って必要なプロジェクトだけ見せる**のが安全。

## API キーの作り方

1. ボット用ユーザーで OpenProject にログイン
2. **マイアカウント → アクセストークン → API** で生成
3. Secret にキー `openproject-api-key` を入れ、values に `openproject.enabled=true` と `openproject.url` を書く

認証は Basic(ユーザー名 `apikey` 固定、パスワードが API キー)。起動時に `/api/v3/users/me` を引いて通らなければ落とす。

## ストーリーポイントとスプリント(API の癖)

- `storyPoints` は Backlogs(Scrum)の core 属性。返るのは **Backlogs モジュールが有効**で、かつそのタイプが
  管理画面の **Story types** に入っているときだけ(タスクタイプは `remainingTime` 側で `null`)
- スプリントはバージョンではなく `/api/v3/projects/{id}/sprints`。進行中は `_links.status.href` の末尾 `:active`。
  作業パッケージの絞り込みは `filters=[{"sprint":{"operator":"=","values":["<id>"]}}]`
- **ページングの `offset` はページ番号(1 始まり)**で、件数のオフセットではない
- **`/projects/{id}/sprints` は実際の割当と一致しない。** 他プロジェクト定義のスプリントが混ざる一方、実際に
  割り当てられているスプリントが出てこないことがある。`openproject_velocity` はこの一覧を使わず、クローズ扱いの
  作業パッケージを全部引いて `_links.sprint` で束ねる。`openproject_sprints` は両方を並べるので食い違いはそこで分かる
- ベロシティは「スプリント別に、クローズ扱いの `storyPoints` を合計」= バーンダウンと同じ数え方。
  **スプリント単位が正で、週あたりは換算値**。完了日が API から取れないため、日付で切る集計は `updatedAt` 代用で誤差が出る
