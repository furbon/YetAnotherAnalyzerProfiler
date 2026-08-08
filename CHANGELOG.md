# 変更履歴

このファイルには、YAAPの利用者に影響する変更を記録します。形式は
[Keep a Changelog](https://keepachangelog.com/ja/1.1.0/)を参考にし、バージョンはSemantic Versioningに従います。

## [Unreleased]

### 追加

- 履歴へ任意ラベルを自動保存し、Undo／Redoと比較候補で利用できる機能
- カレンダーと複数の日付表記に対応した履歴期間フィルター
- CLIを.NETグローバルツールとして検証・配布するNuGet packとタグ起点のGitHub Release workflow

### 変更

- Analyzer／Source Generatorの表・ツリーを拘束されたスクロール領域にし、マウスホイール、キー操作、
  表示選択追従、Recycling仮想化を保証
- 履歴検索をデバウンス付きの自動絞り込みにし、更新を小さなアイコンへ移動
- 履歴IDを画面から除き、読込み文言、右クリック削除、設定内の履歴管理へ整理
- 比較対象をラベル、日時、対象、構成から選ぶプルダウンへ変更
- 出力とトラブルシュートを別タブに分け、出力ファイルとbinlogの標準選択ダイアログを追加
- Pull Request検証へ同時実行制御、タイムアウト、NuGet toolのインストールスモークを追加

## [0.1.0] - 2026-08-08

YAAPの最初の公開リリースです。

### 追加

- `.sln`、`.slnx`、`.csproj` を対象とするAnalyzer／Source Generator測定
- .NET 8／10を対象とするクロスプラットフォームCLI
- Windows向けWPF GUIとライト／ダークテーマ
- warm、cold、custom測定モードと反復統計
- コンパイラー報告値に基づくAnalyzer／Generator時間の分離集計
- 生成ファイルの件数、バイト数、行数、相対パスの記録
- ローカル履歴、検索、保持件数、詳細の遅延読込み
- 2履歴の比較と比較可能性の警告
- CSV、JSON、Markdown export
- CLI／GUIでの既存binlog解析、restore切替、履歴日時／件数フィルター、既定有効の分離出力
- 長時間処理のキャンセル、部分結果と安定した診断コード
- 実行中履歴を保護するlease、原子的なエクスポート、CSV式注入対策
- Generatorアセンブリを区別した生成出力集約と、反復ごとの重複メタデータ除去
- 生成出力全件のディスク上NDJSON manifest、各Generator先頭100件の決定的プレビュー、全件export

### セキュリティとプライバシー

- YAAP自身にはテレメトリ、更新確認、外部API通信を実装していません。
- 測定対象のrestore、build、MSBuild task、Analyzer、Source Generatorは通信や副作用を起こせます。
- `--isolated` は出力場所を分けますが、サンドボックスではありません。
- 履歴とbinlogはローカルに保存され、対象由来の機密情報を含む可能性があります。

### 既知の制約

- GUIはWindows専用です。
- CLIの標準出力とGUIのファイル選択／テーマなど、表示媒体に応じた操作差があります。正確な差は
  [機能対応表](docs/index.md#cliとguiの機能対応表)を参照してください。
- Source Generator時間はアセンブリ／型単位です。生成ファイル単位の時間は推定しません。
- 信頼できない測定対象を隔離して実行する機能はありません。

リリース用アーカイブの対応OS、RID、TFM、checksumは、公開時のリリース添付情報を正本とします。
