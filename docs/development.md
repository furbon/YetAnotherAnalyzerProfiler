# 開発ガイド

## 必要なもの

- .NET 8 SDK または .NET 10 SDK
- WPF／GUIのビルドと実行には Windows
- Git（製品の実行自体には必須ではありません）

`global.json` は .NET 8 以上の最新メジャーを選択します。Core と CLI は `net8.0;net10.0`、
GUI は `net8.0-windows;net10.0-windows` を対象にします。バージョンは
`eng/Version.props` だけを変更し、アセンブリ／ファイル版の4番目は常に0です。

## 共通コマンド

```powershell
./eng/build.ps1 verify
./eng/build.ps1 pack --framework net10.0
./eng/build.ps1 publish --runtime win-x64 --framework net10.0
```

```sh
./eng/build.sh verify
./eng/build.sh pack --framework net10.0
./eng/build.sh publish --runtime linux-x64 --framework net10.0
```

`verify` はリポジトリガード、locked restore、format、警告をエラーにした build、Core／CLI／
GUIテスト、実 Analyzer／Generator 統合テスト、ローカルNuGet feed／lock file試験を実行します。
GUIテストは Windows だけでDebug、両対象フレームワークに対して実行され、STA起動スモークを含みます。
`pack` はCLIを `YetAnotherAnalyzerProfiler.Tool` という.NETツールにし、パッケージ内容を許可リストで
検査した後、一時tool-pathへインストールして `version` と `help` を実行します。`net10.0` の `verify`
にもこの検証が含まれます。
`publish` はRID専用の一時lock fileで復元した後にlocked publishを行い、成果物の許可リスト、同梱する
ライセンス／通知、CLIの起動を検証します。Windows向けGUIは実際にウィンドウを開いて終了まで確認します。

新しい NuGet パッケージは中央管理し、提供元、保守状況、利用実績、ライセンス、脆弱性、
推移依存、対象フレームワーク、代替案を作業計画に記録してください。製品依存は最小限にします。
production lock fileに現れる全パッケージと版は `THIRD-PARTY-NOTICES.txt` に記載し、共通ガードで
同期を検証します。

## GitHubリリース

Pull RequestではGitHub Actionsの全OS／両TFM検証が完了しない限り、branch protectionでマージを許可しない
運用を前提にします。同じブランチやPRへ追加pushした場合は古い実行をキャンセルし、各jobにはタイムアウトを
設定しています。

正式リリースは、まず `eng/Version.props` を更新して通常のPR検証を完了し、そのコミットへ同じ版の
`vX.Y.Z` タグを付けます。`.github/workflows/release.yml` はタグと唯一の版情報が一致することを全jobで確認し、
.NET 8／10、Windows／Linux／macOSを再検証してからNuGetツールと4 RIDの自己完結型アーカイブを作成します。
公開jobはGitHub Environment `release` に置き、Environmentへ `NUGET_API_KEY` secretと必要な承認者を設定します。
成果物の検証が完了するまでNuGet pushとGitHub Release公開は行われません。再実行時は同一版のNuGetを
skip-duplicateで扱い、draft releaseへ成果物を再添付してから公開します。

## ファイルと品質

ソーステキストは UTF-8 BOM、CRLF、スペースインデントです。実行可能な `.sh` と
`.agents/skills/` 配下は UTF-8（BOMなし）とLF、NuGet が管理する `packages.lock.json` は
UTF-8（BOMなし）とCRLFです。`AGENTS.md`、`.github/copilot-instructions.md`、
`eng/agent-instructions.md` は完全一致させます。共通ハーネスがこれらとローカルパス、秘密情報、
成果物混入、版番号重複を検査します。
