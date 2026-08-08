# 利用ガイド

## 対応環境

CLI は Windows、macOS、Linux の .NET 8／10 で動作します。GUI は Windows の WPF
アプリケーションです。入力は、通常の C# アプリケーションを含む `.sln`、`.slnx`、
`.csproj` です。

.NET 8 SDK は `.slnx` を直接ビルドできないため、YAAP は履歴の作業領域に一時的な互換
`.sln` を生成して同じ C# プロジェクト群を測定します。対象リポジトリには書き込みません。

## CLI

```text
yaap profile <target> [--configuration Release] [--mode warm|cold|custom]
    [--warmups N] [--iterations N] [--clean true|false]
    [--restore true|false] [--isolated] [--artifacts-path PATH]
    [--history PATH] [--retention N]
yaap configurations <target>
yaap history list|show|delete [options]
yaap compare <baseline-id> <candidate-id> [--history PATH]
yaap export <run-id> --format csv|json|markdown --output PATH
yaap analyze <binlog>
yaap version
```

`warm` はウォームアップ1回、測定3回、各測定前の clean が既定です。`cold` は
ウォームアップなし、測定3回、各測定前の clean です。`custom` では回数を指定します。
restore は最初に一度だけ実行され、対象の `NuGet.Config` 階層、認証プロバイダー、
private feed、lock file を通常の `dotnet restore` と同じ方法で利用します。

`--isolated` は restore、clean、build のすべてに .NET の `--artifacts-path` を渡します。
出力先は対象ディレクトリ外でなければなりません。カスタム target が従来の `bin`／`obj`
を固定参照する場合はエラーになり、通常出力へ暗黙に切り替わることはありません。

終了コードは、成功 `0`、使用方法 `2`、失敗 `3`、部分結果 `4`、キャンセル `130` です。

## 履歴と比較

履歴は既定でユーザーのローカルアプリケーションデータ配下に保存されます。`--history`
で変更できます。`history list` は文字列、状態、開始・終了日時で絞り込みでき、詳細は
`history show` のときに遅延読み込みされます。削除には `--force` が必要です。

比較では Analyzer／Generator の増減、追加・削除、生成ファイル数・バイト数の差を表示し、
SDK、OS、CPU、構成、対象フレームワークが異なる場合は比較可能性の警告を出します。

## GUI

GUI では対象選択、構成の自動検出、測定モード、isolated 出力、進捗、キャンセル、履歴検索、
詳細表示、比較、削除、CSV／JSON／Markdown 出力を一画面から操作できます。大量データ用の
行仮想化を有効にしており、ビルドや解析、履歴I/OはUIスレッド外で実行します。

対象欄へ `.sln`、`.slnx`、`.csproj` を1つドロップするか、参照ボタンまたは文字入力で指定
すると、変更後に構成を自動検出します。入力途中の検出は短時間待機し、次の変更があれば古い
検出をキャンセルするため、手動の検出操作は不要です。

Source Generator の表示時間は Generator アセンブリ／型全体の値です。生成ファイルには
件数、サイズ、行数、相対パスだけを表示し、Roslyn が提供しないファイル単位時間は表示しません。
