# テスト方針

`tests/Yaap.Tests` は外部テストフレームワークに依存しない実行型ハーネスで、統計、履歴、検索、
保持、削除、比較、出力、入力検出、isolated 出力、失敗、部分結果、キャンセル、CLI、10万件集計を
検証します。`tests/Yaap.Gui.Tests` は WPF 初期化、D&D、自動構成検出の競合とキャンセル、構成の
履歴／Release／Debug／アルファベット順選択、空欄・未知構成の開始拒否、実行中を含む状態説明、
WPF UIテーマ辞書とFluentWindowの統合、restoreを含む詳細設定、履歴の自動検索、ファジー日付、ラベルの
自動保存とUndo／Redo、人間可読な比較選択、既存binlog解析、非同期コマンド、表示上のSource Generator制約、
生成出力プレビューを切り詰めた場合の全件export案内を検証します。600件のAnalyzerと240件のGeneratorを
実ウィンドウへ描画し、拘束されたビューポート、スクロール範囲、表示バー、マウスホイール、選択追従、
全件より十分少ない実体化行・ツリーノード、つまみやすいスクロールバーの操作領域と表示サイズまで
確認します。履歴読込み中も一覧の高さ、選択、スクロール位置が変わらないこと、不透明なカレンダーの
月／年／年代表示、期間クリア、最小ウィンドウ幅、結果再読込み後のスクロールバーについても、
ライト／ダーク両テーマの実ウィンドウで文字コントラストと操作領域を検証します。カレンダーの前後移動と
期間クリアは通常、ホバー、押下、キーボードフォーカス、無効の各状態を描画し、アイコンのコントラスト、
中央配置、32px以上の操作領域、アクセシビリティ名を確認します。

生成出力manifestの回帰試験では、各Generatorの決定的な先頭100件、全件の集計値、
`OutputsTruncated`、NDJSONの全件ストリーム、読込みキャンセル、CSV／JSON／Markdownへの全件exportを
検証します。

`tests/assets` の Analyzer／Source Generator fixture を実際に restore／build し、binlog 解析、
コンパイラレポート、生成ファイルを通した統合回帰を実行します。`tests/local-feed` はパッケージを
ローカルで生成し、`NuGet.Config` の `<clear />`、private-feed相当の相対 source、locked restore、
完全オフライン restore を検証します。これらの hermetic fixture がCIの正本です。

.NET 10 SDKの検証レーンでは、YAAPのテスト実行体を `net8.0` として起動した統合試験も追加で
実行します。これにより、新しいSDKが生成するbinlogを古いYAAPランタイムが直接読めない組合せでも、
SDK側Loggerを使った測定が完了することを回帰検証します。

GitHub Actions と GitLab CI は .NET 8／10、Windows、Linux、macOSを対象に同じ `eng` ハーネスを
呼び出します。WPFはWindowsだけで、CLIの self-contained single-file 配布は対象OSで検証します。
WindowsではGUIテストをDebugで両TFMに対して実行し、STAで `MainWindow` を生成、表示、レイアウト、
終了します。GUIを変更した場合は `YAAP_GUI_CAPTURE_DIR` に出力したライト／ダーク両テーマの全タブと
影響状態を目視確認します。publish検証は成果物の厳密な許可リスト、同梱文書、CLIのversion／help、
Windows GUIの起動と終了まで確認します。
NuGet tool packは両TFMの実行物、BuildLogger、README、第三者通知を検査し、ローカルfeedから実際に
インストールして `version`／`help` を確認します。release workflowはタグと `eng/Version.props` の不一致を
公開前に拒否します。

`verify` はlocked restoreに先立って通常の強制restoreも実行し、すべての `packages.lock.json` のSHA-256が
復元前後で一致することを確認します。lock fileはNuGet自身の出力に合わせたUTF-8（BOMなし）／CRLFを
正本とし、CLIではパッケージIDとアセンブリIDを一致させつつ、配布コマンドと出力名の `yaap` を維持します。
