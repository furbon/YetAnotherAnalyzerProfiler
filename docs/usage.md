# 利用ガイド

> [!WARNING]
> YAAP はサンドボックスではありません。対象のMSBuild task、Analyzer、Source Generatorを実行し、
> 測定レポート取得のためコンパイラー呼び出しも再実行します。信頼できない対象を測定しないでください。
> `--isolated` は標準出力場所を分けますが、任意の書込みや通信を防止しません。詳しくは
> [セキュリティ方針](../SECURITY.md#測定対象との信頼境界)を参照してください。

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
    [--restore true|false] [--isolated|--no-isolated] [--artifacts-path PATH]
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
restore が有効な場合は最初に一度だけ実行され、対象の `NuGet.Config` 階層、認証プロバイダー、
private feed、lock file を通常の `dotnet restore` と同じ方法で利用します。

分離出力は既定で有効です。`--isolated` は明示的に有効化し、`--no-isolated` は無効化します。
有効な場合は restore、clean、build のすべてに .NET の `--artifacts-path` を渡します。
出力先は対象ディレクトリ外でなければなりません。カスタム target が従来の `bin`／`obj`
を固定参照する場合はエラーになり、通常出力へ暗黙に切り替わることはありません。

終了コードは、成功 `0`、使用方法 `2`、失敗 `3`、部分結果 `4`、キャンセル `130` です。

## 履歴と比較

履歴は既定でユーザーのローカルアプリケーションデータ配下に保存されます。`--history`
で変更できます。`history list` は文字列、状態、開始・終了日時で絞り込みでき、詳細は
`history show` のときに遅延読み込みされます。削除には `--force` が必要です。

比較では Analyzer／Generator の増減、追加・削除、生成ファイル数・バイト数の差を表示し、
SDK、OS、CPU、構成、対象フレームワークが異なる場合は比較可能性の警告を出します。

保存済み履歴のCSV／JSON／Markdown exportは、ディスク上の生成出力manifestを逐次読み、生成ファイルを
全件出力します。メモリ上のrunデータに含まれる生成ファイル一覧は各Generatorの決定的な先頭100件に
制限されますが、ファイル数、バイト数、行数の集計値は常に全件を表します。

## GUI

GUI では対象選択、構成の自動検出、測定モード、isolated 出力、進捗、キャンセル、履歴検索、
結果の読込み、比較、削除、CSV／JSON／Markdown 出力を操作できます。大量データ用の行・列・ツリー
仮想化とRecyclingを有効にし、表とツリーはマウスホイール、スクロールバー、方向キー、Page Up／Downで
移動できます。ビルドや解析、履歴I/OはUIスレッド外で実行します。

対象欄へ `.sln`、`.slnx`、`.csproj` を1つドロップするか、参照ボタンまたは文字入力で指定
すると、変更後に構成を自動検出します。入力途中の検出は短時間待機し、次の変更があれば古い
検出をキャンセルするため、手動の検出操作は不要です。

構成は、同じ対象の最新履歴で使った構成が現在も存在すればそれを選びます。該当する履歴が
なければ `Release`、`Debug`、その他の構成名のアルファベット順で自動選択します。空欄や検出一覧に
ない構成では測定を開始できません。画面下部には「測定可能」、準備に必要な操作、または「測定中」と、
検出・測定処理の進捗を常に表示します。

外観はWPF UIのFluentテーマを使用し、起動時とWindows側の変更時にシステムテーマへ自動追従します。
上部のテーマ選択で「自動」、「ライト」、「ダーク」をいつでも切り替えられ、ウィンドウ全体と
ポップアップを含むコントロールへ一貫して反映されます。履歴場所、保持件数、restore、clean、分離出力、
分離出力先は「詳細設定」を展開した場合だけ表示します。

Source Generator の表示時間は Generator アセンブリ／型全体の値です。生成ファイルには
件数、サイズ、行数、相対パスだけを表示し、Roslyn が提供しないファイル単位時間は表示しません。
各Generatorの生成ファイル一覧は先頭100件のプレビューです。全件がある場合はその旨を行詳細に表示し、
全件はCSV／JSON／Markdown exportで確認できます。

GUIの履歴タブは、検索文字や状態を変更すると短い待機後に自動で絞り込みます。開始日と終了日は
カレンダーから選択でき、`2026/01/31`、`2026-01-31`、`31/Jan/2026` などの入力も受け付けます。
履歴をダブルクリックするか「読み込み」を選ぶと結果を表示し、削除は右クリックメニューから行います。
任意のラベルは入力後に自動保存され、Ctrl+Z／Ctrl+Yで元に戻す／やり直すことができます。内部IDは
画面に表示せず、比較タブではラベル、日時、対象、構成で2つの履歴を選択します。

履歴一覧の表示上限は設定タブにあり、1～10000件、既定500件です。同じ場所から履歴フォルダーを開くか、
確認後にすべての履歴を削除できます。「出力」タブは拡張子を含む保存ファイルを標準ダイアログで選びます。
既存binlog解析と診断一覧は独立した「トラブルシュート」タブにあります。
GUIのキャンセルボタンは、測定、履歴I/O、binlog解析、比較、exportのうち実行中の処理を停止できます。
CLIとGUIの表示媒体に応じた操作差は [機能対応表](index.md#cliとguiの機能対応表)を参照してください。
