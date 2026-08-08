# 測定設計

既定は Release、restore 1回、ウォームアップ1回、測定3回、各測定前の clean です。Analyzer の
並列実行は無効化せず、平均・最小・最大・標準偏差と環境情報を保存します。cold はウォームアップを
省略し、custom は回数と clean 方針を指定できます。

ビルド経過時間は通常の測定 build の時間です。Analyzer／Source Generator 時間は、その build を
実行するMSBuild内のストリーミングLoggerが記録した同一 C# コンパイラ呼び出しを再実行し、Roslyn
自身が報告した値です。再実行の所要時間を build 時間に加算しません。Loggerは生成側SDKで動くため、
YAAP本体と対象SDKのbinlogリーダー世代が異なっても測定できます。

Source Generator の時間はアセンブリ／型単位です。生成ファイルの件数、バイト数、行数、一覧は
別指標です。ファイル単位の時間配分、推定、按分は行いません。
