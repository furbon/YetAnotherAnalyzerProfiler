# YetAnotherAnalyzerProfiler (YAAP)

YAAP は、C# ビルドに含まれる Roslyn Analyzer と Source Generator のコストを
実測し、履歴・比較・エクスポートを提供するオフライン優先のプロファイラーです。

- Windows、macOS、Linux で利用できる非対話 CLI
- Windows 向けの非同期・キャンセル可能な WPF GUI
- `.sln`、`.slnx`、`.csproj` と .NET 8／10 SDK に対応
- Analyzer と Source Generator の時間を分離して集計
- 生成ファイルは件数、サイズ、行数、一覧を記録（ファイル単位の時間は推測しません）
- ローカル履歴、検索、比較、CSV／JSON／Markdown 出力

## クイックスタート

```powershell
dotnet run --project src/Yaap.Cli --framework net10.0 -- profile path/to/App.slnx
dotnet run --project src/Yaap.Cli --framework net10.0 -- history list
```

対象リポジトリを変更したくない場合は `--isolated` を指定します。通常モードは
対象の `bin`／`obj` を clean・build します。YAAP 自身は通信しませんが、対象の
`dotnet restore` はそのリポジトリの NuGet 設定に従って通信する場合があります。

詳しい使い方は [利用ガイド](docs/usage.md)、開発方法は
[開発ガイド](docs/development.md)、測定上の制約は
[測定設計](docs/measurement.md) を参照してください。

## ライセンス

[MIT License](LICENSE)
