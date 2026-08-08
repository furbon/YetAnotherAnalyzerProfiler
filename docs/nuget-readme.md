# YetAnotherAnalyzerProfiler CLI

YAAP は、C# ビルドに含まれる Roslyn Analyzer と Source Generator の実行時間を、コンパイラー報告値から測定するローカル実行型ツールです。

## 起動

```powershell
dotnet tool install --global YetAnotherAnalyzerProfiler.Tool
yaap profile path/to/App.slnx
yaap history list
yaap help
```

.NET 8 または .NET 10 が必要です。コマンドごとの説明は `yaap <command> --help` で確認できます。

## 安全上の注意

YAAP はサンドボックスではありません。対象の restore、clean、build、MSBuild task、Analyzer、Source Generator を利用者と同じ権限で実行します。信頼できない対象を測定しないでください。分離出力は標準の `bin`／`obj` を別の場所へ移す機能であり、ファイル操作や通信を制限しません。

YAAP 自身はテレメトリや更新確認を行いません。ただし、測定対象は通常の restore や独自実装を通じて通信できます。履歴、binlog、export には絶対パス、ログ、対象由来の機密情報が含まれ得るため、共有前に確認してください。

## 測定値

Analyzer／Source Generator の時間はコンパイラーが報告したアセンブリ／型単位の値です。生成ファイル単位の実行時間は推定しません。失敗した測定は診断として保存し、成功サンプルの統計へ混ぜません。

プロジェクト URL、ソース、Issue、セキュリティ報告先は、公開パッケージの NuGet メタデータから同じバージョンのホスティング先を開いて確認してください。
