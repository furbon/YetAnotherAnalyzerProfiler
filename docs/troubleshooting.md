# トラブルシュート

## YAAP1001: 入力が無効

対象が存在し、拡張子が `.sln`、`.slnx`、`.csproj` のいずれかであることを確認します。
isolated 出力先は対象ディレクトリ外を指定してください。

## YAAP2001: restore／clean／build／profile が失敗

表示された underlying error と同じ作業ディレクトリで `dotnet restore`、`dotnet build` を
実行します。private feed の資格情報プロバイダー、`NuGet.Config`、lock file、選択SDK、構成を
確認してください。YAAP はこれらを置き換えず、通常の dotnet 動作を尊重します。

isolated モードだけ失敗する場合、カスタム target が `bin`／`obj` を固定参照していないか確認し、
対象側を `--artifacts-path` 対応にするか、変更を許容できる環境で通常モードを選択します。

## YAAP3001: binlog／レポートを解析できない

C# コンパイルが実行されたか、利用SDKが `/reportanalyzer` をサポートするか確認します。壊れた
binlog は再測定してください。レポート形式が未知の場合は部分結果と該当行が診断に残ります。

## YAAP5001: キャンセル

キャンセルは子プロセスツリーを停止し、取得済み測定を履歴に保持します。次回の測定は新しい
run IDで開始されます。

## 比較警告

SDK、OS、CPU、構成、TFMが異なる結果は参考値です。同じマシン、SDK、構成、反復条件で再測定
すると、並列 Analyzer によるばらつきを抑えた比較になります。
