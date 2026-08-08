# サポート方針

## サポート対象

- CLI: Windows、macOS、Linux
- GUI: Windows WPF
- YAAPの対象フレームワーク: .NET 8、.NET 10
- 測定入力: C#を含む `.sln`、`.slnx`、`.csproj`
- 履歴、比較、CSV／JSON／Markdown export

公開バイナリのOS、RID、TFMは各リリースの添付情報と [CHANGELOG](CHANGELOG.md) を正本とします。
SDK、OS、ランナーの具体的な検証範囲は [テスト方針](docs/testing.md) を参照してください。

## 質問とバグ報告

一般的な質問、再現可能なバグ、機能提案は、このリポジトリを公開しているホスティングサービスの
Issue機能を使用してください。URLが設定されていないローカルコピーでは、リポジトリ管理者が指定した
経路を使用してください。脆弱性や秘密情報はIssueへ書かず、[セキュリティ方針](SECURITY.md)に従います。

報告には次を含めてください。

- `yaap version` の出力
- OS、CPUアーキテクチャ、`dotnet --info`
- CLIかGUIか、対象形式、構成、測定モード、isolated設定
- 期待した動作と実際の動作
- YAAP診断コードと、秘密情報を除去した診断内容
- 最小の再現用プロジェクトを公開できる場合はその内容

binlog、履歴JSON、ログには絶対パス、feed、ユーザー情報、コンパイラー引数などが含まれ得ます。
添付前に必ず確認し、秘密情報、個人情報、組織固有情報を削除してください。

## 対象外または保証しない事項

- 信頼できないリポジトリを安全に実行するサンドボックス
- 対象MSBuild task、Analyzer、Source Generatorの通信や副作用の遮断
- Roslynが報告しない生成ファイル単位の実行時間
- C#コンパイラー呼び出しを含まない対象の測定
- 対象固有のNuGet feed、資格情報プロバイダー、カスタムbuild環境の設定代行
- 対応表にないOS、CPUアーキテクチャ、SDK previewの動作保証

`.slnx`、private feed、isolated出力、診断コードについては [利用ガイド](docs/usage.md) と
[トラブルシュート](docs/troubleshooting.md) も参照してください。

## 互換性と更新

0.xでは、正式版到達に必要な保存schemaやCLIの改善が行われる可能性があります。互換性に影響する変更、
移行方法、既知の制約はCHANGELOGへ記録します。更新前に対象バージョンのリリースノートを確認し、
必要な履歴とexportをバックアップしてください。
