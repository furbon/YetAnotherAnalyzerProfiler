# 変更履歴

このファイルには、YAAPの利用者に影響する変更を記録します。形式は
[Keep a Changelog](https://keepachangelog.com/ja/1.1.0/)を参考にし、バージョンはSemantic Versioningに従います。

## [0.1.0] - 未公開

YAAPの最初の公開候補です。公開タグ、NuGetパッケージ、リリース添付物が揃うまではリリース済みとして扱いません。

### 主な機能

- `.sln`、`.slnx`、`.csproj`を対象としたAnalyzer／Source Generator測定と、既存binlogの解析
- .NET 8／10対応のクロスプラットフォームCLI、.NETグローバルツール配布、ライト／ダークテーマを備えたWindows向けWPF GUI
- warm、cold、custom測定モード、反復統計、restore切替、Analyzer／Generator別のコンパイラー報告時間
- 生成ファイルの件数、バイト数、行数、相対パス、Generatorのアセンブリ／型を含むディスク上manifestとプレビュー
- ラベル、検索、期間フィルター、保持件数を備えたローカル履歴と、2履歴の比較および比較可能性の警告
- CSV、JSON、Markdownへの結果出力と、生成出力全件のexport
- 長時間処理のキャンセル、失敗／部分結果の保存、安定した診断コード

### セキュリティとプライバシー

- YAAP自身にはテレメトリ、更新確認、外部API通信を実装していません。
- 測定対象のrestore、build、MSBuild task、Analyzer、Source Generatorは通信や副作用を起こせます。
- `--isolated`は出力場所を分けますが、サンドボックスではありません。
- 履歴とbinlogはローカルに保存され、対象由来の機密情報を含む可能性があります。

### 既知の制約

- GUIはWindows専用です。
- CLIの標準出力とGUIのファイル選択／テーマなど、表示媒体に応じた操作差があります。正確な差は
  [機能対応表](docs/index.md#cliとguiの機能対応表)を参照してください。
- Source Generator時間はアセンブリ／型単位です。生成ファイル単位の時間は推定しません。
- 信頼できない測定対象を隔離して実行する機能はありません。

リリース用アーカイブの対応OS、RID、TFM、checksumは、公開時のリリース添付情報を正本とします。
