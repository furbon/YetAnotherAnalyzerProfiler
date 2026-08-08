# テスト方針

`tests/Yaap.Tests` は外部テストフレームワークに依存しない実行型ハーネスで、統計、履歴、検索、
保持、削除、比較、出力、入力検出、isolated 出力、失敗、部分結果、キャンセル、CLI、10万件集計を
検証します。`tests/Yaap.Gui.Tests` は WPF 初期化、非同期コマンド、キャンセル、仮想化、表示上の
Source Generator 制約を検証します。

`tests/assets` の Analyzer／Source Generator fixture を実際に restore／build し、binlog 解析、
コンパイラレポート、生成ファイルを通した統合回帰を実行します。`tests/local-feed` はパッケージを
ローカルで生成し、`NuGet.Config` の `<clear />`、private-feed相当の相対 source、locked restore、
完全オフライン restore を検証します。これらの hermetic fixture がCIの正本です。

GitHub Actions と GitLab CI は .NET 8／10、Windows、Linux、macOSを対象に同じ `eng` ハーネスを
呼び出します。WPFはWindowsだけで、CLIの self-contained single-file 配布は対象OSで検証します。
