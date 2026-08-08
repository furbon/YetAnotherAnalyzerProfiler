# YAAPへの貢献

バグ報告、文書改善、テスト追加、実装提案を歓迎します。セキュリティ上の問題は公開Issueへ書かず、
[セキュリティ方針](SECURITY.md)に従ってください。

## 変更を始める前に

1. [行動規範](CODE_OF_CONDUCT.md)を確認します。
2. 既存のIssueと [CHANGELOG](CHANGELOG.md) を確認します。
3. 大きな仕様変更、互換性変更、新しい依存関係は、実装前にIssueで目的と代替案を相談します。
4. リポジトリの `AGENTS.md` と、存在する場合はローカルの `.docs_agent/WORKFLOW.md` を確認します。

ホスティング先のURLがこのリポジトリに設定されていない環境では、架空のIssue／連絡先を推測せず、
リポジトリ管理者が指定した経路を使用してください。

## 開発環境

- .NET 8 SDKまたは.NET 10 SDK
- Git
- GUIのビルド、テスト、目視確認にはWindows

```powershell
./eng/install-git-hooks.ps1
./eng/build.ps1 verify
```

```sh
sh ./eng/build.sh verify
```

詳しくは [開発ガイド](docs/development.md) と [テスト方針](docs/testing.md) を参照してください。

## ブランチと変更範囲

- エージェントによる変更は、利用者が準備した最新の `develop/v...` から `agent/<変更名>` を作成します。
- `main`、`master`、`develop/*` へ直接コミットしません。
- 関係のない整形、生成物、ローカルパス、binlog、履歴、秘密情報を変更へ含めません。
- コミットメッセージはConventional Commitsを使用します。
- pushやmergeはリポジトリ管理者の手順と承認に従います。

## 実装と文書

- `Yaap.Core` と `Yaap.Cli` はクロスプラットフォームを維持し、WPF依存は `Yaap.Gui` に限定します。
- .NET 8と.NET 10の両方を維持します。
- ビルド、解析、履歴、比較、exportは非同期・キャンセル可能にします。
- 大きなbinlogや生成出力を全件メモリへ読み込みません。
- 計測仕様、CLI help、GUI、README、利用ガイド、CHANGELOGを同じ変更で同期します。
- 人が読む文書とGUI文言は日本語、コード識別子とエージェント指示は英語を使用します。
- ソース形式はリポジトリの `.editorconfig` と `AGENTS.md` に従います。

新しいNuGetパッケージを提案する場合は、提供元、保守状況、利用実績、ライセンス、既知の脆弱性、
推移依存、対象フレームワーク、代替案を作業計画へ記録してください。バージョンは
`Directory.Packages.props` で一元管理します。

## テスト

変更に比例した回帰テストを追加してください。正常系だけでなく、入力不正、失敗、部分結果、
キャンセル、cleanup、大規模入力、対象OS／TFMを確認します。GUI変更ではWindows上で
`Yaap.Gui.Tests`、STA起動smoke、影響する全状態のライト／ダーク表示確認が必要です。

提出前に次を確認します。

```powershell
./eng/build.ps1 verify
dotnet format --verify-no-changes
git diff --check
git status --short
```

macOS／Linuxでは `./eng/build.sh verify` を使用します。

## レビュー時に含める情報

- 変更の目的と利用者への影響
- 互換性、データ形式、セキュリティ、性能への影響
- 実行したテストと結果
- GUI変更時のライト／ダーク表示確認
- 残る制約がある場合は、その理由と文書化場所
