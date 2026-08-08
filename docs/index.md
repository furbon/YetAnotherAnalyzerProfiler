# YAAP ドキュメントガイド

このページは、YAAPの利用、運用、開発に関する文書の入口です。実装とCLI helpを製品仕様の正本とし、
文書はリリースごとに同じ内容へ更新します。

## 目的別の入口

### YAAPを使う

1. [README](../README.md) で安全上の注意と起動方法を確認します。
2. [利用ガイド](usage.md) でCLI／GUIの操作を確認します。
3. [測定設計](measurement.md) で測定値の意味と比較条件を確認します。
4. 問題が起きた場合は [トラブルシュート](troubleshooting.md) を参照します。

### YAAPを運用する

- [セキュリティ方針](../SECURITY.md) — 信頼境界、履歴／binlogの取扱い、脆弱性報告
- [サポート方針](../SUPPORT.md) — 対応範囲、問い合わせ時に必要な情報
- [変更履歴](../CHANGELOG.md) — リリース内容、互換性、既知の制約
- [公開チェックリスト](release-checklist.md) — 公開ゲート、由来証明、復旧

### YAAPを開発する

- [設計](architecture.md) — コンポーネント、測定パイプライン、データ保存
- [開発ガイド](development.md) — SDK、共通コマンド、リポジトリ規約
- [テスト方針](testing.md) — 検証範囲とCI
- [DeepReview](deep-review.md) — 明示起動専用の複数観点・敵対的リポジトリ総合レビュー
- [貢献ガイド](../CONTRIBUTING.md) — 変更提案、実装、レビュー
- [行動規範](../CODE_OF_CONDUCT.md)

## 対応環境

| 項目 | 対応範囲 |
| --- | --- |
| CLI | Windows、macOS、Linux |
| GUI | Windows WPF |
| ソースの対象フレームワーク | .NET 8、.NET 10 |
| 測定対象 | C#を含む `.sln`、`.slnx`、`.csproj` |
| リリースバイナリ | リリースごとの添付ファイルとCHANGELOGを正本とする |

## CLIとGUIの機能対応表

v0.1.0の実装上の差です。「非対応」は、同じCoreに機能があってもその画面から操作できないことを表します。

| 機能 | CLI | GUI |
| --- | --- | --- |
| 対象の測定 | `profile` | 対応 |
| 構成の検出 | `configurations`で明示実行 | 対象入力後に自動検出 |
| warm／cold／custom | 対応 | 対応 |
| warmup／測定回数／clean | オプションで指定 | 詳細設定で指定 |
| restoreの切替 | `--restore`／`--no-restore` | 詳細設定で指定 |
| 分離出力 | 対応。既定は有効。`--no-isolated`で無効化 | 対応。既定は有効 |
| 明示的なartifacts path | `--artifacts-path` | 詳細設定で指定 |
| 実行中処理のキャンセル | Ctrl+C | 処理中のキャンセルボタン |
| 履歴の文字列／状態検索 | 対応 | 対応 |
| 履歴の日時／件数制限 | `--from`、`--to`、`--limit` | 日付は履歴タブ、表示上限は設定タブで指定 |
| 履歴結果の読込み | `history show` | ダブルクリックまたは「読み込み」 |
| 履歴ラベル | 対象外 | 自動保存、Undo／Redo対応 |
| 履歴削除 | `--force`が必須 | 右クリックで選択削除、設定で全削除 |
| 2履歴の比較 | IDを指定 | ラベル、日時、対象、構成から選択 |
| CSV／JSON／Markdown出力 | 対応 | 対応 |
| profile結果全体のJSON | `--json`で標準出力 | 出力タブでJSONファイルへ保存 |
| 既存binlog単体解析 | `analyze` | トラブルシュートタブで解析 |
| ライト／ダークテーマ | 対象外 | 対応 |

GUIのキャンセルボタンは、測定、履歴I/O、比較、exportのうち現在実行している処理へキャンセルを
要求します。保存済み部分結果や、キャンセル時の出力ファイルの扱いは各処理の診断を確認してください。

## 主な既定値

| 設定 | CLI | GUI |
| --- | --- | --- |
| 測定モード | warm | warm |
| warmup回数 | 1 | 1 |
| 測定回数 | 3 | 3 |
| 各測定前のclean | 有効 | 有効 |
| restore | 有効 | 有効 |
| 分離出力 | 有効 | 有効 |
| 履歴保持件数 | 50 | 50 |
| 履歴表示上限 | `--limit`未指定時は無制限 | 500 |
| 構成 | Release | 最新の同一対象履歴、Release、Debug、名前順の順で自動選択 |

## 配布ディレクトリ

`eng`ハーネスでpublishした場合の主要ファイル例です。実際には自己完結実行に必要なruntimeファイルと
`docs/`も含まれ、許可リストとMarkdownリンク検査が正本です。

```text
artifacts/publish/<RID>/<TFM>/
├── cli/
│   ├── yaap または yaap.exe
│   ├── Yaap.BuildLogger.dll
│   ├── Yaap.Core.xml
│   └── LICENSE、README.md、CHANGELOG.md、THIRD-PARTY-NOTICES.txt
└── gui/                    # Windowsのみ
    ├── yaap-gui.exe
    ├── Yaap.BuildLogger.dll
    ├── Yaap.Core.xml
    └── LICENSE、README.md、CHANGELOG.md、THIRD-PARTY-NOTICES.txt
```

publishハーネスは許可リスト外のファイルが混入した場合に失敗します。`Yaap.BuildLogger.dll` を実行ファイル
から分離すると測定できません。

## 履歴と機密情報

履歴の既定場所は .NET が返すユーザーのローカルアプリケーションデータ配下の `YAAP` です。
`--history`、GUIの履歴場所、または `YAAP_HISTORY_PATH` 環境変数で変更できます。

履歴には絶対パス、Git commit／branch／dirty状態、SDK／OS情報、診断、失敗またはキャンセルした子プロセスの完全ログ、
測定ごとのbinlog、生成ファイルの相対パスが含まれ得ます。対象のbinlogやログには秘密情報が含まれる可能性があるため、
Issue、チャット、公開ストレージへ添付する前に確認・秘匿化してください。

## 文書の整合性

CLIオプションを変更する場合は `yaap help`、[利用ガイド](usage.md)、この機能対応表を同時に更新します。
測定方式や保存形式を変更する場合は [測定設計](measurement.md)、[設計](architecture.md)、CHANGELOGを
同時に更新します。サポート範囲や安全上の前提を変更する場合はREADME、SECURITY、SUPPORTを更新します。
